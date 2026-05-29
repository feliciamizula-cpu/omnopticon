using Argus.Contracts.RequestTool;
using Argus.RequestToolService.Data;
using Microsoft.Extensions.Options;
using Argus.RequestToolService.Http;

namespace Argus.RequestToolService.Services;

public interface ISessionService
{
    Task<RequestToolSessionDto> GetOrCreateSessionAsync(Guid assetId, CancellationToken ct);
}

public sealed class SessionService : ISessionService
{
    private readonly IRequestToolRepository _repository;
    private readonly IAssetServiceClient _assetService;
    private readonly IAssetEvidenceHydrator _evidenceHydrator;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        IRequestToolRepository repository,
        IAssetServiceClient assetService,
        IAssetEvidenceHydrator evidenceHydrator,
        ILogger<SessionService> logger)
    {
        _repository = repository;
        _assetService = assetService;
        _evidenceHydrator = evidenceHydrator;
        _logger = logger;
    }

    public async Task<RequestToolSessionDto> GetOrCreateSessionAsync(Guid assetId, CancellationToken ct)
    {
        var existingSession = await _repository.GetSessionByAssetIdAsync(assetId, ct);
        if (existingSession is not null)
        {
            _logger.LogDebug("Found existing session {SessionId} for asset {AssetId}", existingSession.SessionId, assetId);
            return existingSession;
        }

        var asset = await _assetService.GetAssetAsync(assetId, ct);
        if (asset is null)
        {
            throw new InvalidOperationException($"Asset {assetId} not found");
        }

        var session = await _repository.CreateSessionAsync(new CreateSessionCommand(
            assetId,
            asset.ProgramId,
            asset.ScopeId,
            $"Request Tool - {asset.Value}",
            null), ct);

        _logger.LogInformation("Created new session {SessionId} for asset {AssetId}", session.SessionId, assetId);

        var originalExchange = await _evidenceHydrator.TryHydrateOriginalEvidenceAsync(session.SessionId, assetId, asset.ProgramId, ct);
        if (originalExchange is not null)
        {
            _logger.LogInformation("Hydrated original evidence for asset {AssetId}", assetId);
        }

        return await _repository.GetSessionAsync(session.SessionId, ct) ?? session;
    }
}

public interface IAssetEvidenceHydrator
{
    Task<HttpExchangeDetailDto?> TryHydrateOriginalEvidenceAsync(Guid sessionId, Guid assetId, Guid programId, CancellationToken ct);
}

public sealed class AssetEvidenceHydrator : IAssetEvidenceHydrator
{
    private readonly IRequestToolRepository _repository;
    private readonly IAssetServiceClient _assetService;
    private readonly IArtifactServiceClient _artifactService;
    private readonly ILogger<AssetEvidenceHydrator> _logger;

    public AssetEvidenceHydrator(
        IRequestToolRepository repository,
        IAssetServiceClient assetService,
        IArtifactServiceClient artifactService,
        ILogger<AssetEvidenceHydrator> logger)
    {
        _repository = repository;
        _assetService = assetService;
        _artifactService = artifactService;
        _logger = logger;
    }

    public async Task<HttpExchangeDetailDto?> TryHydrateOriginalEvidenceAsync(Guid sessionId, Guid assetId, Guid programId, CancellationToken ct)
    {
        var asset = await _assetService.GetAssetAsync(assetId, ct);
        if (asset is null)
            return null;

        if (!string.IsNullOrEmpty(asset.Value) && Uri.TryCreate(asset.Value, UriKind.Absolute, out var assetUri))
        {
            var syntheticExchange = await CreateSyntheticExchangeAsync(sessionId, assetId, programId, assetUri, ct);
            return syntheticExchange;
        }

        return null;
    }

    private async Task<HttpExchangeDetailDto> CreateSyntheticExchangeAsync(
        Guid sessionId,
        Guid assetId,
        Guid programId,
        Uri uri,
        CancellationToken ct)
    {
        var exchange = await _repository.InsertExchangeAsync(new InsertExchangeCommand(
            SessionId: sessionId,
            AssetId: assetId,
            ProgramId: programId,
            ParentExchangeId: null,
            Origin: RequestToolExchangeOrigin.Synthetic,
            Outcome: RequestToolExchangeOutcome.Draft,
            TabTitle: $"Synthetic - {uri.Host}",
            RequestMethod: "GET",
            RequestUrl: uri.ToString(),
            RequestScheme: uri.Scheme,
            RequestHost: uri.Host,
            RequestPort: uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80),
            RequestPath: uri.AbsolutePath,
            RequestQuery: uri.Query,
            RequestHttpVersion: "HTTP/1.1",
            RequestHeaders: new Dictionary<string, string[]>(),
            RequestCookies: new Dictionary<string, string>(),
            RequestBody: null,
            RequestBodyArtifactId: null,
            RequestBodySha256: null,
            RequestBodySizeBytes: 0,
            RequestContentType: null,
            ResponseStatusCode: null,
            ResponseReasonPhrase: null,
            ResponseHttpVersion: null,
            ResponseHeaders: null,
            ResponseCookies: null,
            ResponseBody: null,
            ResponseBodyArtifactId: null,
            ResponseBodySha256: null,
            ResponseBodySizeBytes: null,
            ResponseContentType: null,
            DurationMs: null,
            RedirectChain: [],
            TlsInfoJson: null,
            NetworkError: null,
            ScopeStatus: RequestToolScopeStatus.Unknown,
            RateLimitKey: $"request-tool:{programId}:{uri.Host}:GET",
            ProxyId: null,
            RequestSha256: null,
            ResponseSha256: null,
            CreatedBy: null,
            SentAt: null,
            CompletedAt: null), ct);

        return exchange;
    }
}