namespace Argus.Contracts.Programs;

public enum ScopeRuleAction
{
    Include,
    Exclude
}

public enum ScopeAssetType
{
    Domain,
    WildcardDomain,
    Url,
    Cidr,
    IpRange
}

public enum ScopeValidationReason
{
    IncludedByRule,
    ExcludedByRule,
    NoMatchingInclude,
    InvalidAssetType,
    UnsupportedProtocol,
    PortNotAllowed,
    PathExcluded,
    SchemeNotAllowed,
    CidrMismatch
}

public sealed record ProgramDto(
    Guid ProgramId,
    string Name,
    string Source,
    string? ExternalUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<ProgramScopeDto> Scopes);

public sealed record CreateProgramRequest(
    string Name,
    string Source,
    string? ExternalUrl);

public sealed record ProgramScopeDto(
    Guid ScopeId,
    Guid ProgramId,
    string ScopeType,
    string Pattern,
    ScopeRuleAction Action,
    string? Notes,
    DateTimeOffset CreatedAt);

public sealed record CreateProgramScopeRequest(
    Guid ProgramId,
    string ScopeType,
    string Pattern,
    ScopeRuleAction Action,
    string? Notes);

public sealed record ScopeValidationRequest(
    Guid ProgramId,
    string Target,
    string TargetType);

public sealed record ScopeValidationResult(
    Guid ProgramId,
    string Target,
    bool IsAllowed,
    Guid? MatchedScopeId,
    ScopeValidationReason Reason,
    string? RulePattern);

public sealed record ProgramRuleRevisionDto(
    Guid RevisionId,
    Guid ProgramId,
    int Version,
    string ChangeType,
    string? OldValue,
    string? NewValue,
    string ChangedBy,
    DateTimeOffset CreatedAt);

public sealed record ScopeExclusionDto(
    Guid ExclusionId,
    Guid ProgramId,
    string Pattern,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record RateLimitPolicyDto(
    Guid PolicyId,
    Guid ProgramId,
    Guid? ScopeId,
    string BucketKey,
    int Capacity,
    int RefillRate,
    string Source);

public sealed record CreateScopeExclusionRequest(
    string Pattern,
    string? Reason,
    DateTimeOffset? ExpiresAt);

public sealed record CreateRateLimitPolicyRequest(
    Guid? ScopeId,
    string BucketKey,
    int Capacity,
    int RefillRate,
    string Source);

public sealed record ScopeBatchValidationRequest(
    Guid ProgramId,
    IReadOnlyCollection<string> Targets,
    string TargetType);

public sealed record ScopeSnapshot(
    Guid SnapshotId,
    Guid ProgramId,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<ProgramScopeDto> Scopes,
    IReadOnlyCollection<ScopeExclusionDto> Exclusions,
    string Signature);

public sealed record ProgramExportDto(
    Guid ProgramId,
    string Name,
    string Source,
    string? ExternalUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<ProgramScopeDto> Scopes,
    IReadOnlyCollection<ScopeExclusionDto> Exclusions,
    IReadOnlyCollection<ProgramRuleRevisionDto> RuleRevisions,
    IReadOnlyCollection<RateLimitPolicyDto> RateLimitPolicies,
    string ExportedAt,
    string? Signature);

public sealed record ProgramImportRequest(
    ProgramExportDto Export,
    bool ForceOverwrite);
