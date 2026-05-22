using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HttpProbeWorker>();

await builder.Build().RunAsync();

internal sealed class HttpProbeWorker : IReconWorker
{
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
        var host = WorkerPayload.GetString(task.InputPayloadJson, "host")
            ?? WorkerPayload.GetString(task.InputPayloadJson, "domain")
            ?? "example.com";

        await context.ReportProgressAsync(15, $"Waiting for rate-limit token for {host}", null);

        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerPayload.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, delayed = true }), []);
        }

        await context.ReportProgressAsync(60, $"Probing HTTPS for {host}", "{\"scheme\":\"https\"}");
        await Task.Delay(100, cancellationToken);

        var assets = new[]
        {
            new WorkerProducedAsset("Url", $"https://{host}/", null, new Dictionary<string, string> { ["http.status_code"] = "200" }, ["http"]),
            new WorkerProducedAsset("HttpResponse", $"https://{host}/ 200 text/html", "text/html", new Dictionary<string, string> { ["status_code"] = "200", ["content_type"] = "text/html" }, ["response"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { host, statusCode = 200, produced = assets.Length }), assets);
    }
}

internal static class WorkerPayload
{
    public static string? GetString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    public static string GetRegisteredDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? host : string.Join('.', parts[^2..]);
    }
}
