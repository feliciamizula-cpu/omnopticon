using Argus.Contracts.RequestTool;

namespace Argus.RequestToolService.Data;

public interface IRequestToolRepository
{
    Task<RequestToolSessionDto?> GetSessionByAssetIdAsync(Guid assetId, CancellationToken ct);
    Task<RequestToolSessionDto?> GetSessionAsync(Guid sessionId, CancellationToken ct);
    Task<RequestToolSessionDto> CreateSessionAsync(CreateSessionCommand command, CancellationToken ct);
    Task<IReadOnlyList<HttpExchangeSummaryDto>> GetExchangeSummariesAsync(Guid sessionId, CancellationToken ct);
    Task<HttpExchangeDetailDto?> GetExchangeAsync(Guid exchangeId, CancellationToken ct);
    Task<HttpExchangeDetailDto> InsertExchangeAsync(InsertExchangeCommand command, CancellationToken ct);
    Task UpdateExchangeAsync(UpdateExchangeCommand command, CancellationToken ct);
    Task RenameExchangeAsync(Guid exchangeId, string title, CancellationToken ct);
    Task PinExchangeAsync(Guid exchangeId, bool isPinned, CancellationToken ct);
    Task InsertAuditAsync(InsertAuditCommand command, CancellationToken ct);
}

public sealed record CreateSessionCommand(
    Guid AssetId,
    Guid ProgramId,
    Guid? ScopeId,
    string Title,
    string? CreatedBy);

public sealed record InsertExchangeCommand(
    Guid SessionId,
    Guid AssetId,
    Guid ProgramId,
    Guid? ParentExchangeId,
    RequestToolExchangeOrigin Origin,
    RequestToolExchangeOutcome Outcome,
    string TabTitle,
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
    string? TlsInfoJson,
    string? NetworkError,
    RequestToolScopeStatus ScopeStatus,
    string? RateLimitKey,
    string? ProxyId,
    string? RequestSha256,
    string? ResponseSha256,
    string? CreatedBy,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt);

public sealed record UpdateExchangeCommand(
    Guid ExchangeId,
    RequestToolExchangeOutcome Outcome,
    int? ResponseStatusCode,
    string? ResponseReasonPhrase,
    IReadOnlyDictionary<string, string[]>? ResponseHeaders,
    IReadOnlyDictionary<string, string>? ResponseCookies,
    string? ResponseBody,
    Guid? ResponseBodyArtifactId,
    string? ResponseBodySha256,
    long? ResponseBodySizeBytes,
    string? ResponseContentType,
    int? DurationMs,
    IReadOnlyList<RedirectHopDto> RedirectChain,
    string? TlsInfoJson,
    string? NetworkError,
    DateTimeOffset? CompletedAt);

public sealed record InsertAuditCommand(
    Guid? ExchangeId,
    Guid? SessionId,
    Guid? AssetId,
    Guid? ProgramId,
    string? Actor,
    string Action,
    string? TargetUrl,
    string? RequestMethod,
    string? ScopeStatus,
    string? RequestSha256,
    string? ResponseSha256,
    RequestToolExchangeOutcome Outcome,
    string? Metadata);