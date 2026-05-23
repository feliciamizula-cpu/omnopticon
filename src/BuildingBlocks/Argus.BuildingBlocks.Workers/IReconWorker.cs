using Argus.BuildingBlocks.Artifacts;
using Argus.Contracts.Workers;
using Argus.Contracts.Tasks;

namespace Argus.BuildingBlocks.Workers;

public interface IReconWorker
{
    WorkerCapabilityDescriptor Capability { get; }

    Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record WorkerExecutionContext(
    string WorkerId,
    Guid TaskId,
    bool SupportsCheckpoint,
    Func<int, string, string?, Task> ReportProgressAsync,
    Func<RateLimitRequest, Task<bool>> RequestRateLimitTokenAsync,
    Func<RateLimitBackpressureSignal, Task> SignalBackpressureAsync,
    Func<string, Task>? SaveCheckpointAsync = null)
{
    public async Task<string> SaveCheckpointAsync(string checkpointJson)
    {
        if (SaveCheckpointAsync != null)
        {
            await SaveCheckpointAsync(checkpointJson);
        }
        return checkpointJson;
    }
}

public sealed record RateLimitBackpressureSignal(
    string Host,
    string BucketKey,
    TimeSpan RetryAfter,
    int ObservedStatusCode);

public sealed record RateLimitRequest(
    Guid ProgramId,
    Guid? ScopeId,
    string? Host,
    string? RegisteredDomain,
    string? Ip,
    string WorkerType,
    string? ProxyId = null,
    int PermitCount = 1);

public sealed record WorkerProcessResult(
    bool PartiallySucceeded,
    string OutputSummaryJson,
    IReadOnlyCollection<WorkerProducedAsset> ProducedAssets,
    IReadOnlyCollection<WorkerProducedArtifact>? ProducedArtifacts = null,
    TimeSpan? RetryAfter = null)
{
    public static WorkerProcessResult Empty(string summary) => new(false, summary, [], null, null);
}

public sealed record WorkerProducedAsset(
    string AssetType,
    string Value,
    string? Subtype,
    decimal? Confidence,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyCollection<ArtifactReference>? ArtifactReferences = null)
{
    public WorkerProducedAsset(
        string assetType,
        string value,
        string? subtype,
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyCollection<string>? tags,
        IReadOnlyCollection<ArtifactReference>? artifactReferences = null)
        : this(assetType, value, subtype, null, metadata, tags, artifactReferences)
    {
    }
}

public sealed record WorkerProducedArtifact(
    string ArtifactType,
    string Name,
    string ContentType,
    byte[] Data,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public string ComputeHash()
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class WorkerTimeoutException : Exception
{
    public Guid TaskId { get; }
    public TimeSpan Timeout { get; }
    public bool IsRetryable { get; }

    public WorkerTimeoutException(Guid taskId, TimeSpan timeout, bool isRetryable = true)
        : base($"Task {taskId} timed out after {timeout.TotalSeconds:F1} seconds")
    {
        TaskId = taskId;
        Timeout = timeout;
        IsRetryable = isRetryable;
    }
}

public sealed class WorkerShutdownException : Exception
{
    public WorkerShutdownException(string message) : base(message)
    {
    }
}

internal sealed record TaskRunState(
    CancellationTokenSource CancellationSource,
    string? CheckpointJson);
