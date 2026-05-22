using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HtmlDomSpiderWorker>();

await builder.Build().RunAsync();

internal sealed class HtmlDomSpiderWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HtmlDomSpiderWorker",
        ["Url", "HtmlPage"],
        ["Url", "ApiEndpoint", "JavaScriptFile"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 25);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = WorkerPayload.GetString(task.InputPayloadJson, "url") ?? "https://example.com/";
        var host = new Uri(url).Host;

        await context.ReportProgressAsync(10, $"Checking crawl quota for {host}", null);
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerPayload.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { url, delayed = true }), []);
        }

        await context.ReportProgressAsync(55, $"Extracting DOM links from {url}", "{\"depth\":0}");
        await Task.Delay(100, cancellationToken);

        var baseUri = new Uri(url);
        var assets = new[]
        {
            new WorkerProducedAsset("Url", new Uri(baseUri, "/login").ToString(), "LoginPage", new Dictionary<string, string> { ["source"] = "html" }, ["link"]),
            new WorkerProducedAsset("Url", new Uri(baseUri, "/admin").ToString(), "AdminRoute", new Dictionary<string, string> { ["source"] = "html" }, ["interesting"]),
            new WorkerProducedAsset("JavaScriptFile", new Uri(baseUri, "/static/app.js").ToString(), null, new Dictionary<string, string> { ["source"] = "script-tag" }, ["js"]),
            new WorkerProducedAsset("ApiEndpoint", new Uri(baseUri, "/api/v1/status").ToString(), "REST", new Dictionary<string, string> { ["source"] = "html" }, ["api"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { url, produced = assets.Length }), assets);
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
