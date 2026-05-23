using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<FingerprintWorker>();

await builder.Build().RunAsync();

internal sealed class FingerprintWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "FingerprintWorker",
        ["Url", "HttpResponse", "HtmlPage"],
        ["Technology"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 50);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var target = WorkerPayload.GetString(task.InputPayloadJson, "url")
            ?? WorkerPayload.GetString(task.InputPayloadJson, "host")
            ?? "unknown";

        await context.ReportProgressAsync(60, $"Fingerprinting {target}", null);
        await Task.Delay(75, cancellationToken);

        var assets = new[]
        {
            new WorkerProducedAsset("Technology", "nginx", "Server", null, new Dictionary<string, string> { ["target"] = target }, ["tech"]),
            new WorkerProducedAsset("Technology", "react", "FrontendFramework", null, new Dictionary<string, string> { ["target"] = target }, ["tech"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { target, produced = assets.Length }), assets);
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
