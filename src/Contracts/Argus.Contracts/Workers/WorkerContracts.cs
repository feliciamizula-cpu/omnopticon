namespace Argus.Contracts.Workers;

public sealed record WorkerCapabilityDescriptor(
    string WorkerType,
    IReadOnlyCollection<string> SubscribedAssetTypes,
    IReadOnlyCollection<string> ProducedAssetTypes,
    bool RequiresHttp,
    bool SupportsCheckpoint,
    int MaxConcurrency,
    // Integration-event types this worker consumes. Null = the default recon set
    // (AssetDiscovered, AssetConfirmed, WorkerProcessRequested). The storage worker overrides this
    // to ["AssetProduced"] so only it consumes produced-asset events.
    IReadOnlyCollection<string>? SubscribedEventTypes = null);

public sealed record WorkerRegistrationRequest(
    string WorkerId,
    WorkerCapabilityDescriptor Capability,
    string? Version);

public sealed record WorkerHeartbeatRequest(
    string WorkerId,
    string WorkerType,
    int RunningTasks,
    int MaxConcurrency,
    DateTimeOffset SeenAt);

public sealed record WorkerStatusDto(
    string WorkerId,
    string WorkerType,
    string? Version,
    int RunningTasks,
    int MaxConcurrency,
    DateTimeOffset LastSeenAt,
    bool IsOnline);

public enum WorkerScaleCommandStatus
{
    Pending = 0,
    Applied = 1,
    Failed = 2,
    NoOp = 3,
    Rejected = 4
}

public enum WorkerTypeHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    UnderScaled = 2,
    OverScaled = 3,
    Saturated = 4,
    Stale = 5,
    Paused = 6,
    ScalingPending = 7,
    ScalingFailed = 8
}

public sealed record WorkerTypeScaleSettingsDto(
    string WorkerType,
    string DisplayName,
    string RuntimeMode,
    int DesiredReplicas,
    int MinReplicas,
    int MaxReplicas,
    bool IsPaused,
    string? DeploymentName,
    string? Namespace,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record WorkerTypeSummaryDto(
    string WorkerType,
    string DisplayName,
    string RuntimeMode,
    int OnlineWorkers,
    int OfflineWorkers,
    int DesiredReplicas,
    int MinReplicas,
    int MaxReplicas,
    int RunningTasks,
    int MaxConcurrency,
    double UtilizationPercent,
    bool IsPaused,
    WorkerTypeHealthStatus HealthStatus,
    DateTimeOffset? LastSeenAt,
    Guid? LastScaleCommandId,
    string? LastScaleCommandStatus,
    string? LastScaleMessage,
    IReadOnlyCollection<string> SubscribedAssetTypes,
    IReadOnlyCollection<string> ProducedAssetTypes,
    bool RequiresHttp,
    bool SupportsCheckpoint);

public sealed record WorkersPageSummaryDto(
    int WorkerTypeCount,
    int OnlineWorkers,
    int OfflineWorkers,
    int DesiredReplicas,
    int RunningTasks,
    int MaxConcurrency,
    int SaturatedWorkerTypes,
    int StaleWorkerTypes,
    int PausedWorkerTypes,
    IReadOnlyCollection<WorkerTypeSummaryDto> WorkerTypes);

public sealed record WorkerInstanceDto(
    string WorkerId,
    string WorkerType,
    string? Version,
    int RunningTasks,
    int MaxConcurrency,
    double UtilizationPercent,
    DateTimeOffset LastSeenAt,
    bool IsOnline,
    bool IsStale);

public sealed record UpdateWorkerScaleSettingsRequest(
    int DesiredReplicas,
    int MinReplicas,
    int MaxReplicas,
    bool IsPaused,
    string? DeploymentName,
    string? Namespace);

public sealed record ScaleWorkerTypeRequest(
    int DesiredReplicas,
    string? Reason);

public sealed record WorkerScaleCommandDto(
    Guid CommandId,
    string WorkerType,
    string Action,
    int PreviousDesiredReplicas,
    int RequestedDesiredReplicas,
    int? AppliedReplicas,
    WorkerScaleCommandStatus Status,
    string? Message,
    string? ScalerKind,
    string? Actor,
    DateTimeOffset RequestedAt,
    DateTimeOffset? AppliedAt);
