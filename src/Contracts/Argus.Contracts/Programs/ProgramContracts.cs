namespace Argus.Contracts.Programs;

public enum ScopeRuleAction
{
    Include,
    Exclude
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
    string Reason);
