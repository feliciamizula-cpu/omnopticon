using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HeadlessSpiderWorker>();

await builder.Build().RunAsync();

internal sealed class HeadlessSpiderWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HeadlessSpiderWorker",
        ["Url"],
        ["Url", "ApiEndpoint", "JavaScriptFile"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 10);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = WorkerPayload.GetString(task.InputPayloadJson, "url") ?? "https://example.com/";
        var host = new Uri(url).Host;

        await context.ReportProgressAsync(10, $"Checking browser quota for {host}", "{\"stage\":\"quota\"}");
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

        await context.ReportProgressAsync(55, $"Rendering SPA route {url}", "{\"stage\":\"render\"}");
        await Task.Delay(125, cancellationToken);

        var baseUri = new Uri(url);
        var assets = new[]
        {
            new WorkerProducedAsset("Url", new Uri(baseUri, "/dashboard").ToString(), "RenderedRoute", null, new Dictionary<string, string> { ["source"] = "headless" }, Tags: null, ArtifactReferences: null),
            new WorkerProducedAsset("ApiEndpoint", new Uri(baseUri, "/api/bootstrap").ToString(), "REST", null, new Dictionary<string, string> { ["source"] = "network-capture" }, Tags: null, ArtifactReferences: null),
            new WorkerProducedAsset("JavaScriptFile", new Uri(baseUri, "/assets/runtime.js").ToString(), null, null, new Dictionary<string, string> { ["source"] = "network-capture" }, Tags: null, ArtifactReferences: null)
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
