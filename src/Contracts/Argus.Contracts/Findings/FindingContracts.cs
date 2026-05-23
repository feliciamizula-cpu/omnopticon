namespace Argus.Contracts.Findings;

public enum FindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}

public enum FindingStatus
{
    New,
    Triaged,
    Accepted,
    FalsePositive,
    Mitigated,
    Reproduced
}

public sealed record FindingDto(
    Guid FindingId,
    Guid TargetId,
    Guid ProgramId,
    string Title,
    string Description,
    FindingSeverity Severity,
    decimal Confidence,
    FindingStatus Status,
    Guid? AffectedAssetId,
    Guid? SourceAssetId,
    string? SourceWorkerType,
    Guid? SourceTaskRunId,
    Guid? SourceEventId,
    Guid? CorrelationId,
    IReadOnlyCollection<Guid> EvidenceArtifactIds,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<FindingNoteDto>? Notes,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastUpdatedAt);

public sealed record FindingNoteDto(
    Guid NoteId,
    Guid FindingId,
    string UserId,
    string Note,
    DateTimeOffset CreatedAt);

public sealed record FindingSearchResult(
    IReadOnlyCollection<FindingDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record SearchFindingsRequest(
    Guid? ProgramId,
    Guid? TargetId,
    IReadOnlyCollection<FindingSeverity> Severity,
    IReadOnlyCollection<FindingStatus> Status,
    string? Search,
    IReadOnlyCollection<string> Tags,
    int Page = 1,
    int PageSize = 100);

public sealed record CreateFindingRequest(
    Guid TargetId,
    Guid ProgramId,
    string Title,
    string Description,
    FindingSeverity Severity,
    decimal Confidence,
    Guid? AffectedAssetId,
    Guid? SourceAssetId,
    string? SourceWorkerType,
    Guid? SourceTaskRunId,
    Guid? SourceEventId,
    Guid? CorrelationId,
    IReadOnlyCollection<Guid>? EvidenceArtifactIds,
    IReadOnlyCollection<string>? Tags);

public sealed record UpdateFindingRequest(
    string? Title,
    string? Description,
    FindingSeverity? Severity,
    decimal? Confidence,
    FindingStatus? Status,
    IReadOnlyCollection<string>? Tags);

public sealed record TriageFindingRequest(
    FindingStatus NewStatus,
    string? UserId,
    string? Reason);

public sealed record AddFindingNoteRequest(
    string UserId,
    string Note);

public sealed record UpdateFindingTagsRequest(
    IReadOnlyCollection<string> Tags);

public sealed record ExportFindingsRequest(
    Guid? ProgramId,
    IReadOnlyCollection<FindingSeverity> Severity,
    IReadOnlyCollection<FindingStatus> Status,
    string? Format);

public sealed record ExportResult(
    string Content,
    string Format,
    int Count);