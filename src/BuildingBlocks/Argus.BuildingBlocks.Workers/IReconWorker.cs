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

public sealed partial record WorkerExecutionContext(
    string WorkerId,
    Func<int, string, string?, Task> ReportProgressAsync,
    Func<RateLimitRequest, Task<bool>> RequestRateLimitTokenAsync,
    Func<RateLimitBackpressureSignal, Task> SignalBackpressureAsync);

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
    TimeSpan? RetryAfter = null)
{
    public IReadOnlyCollection<WorkerProducedArtifact> ProducedArtifacts { get; init; } = Array.Empty<WorkerProducedArtifact>();
    public static WorkerProcessResult Empty(string summary) => new(false, summary, [], null);
}

public sealed record WorkerProducedAsset(
    string AssetType,
    string Value,
    string? Subtype,
    decimal? Confidence,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyCollection<ArtifactReference>? ArtifactReferences = null);

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

public sealed record ArtifactReference(
    string ArtifactType,
    string Name,
    string Hash);
