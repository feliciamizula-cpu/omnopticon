using System.Text.Json;
using Argus.Contracts.RequestTool;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Argus.RequestToolService.Data;

public sealed class RequestToolRepository : IRequestToolRepository
{
    private readonly RequestToolDbContext _dbContext;

    public RequestToolRepository(RequestToolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<RequestToolSessionDto?> GetSessionByAssetIdAsync(Guid assetId, CancellationToken ct)
    {
        var session = await _dbContext.Sessions
            .FirstOrDefaultAsync(s => s.AssetId == assetId, ct);

        if (session is null)
            return null;

        var exchanges = await GetExchangeSummariesAsync(session.SessionId, ct);
        return ToDto(session, exchanges);
    }

    public async Task<RequestToolSessionDto?> GetSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await _dbContext.Sessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        if (session is null)
            return null;

        var exchanges = await GetExchangeSummariesAsync(sessionId, ct);
        return ToDto(session, exchanges);
    }

    public async Task<RequestToolSessionDto> CreateSessionAsync(CreateSessionCommand command, CancellationToken ct)
    {
        var session = new RequestToolSessionRecord
        {
            SessionId = Guid.NewGuid(),
            AssetId = command.AssetId,
            ProgramId = command.ProgramId,
            ScopeId = command.ScopeId,
            Title = command.Title,
            CreatedBy = command.CreatedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Sessions.Add(session);
        await _dbContext.SaveChangesAsync(ct);

        return ToDto(session, []);
    }

    public async Task<IReadOnlyList<HttpExchangeSummaryDto>> GetExchangeSummariesAsync(Guid sessionId, CancellationToken ct)
    {
        var exchanges = await _dbContext.Exchanges
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

        return exchanges.Select(ToSummaryDto).ToList();
    }

    public async Task<HttpExchangeDetailDto?> GetExchangeAsync(Guid exchangeId, CancellationToken ct)
    {
        var exchange = await _dbContext.Exchanges
            .FirstOrDefaultAsync(e => e.ExchangeId == exchangeId, ct);

        if (exchange is null)
            return null;

        return ToDetailDto(exchange);
    }

    public async Task<HttpExchangeDetailDto> InsertExchangeAsync(InsertExchangeCommand command, CancellationToken ct)
    {
        var exchange = new HttpExchangeRecord
        {
            ExchangeId = Guid.NewGuid(),
            SessionId = command.SessionId,
            AssetId = command.AssetId,
            ProgramId = command.ProgramId,
            ParentExchangeId = command.ParentExchangeId,
            Origin = command.Origin.ToString(),
            Outcome = command.Outcome.ToString(),
            TabTitle = command.TabTitle,
            IsPinned = false,
            RequestMethod = command.RequestMethod,
            RequestUrl = command.RequestUrl,
            RequestScheme = command.RequestScheme,
            RequestHost = command.RequestHost,
            RequestPort = command.RequestPort,
            RequestPath = command.RequestPath,
            RequestQuery = command.RequestQuery,
            RequestHttpVersion = command.RequestHttpVersion,
            RequestHeaders = JsonSerializer.Serialize(command.RequestHeaders),
            RequestCookies = JsonSerializer.Serialize(command.RequestCookies),
            RequestBodyInline = command.RequestBody,
            RequestBodyArtifactId = command.RequestBodyArtifactId,
            RequestBodySha256 = command.RequestBodySha256,
            RequestBodySizeBytes = command.RequestBodySizeBytes,
            RequestContentType = command.RequestContentType,
            ResponseStatusCode = command.ResponseStatusCode,
            ResponseReasonPhrase = command.ResponseReasonPhrase,
            ResponseHttpVersion = command.ResponseHttpVersion,
            ResponseHeaders = command.ResponseHeaders is not null ? JsonSerializer.Serialize(command.ResponseHeaders) : null,
            ResponseCookies = command.ResponseCookies is not null ? JsonSerializer.Serialize(command.ResponseCookies) : null,
            ResponseBodyInline = command.ResponseBody,
            ResponseBodyArtifactId = command.ResponseBodyArtifactId,
            ResponseBodySha256 = command.ResponseBodySha256,
            ResponseBodySizeBytes = command.ResponseBodySizeBytes,
            ResponseContentType = command.ResponseContentType,
            DurationMs = command.DurationMs,
            RedirectChain = JsonSerializer.Serialize(command.RedirectChain),
            TlsInfo = command.TlsInfoJson,
            NetworkError = command.NetworkError,
            ScopeStatus = command.ScopeStatus.ToString(),
            RateLimitKey = command.RateLimitKey,
            ProxyId = command.ProxyId,
            RequestSha256 = command.RequestSha256,
            ResponseSha256 = command.ResponseSha256,
            CreatedBy = command.CreatedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            SentAt = command.SentAt,
            CompletedAt = command.CompletedAt
        };

        _dbContext.Exchanges.Add(exchange);

        var session = await _dbContext.Sessions.FindAsync([command.SessionId], ct);
        if (session is not null)
        {
            session.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);

        return ToDetailDto(exchange);
    }

    public async Task UpdateExchangeAsync(UpdateExchangeCommand command, CancellationToken ct)
    {
        var exchange = await _dbContext.Exchanges
            .FirstOrDefaultAsync(e => e.ExchangeId == command.ExchangeId, ct);

        if (exchange is null)
            return;

        exchange.Outcome = command.Outcome.ToString();
        exchange.ResponseStatusCode = command.ResponseStatusCode;
        exchange.ResponseReasonPhrase = command.ResponseReasonPhrase;
        exchange.ResponseHeaders = command.ResponseHeaders is not null ? JsonSerializer.Serialize(command.ResponseHeaders) : null;
        exchange.ResponseCookies = command.ResponseCookies is not null ? JsonSerializer.Serialize(command.ResponseCookies) : null;
        exchange.ResponseBodyInline = command.ResponseBody;
        exchange.ResponseBodyArtifactId = command.ResponseBodyArtifactId;
        exchange.ResponseBodySha256 = command.ResponseBodySha256;
        exchange.ResponseBodySizeBytes = command.ResponseBodySizeBytes;
        exchange.ResponseContentType = command.ResponseContentType;
        exchange.DurationMs = command.DurationMs;
        exchange.RedirectChain = JsonSerializer.Serialize(command.RedirectChain);
        exchange.TlsInfo = command.TlsInfoJson;
        exchange.NetworkError = command.NetworkError;
        exchange.CompletedAt = command.CompletedAt;

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task RenameExchangeAsync(Guid exchangeId, string title, CancellationToken ct)
    {
        var exchange = await _dbContext.Exchanges
            .FirstOrDefaultAsync(e => e.ExchangeId == exchangeId, ct);

        if (exchange is null)
            return;

        exchange.TabTitle = title;
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task PinExchangeAsync(Guid exchangeId, bool isPinned, CancellationToken ct)
    {
        var exchange = await _dbContext.Exchanges
            .FirstOrDefaultAsync(e => e.ExchangeId == exchangeId, ct);

        if (exchange is null)
            return;

        exchange.IsPinned = isPinned;
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task InsertAuditAsync(InsertAuditCommand command, CancellationToken ct)
    {
        var audit = new HttpExchangeAuditRecord
        {
            AuditId = Guid.NewGuid(),
            ExchangeId = command.ExchangeId,
            SessionId = command.SessionId,
            AssetId = command.AssetId,
            ProgramId = command.ProgramId,
            Actor = command.Actor,
            Action = command.Action,
            TargetUrl = command.TargetUrl,
            RequestMethod = command.RequestMethod,
            ScopeStatus = command.ScopeStatus,
            RequestSha256 = command.RequestSha256,
            ResponseSha256 = command.ResponseSha256,
            Outcome = command.Outcome.ToString(),
            Metadata = command.Metadata ?? "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.AuditLogs.Add(audit);
        await _dbContext.SaveChangesAsync(ct);
    }

    private static RequestToolSessionDto ToDto(RequestToolSessionRecord session, IReadOnlyList<HttpExchangeSummaryDto> exchanges) =>
        new(
            session.SessionId,
            session.AssetId,
            session.ProgramId,
            session.ScopeId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            exchanges);

    private static HttpExchangeSummaryDto ToSummaryDto(HttpExchangeRecord e) =>
        new(
            e.ExchangeId,
            e.SessionId,
            e.AssetId,
            e.ProgramId,
            e.ParentExchangeId,
            Enum.Parse<RequestToolExchangeOrigin>(e.Origin),
            Enum.Parse<RequestToolExchangeOutcome>(e.Outcome),
            e.TabTitle,
            e.IsPinned,
            e.RequestMethod,
            e.RequestUrl,
            e.RequestHost,
            Enum.Parse<RequestToolScopeStatus>(e.ScopeStatus),
            e.ResponseStatusCode,
            e.ResponseReasonPhrase,
            e.ResponseContentType,
            e.ResponseBodySizeBytes,
            e.DurationMs,
            e.NetworkError,
            e.CreatedAt,
            e.SentAt,
            e.CompletedAt);

    private static HttpExchangeDetailDto ToDetailDto(HttpExchangeRecord e) =>
        new(
            e.ExchangeId,
            e.SessionId,
            e.AssetId,
            e.ProgramId,
            e.ParentExchangeId,
            Enum.Parse<RequestToolExchangeOrigin>(e.Origin),
            Enum.Parse<RequestToolExchangeOutcome>(e.Outcome),
            e.TabTitle,
            e.IsPinned,
            e.RequestMethod,
            e.RequestUrl,
            e.RequestScheme,
            e.RequestHost,
            e.RequestPort,
            e.RequestPath,
            e.RequestQuery,
            e.RequestHttpVersion,
            JsonSerializer.Deserialize<Dictionary<string, string[]>>(e.RequestHeaders) ?? new Dictionary<string, string[]>(),
            JsonSerializer.Deserialize<Dictionary<string, string>>(e.RequestCookies) ?? new Dictionary<string, string>(),
            e.RequestBodyInline,
            e.RequestBodyArtifactId,
            e.RequestBodySha256,
            e.RequestBodySizeBytes,
            e.RequestContentType,
            e.ResponseStatusCode,
            e.ResponseReasonPhrase,
            e.ResponseHttpVersion,
            string.IsNullOrEmpty(e.ResponseHeaders) ? null : JsonSerializer.Deserialize<Dictionary<string, string[]>>(e.ResponseHeaders),
            string.IsNullOrEmpty(e.ResponseCookies) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(e.ResponseCookies),
            e.ResponseBodyInline,
            e.ResponseBodyArtifactId,
            e.ResponseBodySha256,
            e.ResponseBodySizeBytes,
            e.ResponseContentType,
            e.DurationMs,
            JsonSerializer.Deserialize<List<RedirectHopDto>>(e.RedirectChain) ?? new List<RedirectHopDto>(),
            string.IsNullOrEmpty(e.TlsInfo) ? null : JsonSerializer.Deserialize<TlsInfoDto>(e.TlsInfo),
            e.NetworkError,
            Enum.Parse<RequestToolScopeStatus>(e.ScopeStatus),
            e.RateLimitKey,
            e.ProxyId,
            e.CreatedAt,
            e.SentAt,
            e.CompletedAt);
}