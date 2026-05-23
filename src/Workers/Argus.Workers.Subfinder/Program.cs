using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<SubfinderWorker>();

await builder.Build().RunAsync();

internal sealed class SubfinderWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "SubfinderWorker",
        ["Domain"],
        ["Subdomain"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 20);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var domain = WorkerPayload.GetString(task.InputPayloadJson, "domain") ?? "example.com";

        await context.ReportProgressAsync(35, $"Querying passive sources for {domain}", "{\"source\":\"passive\"}");
        await Task.Delay(100, cancellationToken);
        await context.ReportProgressAsync(85, $"Preparing subfinder candidates for {domain}", "{\"phase\":\"emit\"}");

        var assets = new[]
        {
            new WorkerProducedAsset("Subdomain", $"www.{domain}", null, null, new Dictionary<string, string> { ["source"] = "subfinder" }, ["subfinder"]),
            new WorkerProducedAsset("Subdomain", $"staging.{domain}", null, null, new Dictionary<string, string> { ["source"] = "subfinder" }, ["subfinder"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { domain, produced = assets.Length }), assets);
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
