namespace Argus.Contracts.Tasks;

public enum WorkerPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum ReconTaskState
{
    Requested,
    Queued,
    Leased,
    Running,
    HeartbeatLost,
    RetryPending,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled,
    Expired
}

public sealed record ReconTaskDto(
    Guid TaskId,
    string TaskType,
    Guid ProgramId,
    Guid? ScopeId,
    Guid? InputAssetId,
    string? InputPayloadJson,
    string WorkerCapability,
    string? RequiredAssetType,
    ReconTaskState State,
    int Attempt,
    int MaxAttempts,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int ProgressPercent,
    string? ProgressMessage,
    string? CheckpointJson,
    string? OutputSummaryJson,
    string? ErrorCode,
    string? ErrorMessage,
    string? DedupeHash,
    WorkerPriority Priority = WorkerPriority.Normal);

public sealed record CreateReconTaskRequest(
    string TaskType,
    Guid ProgramId,
    Guid? ScopeId,
    Guid? InputAssetId,
    string? InputPayloadJson,
    string WorkerCapability,
    string? RequiredAssetType,
    int MaxAttempts = 3,
    WorkerPriority Priority = WorkerPriority.Normal,
    string? DedupeHash = null);

public sealed record LeaseReconTaskRequest(
    string WorkerId,
    string WorkerCapability,
    string? RequiredAssetType,
    TimeSpan LeaseDuration);

public sealed record UpdateReconTaskProgressRequest(
    int ProgressPercent,
    string ProgressMessage,
    string? CheckpointJson);

public sealed record CompleteReconTaskRequest(
    bool PartiallySucceeded,
    string? OutputSummaryJson);

public sealed record FailReconTaskRequest(
    string ErrorCode,
    string ErrorMessage,
    bool Retryable,
    string? CheckpointJson);

public sealed record CancelTaskRequest(
    string Reason);

public sealed record TaskHistoryDto(
    Guid HistoryId,
    Guid TaskId,
    ReconTaskState State,
    DateTimeOffset Timestamp,
    string? WorkerId,
    string? Message,
    string? CheckpointSummary);

public sealed record TaskDedupeKey(
    Guid ProgramId,
    Guid? ScopeId,
    string TaskType,
    Guid? InputAssetId,
    string PayloadHash);
