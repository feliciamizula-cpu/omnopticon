namespace Argus.Contracts.Events;

public sealed record IntegrationEventEnvelope<T>
{
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid CausationId { get; init; }
    public required string SourceService { get; init; }
    public required T Payload { get; init; }

    public static IntegrationEventEnvelope<T> Create(
        T payload,
        string eventType,
        string sourceService,
        Guid? correlationId = null,
        Guid? causationId = null)
    {
        var eventId = Guid.NewGuid();

        return new IntegrationEventEnvelope<T>
        {
            EventId = eventId,
            EventType = eventType,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId ?? eventId,
            CausationId = causationId ?? eventId,
            SourceService = sourceService,
            Payload = payload
        };
    }
}

public sealed record ProgramCreated(Guid ProgramId, string Name);
public sealed record ScopeCreated(Guid ProgramId, Guid ScopeId, string Pattern, string ScopeType);
public sealed record AssetDiscovered(Guid AssetId, Guid ProgramId, string AssetType, string Value);
public sealed record AssetRelationshipDiscovered(Guid FromAssetId, Guid ToAssetId, string EdgeType);
public sealed record TaskRequested(Guid TaskId, string TaskType, Guid ProgramId, Guid? InputAssetId);
public sealed record TaskStarted(Guid TaskId, string WorkerId, DateTimeOffset StartedAt);
public sealed record TaskProgressed(Guid TaskId, int ProgressPercent, string ProgressMessage);
public sealed record TaskCompleted(Guid TaskId, string OutputSummary);
public sealed record TaskFailed(Guid TaskId, string ErrorCode, string ErrorMessage);
public sealed record WorkerHeartbeat(Guid WorkerId, string WorkerType, DateTimeOffset SeenAt);
public sealed record RateLimitTokenGranted(string BucketKey, Guid TokenId, DateTimeOffset ExpiresAt);
public sealed record RateLimitDelayed(string BucketKey, TimeSpan RetryAfter);
