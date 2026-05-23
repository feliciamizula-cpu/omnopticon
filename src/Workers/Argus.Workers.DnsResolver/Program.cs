using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<DnsResolverWorker>();

await builder.Build().RunAsync();

internal sealed class DnsResolverWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "DnsResolverWorker",
        ["Domain", "Subdomain"],
        ["Ip", "DnsRecord"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 50);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var host = WorkerPayload.GetString(task.InputPayloadJson, "host")
            ?? WorkerPayload.GetString(task.InputPayloadJson, "domain")
            ?? "example.com";

        await context.ReportProgressAsync(50, $"Resolving {host}", null);
        await Task.Delay(75, cancellationToken);

        var assets = new[]
        {
            new WorkerProducedAsset("Ip", "203.0.113.10", null, null, new Dictionary<string, string> { ["host"] = host }, ["reserved"]),
            new WorkerProducedAsset("DnsRecord", $"{host} A 203.0.113.10", "A", null, new Dictionary<string, string> { ["host"] = host }, ["dns"])
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { host, produced = assets.Length }), assets);
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
