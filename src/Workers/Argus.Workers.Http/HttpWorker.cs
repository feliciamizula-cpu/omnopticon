using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Argus.BuildingBlocks.RateLimiting;
using Argus.BuildingBlocks.WorkerDistribution;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Argus.Workers.Http;

public sealed class HttpWorker : IEphemeralWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly RoundRobinWorkerDistributor _distributor;
    private readonly ILogger<HttpWorker> _logger;

    public HttpWorker(
        IHttpClientFactory httpClientFactory,
        TokenBucketRateLimiter rateLimiter,
        RoundRobinWorkerDistributor distributor,
        ILogger<HttpWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _distributor = distributor;
        _logger = logger;
    }

    public EphemeralWorkerDescriptor Descriptor { get; } = new(
        WorkerType: "HttpWorker",
        SubscribedEvents: ["AssetDiscovered", "AssetCreated"],
        SubscribedAssetTypes: ["Subdomain", "Ip", "Url"],
        ProducedAssetTypes: ["Url", "HttpResponse"],
        RequiresHttp: true,
        IsSystemWorker: true);

    public async Task<EphemeralWorkerResult> ProcessAsync(
        EphemeralWorkerContext context,
        CancellationToken cancellationToken)
    {
        var asset = context.Asset;
        var host = ExtractHost(asset);

        if (string.IsNullOrEmpty(host))
        {
            return new EphemeralWorkerResult(
                Success: false,
                ProducedAssets: [],
                PublishedEvents: [],
                Error: "Could not extract host from asset");
        }

        var workerId = _distributor.GetNextWorker(host, Descriptor.WorkerType);
        _logger.LogInformation("HttpWorker {WorkerId} processing {AssetType}:{Value}", workerId, asset.Type, asset.Value);

        var bucketKey = $"http:{host.ToLowerInvariant()}";
        var allowed = await _rateLimiter.TryConsumeAsync(bucketKey);

        if (!allowed)
        {
            _logger.LogDebug("HttpWorker {WorkerId} rate limited for {Host}", workerId, host);
            return new EphemeralWorkerResult(
                Success: false,
                ProducedAssets: [],
                PublishedEvents: [],
                Error: $"Rate limited for {host}");
        }

        var producedAssets = new List<WorkerProducedAsset>();
        var publishedEvents = new List<PublishedEvent>();

        var schemes = new[] { "https", "http" };
        bool confirmed = false;

        foreach (var scheme in schemes)
        {
            var probeUrl = $"{scheme}://{host}/";

            try
            {
                var client = _httpClientFactory.CreateClient("http-worker");
                client.Timeout = TimeSpan.FromSeconds(15);

                using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ServiceHealthCheck/1.0)");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

                var urlAsset = new WorkerProducedAsset(
                    AssetType: "Url",
                    Value: probeUrl,
                    Subtype: null,
                    Metadata: new Dictionary<string, string>
                    {
                        ["http.status_code"] = statusCode.ToString(),
                        ["http.content_type"] = contentType,
                        ["url.scheme"] = scheme,
                        ["confirmed"] = "true"
                    },
                    Tags: ["http", "alive", scheme]);

                producedAssets.Add(urlAsset);

                var responseAsset = new WorkerProducedAsset(
                    AssetType: "HttpResponse",
                    Value: $"{probeUrl} {statusCode} {contentType}",
                    Subtype: contentType,
                    Metadata: new Dictionary<string, string>
                    {
                        ["status_code"] = statusCode.ToString(),
                        ["content_type"] = contentType,
                        ["content_length"] = (response.Content.Headers.ContentLength ?? 0).ToString(),
                        ["response.scheme"] = scheme
                    },
                    Tags: ["response", scheme]);

                producedAssets.Add(responseAsset);

                var confirmedEvent = new AssetConfirmed(
                    AssetId: asset.AssetId,
                    ProgramId: asset.ProgramId,
                    AssetType: asset.Type.ToString(),
                    Value: asset.Value,
                    ConfirmedByTaskId: null);

                publishedEvents.Add(new PublishedEvent(
                    EventType: nameof(AssetConfirmed),
                    Payload: confirmedEvent));

                confirmed = true;
                _logger.LogInformation("HttpWorker {WorkerId} confirmed {Host} via {Scheme}", workerId, host, scheme);

                if (scheme == "https" && statusCode < 400)
                {
                    break;
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HttpWorker {WorkerId} failed to probe {Scheme}://{Host}", workerId, scheme, host);
            }
        }

        if (!confirmed)
        {
            return new EphemeralWorkerResult(
                Success: false,
                ProducedAssets: producedAssets,
                PublishedEvents: publishedEvents,
                Error: $"Could not confirm {host} via HTTP/HTTPS");
        }

        return new EphemeralWorkerResult(
            Success: true,
            ProducedAssets: producedAssets,
            PublishedEvents: publishedEvents);
    }

    private static string? ExtractHost(AssetDto asset)
    {
        return asset.Type switch
        {
            AssetType.Subdomain or AssetType.Domain => asset.Value,
            AssetType.Ip => asset.Value,
            AssetType.Url => Uri.TryCreate(asset.Value, UriKind.Absolute, out var uri) ? uri.Host : null,
            _ => null
        };
    }
}
