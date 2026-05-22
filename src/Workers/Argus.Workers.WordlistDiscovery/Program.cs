using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<WordlistDiscoveryWorker>();

await builder.Build().RunAsync();

internal sealed class WordlistDiscoveryWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "WordlistDiscoveryWorker",
        ["Url"],
        ["Url"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 15);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = WorkerPayload.GetString(task.InputPayloadJson, "url") ?? "https://example.com/";
        var host = new Uri(baseUrl).Host;

        await context.ReportProgressAsync(15, $"Checking wordlist quota for {host}", "{\"word\":\"admin\"}");
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerPayload.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { baseUrl, delayed = true }), []);
        }

        await context.ReportProgressAsync(75, $"Testing high-signal paths on {host}", "{\"word\":\"health\"}");
        await Task.Delay(100, cancellationToken);

        var baseUri = new Uri(baseUrl);
        var assets = new[]
        {
            new WorkerProducedAsset("Url", new Uri(baseUri, "/admin").ToString(), "AdminRoute", new Dictionary<string, string> { ["source"] = "wordlist" }, ["interesting"]),
            new WorkerProducedAsset("Url", new Uri(baseUri, "/health").ToString(), null, new Dictionary<string, string> { ["source"] = "wordlist" }, ["probe"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { baseUrl, produced = assets.Length }), assets);
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
