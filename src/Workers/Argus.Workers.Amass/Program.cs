using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AmassWorker>();

await builder.Build().RunAsync();

internal sealed class AmassWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "AmassWorker",
        ["Domain"],
        ["Subdomain", "DnsRecord"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 10);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var domain = WorkerPayload.GetString(task.InputPayloadJson, "domain") ?? "example.com";

        await context.ReportProgressAsync(25, $"Starting passive enumeration for {domain}", null);
        await Task.Delay(100, cancellationToken);
        await context.ReportProgressAsync(70, $"Normalizing amass output for {domain}", "{\"phase\":\"normalize\"}");

        var assets = new[]
        {
            new WorkerProducedAsset("Subdomain", $"api.{domain}", null, null, new Dictionary<string, string> { ["source"] = "amass" }, ["amass"]),
            new WorkerProducedAsset("Subdomain", $"dev.{domain}", null, null, new Dictionary<string, string> { ["source"] = "amass" }, ["amass"]),
            new WorkerProducedAsset("DnsRecord", $"ns1.{domain}", "NS", null, new Dictionary<string, string> { ["source"] = "amass" }, ["dns"])
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
