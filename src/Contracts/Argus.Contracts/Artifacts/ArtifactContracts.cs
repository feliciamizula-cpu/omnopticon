namespace Argus.Contracts.Artifacts;

public enum RedactionStatus
{
    None,
    Pending,
    Applied,
    Rejected
}

public enum ArtifactType
{
    Screenshot,
   HarFile,
    JsonReport,
    XmlReport,
    TextLog,
    PcapFile,
    Certificate,
    RawResponse,
    ScriptOutput,
    ConfigFile,
    Credentials,
    FindingEvidence,
    RemediationPlan,
    Other
}

public sealed record ArtifactDto(
    Guid ArtifactId,
    Guid TargetId,
    Guid ProgramId,
    Guid? AssetId,
    Guid? TaskRunId,
    string? WorkerType,
    string ArtifactType,
    string ContentType,
    string StorageProvider,
    string StorageKey,
    long SizeBytes,
    string? Sha256,
    string? PreviewText,
    RedactionStatus RedactionStatus,
    DateTimeOffset? RetentionExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record CreateArtifactRequest(
    Guid TargetId,
    Guid ProgramId,
    Guid? AssetId,
    Guid? TaskRunId,
    string? WorkerType,
    string ArtifactType,
    string ContentType,
    string? StorageProvider,
    string StorageKey,
    long SizeBytes,
    string? Sha256,
    string? PreviewText);

public sealed record UpdateArtifactRedactionRequest(RedactionStatus Status, string? Reason);

public sealed record ArtifactPreviewDto(
    Guid ArtifactId,
    string ArtifactType,
    string ContentType,
    string PreviewText,
    long SizeBytes);

public sealed record ArtifactDownloadDto(
    Guid ArtifactId,
    string StorageProvider,
    string StorageKey,
    string ContentType,
    long SizeBytes,
    string? Sha256,
    string ContentDisposition);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);