using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AssetScoringWorker>();

await builder.Build().RunAsync();

internal sealed class AssetScoringWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "AssetScoringWorker",
        ["Domain", "Subdomain", "Url", "ApiEndpoint", "JavaScriptFile", "FindingCandidate", "Technology"],
        ["FindingCandidate"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 100);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var value = WorkerPayload.GetString(task.InputPayloadJson, "value")
            ?? WorkerPayload.GetString(task.InputPayloadJson, "url")
            ?? WorkerPayload.GetString(task.InputPayloadJson, "domain")
            ?? "unknown";

        await context.ReportProgressAsync(80, $"Scoring {value}", null);
        await Task.Delay(50, cancellationToken);

        var produced = value.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("internal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("graphql", StringComparison.OrdinalIgnoreCase)
            ? [new WorkerProducedAsset("FindingCandidate", $"interesting asset: {value}", "InterestingAsset", new Dictionary<string, string> { ["source"] = "asset-scoring" }, ["score"])]
            : Array.Empty<WorkerProducedAsset>();

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { value, produced = produced.Length }), produced);
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
}
