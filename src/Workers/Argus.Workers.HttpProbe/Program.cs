using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HttpProbeWorker>();

await builder.Build().RunAsync();

internal sealed class HttpProbeWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpProbeWorker(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HttpProbeWorker",
        ["Subdomain", "Ip"],
        ["Url", "HttpResponse"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 40);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var host = WorkerHelpers.GetString(task.InputPayloadJson, "host")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "domain")
            ?? throw new InvalidOperationException("No host in task payload");

        var useHttps = true;
        var probeUrl = $"{(useHttps ? "https" : "http")}://{host}/";

        await context.ReportProgressAsync(10, $"Waiting for rate-limit token for {host}", null);

        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerHelpers.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            await context.ReportProgressAsync(100, "Rate limited, will retry", null);
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, delayed = true }), []);
        }

        await context.ReportProgressAsync(30, $"Probing {probeUrl}", "{\"scheme\":\"https\"}");

        var client = _httpClientFactory.CreateClient("probe");
        client.Timeout = TimeSpan.FromSeconds(30);

        var producedAssets = new List<WorkerProducedAsset>();
        string outputSummary;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            request.Headers.UserAgent.ParseAdd("ArgusRecon/1.0 (bug-bounty-recon)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,*/*");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

            await context.ReportProgressAsync(70, $"Received {statusCode} from {host}", null);

            producedAssets.Add(new WorkerProducedAsset(
                "Url",
                probeUrl,
                null,
                new Dictionary<string, string>
                {
                    ["http.status_code"] = statusCode.ToString(),
                    ["http.content_type"] = contentType,
                    ["http.redirects"] = "0"
                },
                ["http", statusCode >= 200 && statusCode < 400 ? "alive" : "error"]));

            producedAssets.Add(new WorkerProducedAsset(
                "HttpResponse",
                $"{probeUrl} {statusCode} {contentType}",
                contentType,
                new Dictionary<string, string>
                {
                    ["status_code"] = statusCode.ToString(),
                    ["content_type"] = contentType,
                    ["content_length"] = (response.Content.Headers.ContentLength ?? 0).ToString()
                },
                ["response"]));

            outputSummary = JsonSerializer.Serialize(new { host, statusCode, contentType });
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            await context.ReportProgressAsync(100, $"Probe failed: {ex.Message}", null);
            producedAssets.Add(new WorkerProducedAsset(
                "HttpResponse",
                $"{probeUrl} error {ex.Message}",
                "error",
                new Dictionary<string, string> { ["error"] = ex.Message },
                ["http", "error"]));
            outputSummary = JsonSerializer.Serialize(new { host, error = ex.Message });
        }
        catch (Exception ex)
        {
            await context.ReportProgressAsync(100, $"Unexpected error: {ex.Message}", null);
            outputSummary = JsonSerializer.Serialize(new { host, error = ex.GetType().Name });
        }

        await context.ReportProgressAsync(100, "Complete", null);

        return new WorkerProcessResult(false, outputSummary, producedAssets);
    }
}