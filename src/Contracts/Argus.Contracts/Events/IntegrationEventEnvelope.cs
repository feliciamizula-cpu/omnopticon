namespace Argus.Contracts.Events;

public sealed record IntegrationEventEnvelope<T>
{
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid CausationId { get; init; }
    public required string SourceService { get; init; }
    public required int SchemaVersion { get; init; } = 1;
    public required T Payload { get; init; }

    public static IntegrationEventEnvelope<T> Create(
        T payload,
        string eventType,
        string sourceService,
        int schemaVersion = 1,
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
            SchemaVersion = schemaVersion,
            Payload = payload
        };
    }

    public IntegrationEventEnvelope<T> WithEventId(Guid eventId) => this with { EventId = eventId };
}

public sealed record ProgramCreated(Guid ProgramId, string Name);
public sealed record ScopeCreated(Guid ProgramId, Guid ScopeId, string Pattern, string ScopeType);
public sealed record AssetDiscovered(Guid AssetId, Guid ProgramId, string AssetType, string Value);
public sealed record AssetCreated(Guid AssetId, Guid ProgramId, string AssetType, string Value, string? CreatedByWorkerId);
public sealed record FindingCandidateCreated(Guid AssetId, Guid ProgramId, string AssetType, string Value, int InterestingScore);
public sealed record AssetConfirmed(Guid AssetId, Guid ProgramId, string AssetType, string Value, Guid? ConfirmedByTaskId);
public sealed record AssetUpdated(Guid AssetId, Guid ProgramId, string AssetType, string Value);
public sealed record AssetRelationshipDiscovered(Guid FromAssetId, Guid ToAssetId, string EdgeType);
// Manual re-trigger: ask a specific worker type to (re)process an existing asset. Fully event-driven, no tasks.
public sealed record WorkerProcessRequested(Guid AssetId, Guid ProgramId, string AssetType, string Value, string TargetWorkerType);
// A recon worker found an asset but did NOT persist it. The storage worker is the sole consumer and
// sole writer to asset-service; it persists and then asset-service emits AssetDiscovered as usual.
public sealed record AssetProduced(
    Guid ProgramId,
    Guid? ScopeId,
    Guid? InputAssetId,
    string AssetType,
    string Value,
    string? Subtype,
    decimal? Confidence,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyCollection<string>? Tags,
    string SourceWorkerType,
    string SourceTaskId);
public sealed record TaskRequested(Guid TaskId, string TaskType, Guid ProgramId, Guid? InputAssetId);
public sealed record TaskLeased(Guid TaskId, string WorkerId, DateTimeOffset LeaseExpiresAt);
public sealed record TaskStarted(Guid TaskId, string WorkerId, DateTimeOffset StartedAt);
public sealed record TaskProgressed(Guid TaskId, int ProgressPercent, string ProgressMessage);
public sealed record TaskCompleted(Guid TaskId, Guid? InputAssetId, string OutputSummary);
public sealed record TaskFailed(Guid TaskId, string ErrorCode, string ErrorMessage);
public sealed record WorkerHeartbeat(Guid WorkerId, string WorkerType, DateTimeOffset SeenAt);
public sealed record RateLimitTokenGranted(string BucketKey, Guid TokenId, DateTimeOffset ExpiresAt);
public sealed record RateLimitDelayed(string BucketKey, TimeSpan RetryAfter);
public sealed record RateLimitBackpressureSignaled(string Host, string BucketKey, TimeSpan RetryAfter, int ObservedStatusCode);
public sealed record ProgramScopeChanged(Guid ProgramId, Guid ScopeId, string ChangeType);
public sealed record AssetPropertyChanged(
    Guid AssetId,
    Guid ProgramId,
    string AssetType,
    string Value,
    IReadOnlyDictionary<string, string> PreviousMetadata,
    IReadOnlyDictionary<string, string> NewMetadata,
    IReadOnlyCollection<string> ChangedKeys);

public sealed record ProxyAdded(Guid ProxyId, string Url, string Protocol);
public sealed record ProxyRemoved(Guid ProxyId, string Url);
public sealed record ProxyStatusChanged(Guid ProxyId, string Url, string OldStatus, string NewStatus);
public sealed record ProxyRateLimitExceeded(Guid ProxyId, string Url, int CurrentRequestsPerSecond);
public sealed record ArtifactCreated(Guid ArtifactId, Guid TargetId, string ArtifactType, string ContentType);
public sealed record EvidenceAdded(Guid FindingId, Guid ArtifactId);
public sealed record FindingCreated(Guid FindingId, Guid TargetId, string Title, string Severity);
public sealed record FindingUpdated(Guid FindingId, string Status);
public sealed record FindingTriaged(Guid FindingId, string OldStatus, string NewStatus, string Reason);
