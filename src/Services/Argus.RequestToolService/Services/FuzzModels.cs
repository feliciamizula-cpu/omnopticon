using System.Text.Json.Serialization;

namespace Argus.RequestToolService.Services;

// ── Payload source types ──────────────────────────────────────────────────────

public enum PayloadSourceType { List, Counter, BuiltIn, File }

public enum TransformationType
{
    Uppercase, Lowercase,
    Base64Encode, Base64Decode,
    UrlEncode, UrlDecode,
    HtmlEncode, HtmlDecode,
    Md5, Sha1,
    Reverse, Trim,
    Prefix, Suffix,
    RegexExtract,
}

public sealed record Transformation(
    TransformationType Type,
    string? Value = null);  // Prefix/Suffix string, or regex pattern for RegexExtract

public sealed record PayloadSource(
    PayloadSourceType SourceType,
    // List: newline-separated values
    IReadOnlyList<string>? Values = null,
    // Counter
    long CounterStart = 0,
    long CounterEnd = 100,
    long CounterStep = 1,
    int CounterPadding = 0,
    // BuiltIn
    string? BuiltInName = null,
    // Transformations applied after source generation
    IReadOnlyList<Transformation>? Transformations = null);

// ── Attack types ──────────────────────────────────────────────────────────────

/// <summary>
/// Pitchfork: cycle all variable lists in parallel at the same index.
/// ClusterBomb: cartesian product of all variable lists (all permutations).
/// </summary>
public enum FuzzAttackType { Pitchfork, ClusterBomb }

// ── Extract pattern (user-defined column) ────────────────────────────────────

public sealed record FuzzExtractPattern(
    string Name,
    string Regex,
    bool CaptureGroup1 = true);  // true = group 1 value; false = full match

// ── Request / Response DTOs ──────────────────────────────────────────────────

public sealed record FuzzRunRequest(
    Guid TemplateExchangeId,
    FuzzAttackType AttackType,
    IReadOnlyList<FuzzVariableConfig> Variables,
    IReadOnlyList<FuzzExtractPattern> ExtractPatterns,
    int ThrottleMs = 0,
    int MaxConcurrent = 10,
    int MaxRequests = 1000);

public sealed record FuzzVariableConfig(
    string Name,
    PayloadSource Source);

public sealed record FuzzRunStatusDto(
    Guid RunId,
    Guid SessionId,
    string Status,
    FuzzAttackType AttackType,
    int TotalCount,
    int CompletedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record FuzzResultDto(
    Guid ResultId,
    Guid RunId,
    int SequenceNumber,
    IReadOnlyDictionary<string, string> PayloadValues,
    int? StatusCode,
    long? ResponseSizeBytes,
    int? DurationMs,
    IReadOnlyDictionary<string, string> ExtractValues,
    string? NetworkError,
    DateTimeOffset CreatedAt);

public sealed record FuzzResultsPageDto(
    IReadOnlyList<FuzzResultDto> Results,
    int TotalCount,
    int Offset);
