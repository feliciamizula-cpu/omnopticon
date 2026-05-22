namespace Argus.Contracts.Workers;

public sealed record WorkerCapabilityDescriptor(
    string WorkerType,
    IReadOnlyCollection<string> SubscribedAssetTypes,
    IReadOnlyCollection<string> ProducedAssetTypes,
    bool RequiresHttp,
    bool SupportsCheckpoint,
    int MaxConcurrency);

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
