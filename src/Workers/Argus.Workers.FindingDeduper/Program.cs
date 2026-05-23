using Argus.BuildingBlocks.EventBus;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Events;
using Argus.Contracts.Findings;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<FindingDeduperWorker>();

await builder.Build().RunAsync();

internal sealed class FindingDeduperWorker : IReconWorker, IIntegrationEventConsumer<ObservationCreated>
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "FindingDeduperWorker",
        ["Observation", "Domain", "Subdomain", "Url", "ApiEndpoint"],
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
        var targetId = GetPayloadGuid(task.InputPayloadJson, "targetId");
        var programId = task.ProgramId;
        var sourceTaskId = task.TaskId;

        await context.ReportProgressAsync(20, $"Analyzing observation: {observationValue}", null);

        var isHighConfidence = score >= 60;

        if (!isHighConfidence)
        {
            await context.ReportProgressAsync(100, $"Low confidence observation ({score}), skipping finding creation", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { skipped = true, reason = "low-confidence", score }));
        }

        await context.ReportProgressAsync(50, $"High confidence observation passed dedup checks", null);

        var dedupeKey = ComputeDedupeKey(signal, observationValue);

        var findingCandidate = new WorkerProducedAsset(
            AssetType: "FindingCandidate",
            Value: $"{signal}:{observationValue}",
            Subtype: observationSubtype,
            Confidence: score / 100m,
            Metadata: new Dictionary<string, string>
            {
                ["signal"] = signal,
                ["sourceTaskId"] = sourceTaskId.ToString(),
                ["targetId"] = targetId?.ToString() ?? "",
                ["programId"] = programId.ToString(),
                ["confidence"] = score.ToString(),
                ["dedupKey"] = dedupeKey,
                ["observationType"] = observationSubtype
            },
            Tags: ["finding-candidate", "deduped", signal]);

        await context.ReportProgressAsync(80, $"Produced finding candidate: {observationValue}", null);

        return new WorkerProcessResult(
            PartiallySucceeded: false,
            OutputSummaryJson: JsonSerializer.Serialize(new
            {
                created = true,
                value = observationValue,
                signal,
                score,
                dedupeKey
            }),
            ProducedAssets: [findingCandidate]);
    }

    public async Task HandleAsync(IntegrationEventEnvelope<ObservationCreated> envelope, CancellationToken cancellationToken)
    {
        var observation = envelope.Payload;
        if (observation.Score >= 60)
        {
            await ProcessHighValueObservationAsync(observation, cancellationToken);
        }
    }

    private Task ProcessHighValueObservationAsync(ObservationCreated observation, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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

    private static Guid? GetPayloadGuid(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                return Guid.TryParse(value.GetString(), out var guid) ? guid : null;
        }
        catch { }
        return null;
    }
}

public sealed record ObservationCreated(Guid ObservationId, Guid AssetId, Guid TargetId, Guid ProgramId, string ObservationType, string Value, string Signal, int Score, Guid? SourceTaskId);