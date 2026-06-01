using System.Net.Http.Json;
using System.Text.Json;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Workers.Http;

/// <summary>
/// Fetches a host/URL over HTTP(S), confirms reachability, and produces Url + HttpResponse assets.
/// Runs on the recon worker framework: consumes asset events, emits AssetProduced (host persists via
/// the storage worker), and confirms its input asset.
/// </summary>
public sealed class HttpWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _assetServiceBaseAddress;
    private readonly ILogger<HttpWorker> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HttpWorker(
        IHttpClientFactory httpClientFactory,
        IOptions<ArgusWorkerOptions> options,
        ILogger<HttpWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _assetServiceBaseAddress = options.Value.AssetServiceBaseAddress;
        _logger = logger;
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        WorkerType: "HttpWorker",
        SubscribedAssetTypes: ["Subdomain", "Ip", "Url"],
        ProducedAssetTypes: ["Url", "HttpResponse"],
        RequiresHttp: true,
        SupportsCheckpoint: false,
        MaxConcurrency: 20);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var value = WorkerHelpers.GetString(task.InputPayloadJson, "value")
                    ?? WorkerHelpers.GetString(task.InputPayloadJson, "url");
        var host = ExtractHost(value, task.RequiredAssetType);
        if (string.IsNullOrEmpty(host))
            return WorkerProcessResult.Empty("Could not extract host.");

        // Rate-limit per host/registered-domain via the shared rate-limit service.
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId, task.ScopeId, host, WorkerHelpers.GetRegisteredDomain(host), null, Capability.WorkerType));
        if (!allowed)
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, delayed = true }), []);

        var produced = new List<WorkerProducedAsset>();
        var confirmed = false;

        foreach (var scheme in new[] { "https", "http" })
        {
            var probeUrl = $"{scheme}://{host}/";
            try
            {
                await context.ReportProgressAsync(40, $"Requesting {probeUrl}", null);
                var client = _httpClientFactory.CreateClient("http-worker");
                client.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ArgusHttp/1.0)");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var status = (int)response.StatusCode;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

                produced.Add(new WorkerProducedAsset(
                    AssetType: "Url",
                    Value: probeUrl,
                    Metadata: new Dictionary<string, string>
                    {
                        ["http.status_code"] = status.ToString(),
                        ["http.content_type"] = contentType,
                        ["url.scheme"] = scheme,
                        ["confirmed"] = "true"
                    },
                    Tags: ["http", "alive", scheme]));

                produced.Add(new WorkerProducedAsset(
                    AssetType: "HttpResponse",
                    Value: $"{probeUrl} {status} {contentType}",
                    Subtype: contentType,
                    Metadata: new Dictionary<string, string>
                    {
                        ["status_code"] = status.ToString(),
                        ["content_type"] = contentType,
                        ["content_length"] = (response.Content.Headers.ContentLength ?? 0).ToString(),
                        ["response.scheme"] = scheme
                    },
                    Tags: ["response", scheme]));

                confirmed = true;
                _logger.LogInformation("HttpWorker confirmed {Host} via {Scheme} ({Status})", host, scheme, status);

                if (scheme == "https" && status < 400) break;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HttpWorker failed to probe {Scheme}://{Host}", scheme, host);
            }
        }

        // Confirm the input asset is reachable so downstream (confirmed) workers proceed.
        if (confirmed && task.InputAssetId is { } inputId && inputId != Guid.Empty)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.BaseAddress = _assetServiceBaseAddress;
                using var resp = await client.PostAsJsonAsync($"/assets/{inputId}/confirm", new { }, JsonOptions, cancellationToken);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "HttpWorker confirm failed for {AssetId}", inputId); }
        }

        await context.ReportProgressAsync(100, confirmed ? $"Confirmed {host}" : $"Unreachable {host}", null);
        return new WorkerProcessResult(!confirmed, JsonSerializer.Serialize(new { host, confirmed, produced = produced.Count }), produced);
    }

    private static string? ExtractHost(string? value, string? assetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(assetType, "Url", StringComparison.OrdinalIgnoreCase) || value.Contains("://"))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null;
        return value; // Subdomain / Ip / Domain are already hosts
    }
}
