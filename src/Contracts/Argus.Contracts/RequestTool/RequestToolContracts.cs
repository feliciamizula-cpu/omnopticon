namespace Argus.Contracts.RequestTool;

public enum RequestToolExchangeOrigin
{
    OriginalEvidence = 0,
    UserReplay = 1,
    Imported = 2,
    FutureScript = 3,
    Synthetic = 4
}

public enum RequestToolScopeStatus
{
    Unknown = 0,
    InScope = 1,
    OutOfScope = 2,
    Excluded = 3,
    NeedsReview = 4
}

public enum RequestToolExchangeOutcome
{
    Draft = 0,
    Completed = 1,
    NetworkError = 2,
    RateLimited = 3,
    ValidationError = 4,
    StorageError = 5,
    Cancelled = 6
}

public enum RequestToolCompareTarget
{
    RequestHeaders = 0,
    RequestBody = 1,
    RawRequest = 2,
    ResponseHeaders = 3,
    ResponseBody = 4,
    RawResponse = 5
}

public sealed record RequestToolSessionDto(
    Guid SessionId,
    Guid AssetId,
    Guid ProgramId,
    Guid? ScopeId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HttpExchangeSummaryDto> Exchanges);

public sealed record HttpExchangeSummaryDto(
    Guid ExchangeId,
    Guid SessionId,
    Guid AssetId,
    Guid ProgramId,
    Guid? ParentExchangeId,
    RequestToolExchangeOrigin Origin,
    RequestToolExchangeOutcome Outcome,
    string TabTitle,
    bool IsPinned,
    string RequestMethod,
    string RequestUrl,
    string RequestHost,
    RequestToolScopeStatus ScopeStatus,
    int? ResponseStatusCode,
    string? ResponseReasonPhrase,
    string? ResponseContentType,
    long? ResponseBodySizeBytes,
    int? DurationMs,
    string? NetworkError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt);

public sealed record HttpExchangeDetailDto(
    Guid ExchangeId,
    Guid SessionId,
    Guid AssetId,
    Guid ProgramId,
    Guid? ParentExchangeId,
    RequestToolExchangeOrigin Origin,
    RequestToolExchangeOutcome Outcome,
    string TabTitle,
    bool IsPinned,

    string RequestMethod,
    string RequestUrl,
    string RequestScheme,
    string RequestHost,
    int? RequestPort,
    string RequestPath,
    string? RequestQuery,
    string? RequestHttpVersion,
    IReadOnlyDictionary<string, string[]> RequestHeaders,
    IReadOnlyDictionary<string, string> RequestCookies,
    string? RequestBody,
    Guid? RequestBodyArtifactId,
    string? RequestBodySha256,
    long? RequestBodySizeBytes,
    string? RequestContentType,

    int? ResponseStatusCode,
    string? ResponseReasonPhrase,
    string? ResponseHttpVersion,
    IReadOnlyDictionary<string, string[]>? ResponseHeaders,
    IReadOnlyDictionary<string, string>? ResponseCookies,
    string? ResponseBody,
    Guid? ResponseBodyArtifactId,
    string? ResponseBodySha256,
    long? ResponseBodySizeBytes,
    string? ResponseContentType,

    int? DurationMs,
    IReadOnlyList<RedirectHopDto> RedirectChain,
    TlsInfoDto? TlsInfo,
    string? NetworkError,

    RequestToolScopeStatus ScopeStatus,
    string? RateLimitKey,
    string? ProxyId,

    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt);

public sealed record RedirectHopDto(
    int Order,
    string Url,
    int? StatusCode,
    string? Location,
    long? DurationMs);

public sealed record TlsInfoDto(
    string? Protocol,
    string? CipherSuite,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? Thumbprint);

public sealed record CreateOrGetRequestToolSessionRequest(
    Guid AssetId);

public sealed record SendHttpRequestToolRequest(
    Guid? ParentExchangeId,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyDictionary<string, string> Cookies,
    string? Body,
    string? ContentType,
    bool FollowRedirects,
    string? ProxyId,
    string? TabTitle);

public sealed record CloneHttpExchangeRequest(
    string? TabTitle);

public sealed record RenameHttpExchangeRequest(
    string TabTitle);

public sealed record PinHttpExchangeRequest(
    bool IsPinned);

public sealed record CompareHttpExchangeRequest(
    Guid LeftExchangeId,
    Guid RightExchangeId,
    RequestToolCompareTarget Target);

public sealed record CompareHttpExchangeResponse(
    Guid LeftExchangeId,
    Guid RightExchangeId,
    RequestToolCompareTarget Target,
    string LeftTitle,
    string RightTitle,
    string LeftText,
    string RightText,
    string UnifiedDiff,
    object? SideBySideDiff);

public sealed record RawHttpMessageDto(
    Guid ExchangeId,
    string ContentType,
    string Text,
    bool IsTruncated,
    long? FullSizeBytes,
    Guid? BodyArtifactId);