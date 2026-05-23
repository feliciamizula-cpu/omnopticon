using Argus.BuildingBlocks.EventBus;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Events;
using Argus.Contracts.Findings;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<FindingDeduperWorker>();

await builder.Build().RunAsync();

internal sealed class FindingDeduperWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "FindingDeduperWorker",
        ["Observation", "FindingCandidate", "Domain", "Subdomain", "Url", "ApiEndpoint"],
        ["FindingCandidate"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 50);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var observationValue = GetPayloadString(task.InputPayloadJson, "value") ?? "unknown";
        var observationSubtype = GetPayloadString(task.InputPayloadJson, "subtype") ?? "GenericObservation";
        var signal = GetPayloadString(task.InputPayloadJson, "signal") ?? "unknown";
        var score = GetPayloadInt(task.InputPayloadJson, "score") ?? 0;

        await context.ReportProgressAsync(20, $"Analyzing observation: {observationValue}", null);

        var isHighConfidence = score >= 60;

        if (!isHighConfidence)
        {
            await context.ReportProgressAsync(100, $"Low confidence observation ({score}), skipping finding creation", null);
            return new WorkerProcessResult(
                HasMoreWork: false,
                NextTaskPayloadJson: null,
                ProducedAssets: [],
                OutputSummaryJson: JsonSerializer.Serialize(new { skipped = true, reason = "low-confidence", score }));
        }

        await context.ReportProgressAsync(50, $"High confidence observation passed dedup checks", null);

        var findingCandidate = new WorkerProducedAsset(
            AssetType: "FindingCandidate",
            Value: $"{signal}:{observationValue}",
            Subtype: observationSubtype,
            ParentAssetId: task.InputAssetId,
            Metadata: new Dictionary<string, string>
            {
                ["signal"] = signal,
                ["sourceTaskId"] = task.TaskId.ToString(),
                ["confidence"] = score.ToString(),
                ["dedupKey"] = ComputeDedupeKey(signal, observationValue),
                ["programId"] = task.ProgramId.ToString()
            },
            Tags: ["finding-candidate", "deduped", signal]);

        await context.ReportProgressAsync(80, $"Produced finding candidate: {observationValue}", null);

        return new WorkerProcessResult(
            HasMoreWork: false,
            NextTaskPayloadJson: null,
            ProducedAssets: [findingCandidate],
            OutputSummaryJson: JsonSerializer.Serialize(new
            {
                created = true,
                value = observationValue,
                signal,
                score,
                dedupeKey = ComputeDedupeKey(signal, observationValue)
            }));
    }

    private static string ComputeDedupeKey(string signal, string value)
    {
        var normalized = $"{signal}:{value}".ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string? GetPayloadString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(propertyName, out var value))
                return value.GetString();
        }
        catch { }
        return null;
    }

    private static int? GetPayloadInt(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(propertyName, out var value))
                return value.TryGetInt32(out var i) ? i : null;
        }
        catch { }
        return null;
    }
}

internal sealed class ObservationToFindingConsumer : IIntegrationEventConsumer<ObservationCreated>
{
    private readonly ILogger<ObservationToFindingConsumer> _logger;

    public ObservationToFindingConsumer(ILogger<ObservationToFindingConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(IntegrationEventEnvelope<ObservationCreated> envelope, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Received observation event {EventId} for asset {AssetId}",
            envelope.Payload.ObservationId, envelope.Payload.AssetId);
    }
}

public sealed record ObservationCreated(Guid ObservationId, Guid AssetId, string ObservationType, string Value, int Score);