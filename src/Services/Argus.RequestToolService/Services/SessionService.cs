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
    private readonly IHttpReplayExecutor _replayExecutor;
    private readonly ILogger<AssetEvidenceHydrator> _logger;

    public AssetEvidenceHydrator(
        IRequestToolRepository repository,
        IAssetServiceClient assetService,
        IArtifactServiceClient artifactService,
        IHttpReplayExecutor replayExecutor,
        ILogger<AssetEvidenceHydrator> logger)
    {
        _repository = repository;
        _assetService = assetService;
        _artifactService = artifactService;
        _replayExecutor = replayExecutor;
        _logger = logger;
    }

    public async Task<HttpExchangeDetailDto?> TryHydrateOriginalEvidenceAsync(Guid sessionId, Guid assetId, Guid programId, CancellationToken ct)
    {
        var asset = await _assetService.GetAssetAsync(assetId, ct);
        if (asset is null)
            return null;

        var url = ExtractRequestUrl(asset.Value);
        if (url is null)
            return null;

        // Reconstruct the original request (method + URL + the recon User-Agent) and issue it through the
        // replay executor. This populates the request pane AND captures the live response (status, headers,
        // body) into the first exchange/tab, marked as OriginalEvidence. The executor enforces rate limits
        // and SSRF checks, so this is the same guarded path used for user replays.
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var request = new SendHttpRequestToolRequest(
            ParentExchangeId: null,
            Method: "GET",
            Url: url,
            Headers: new Dictionary<string, string[]>
            {
                ["User-Agent"] = ["Mozilla/5.0 (compatible; ArgusHttp/1.0)"]
            },
            Cookies: new Dictionary<string, string>(),
            Body: null,
            ContentType: null,
            FollowRedirects: true,
            ProxyId: null,
            TabTitle: $"Original - {host}");

        var exchange = await _replayExecutor.ExecuteAsync(
            sessionId, programId, request, ct, RequestToolExchangeOrigin.OriginalEvidence);
        return exchange;
    }

    /// <summary>
    /// Derives an absolute http(s) URL from an asset value. Url assets are already a URL; HttpResponse
    /// assets use the form "&lt;url&gt; &lt;status&gt; &lt;content-type&gt;", so the first token is the URL.
    /// </summary>
    private static string? ExtractRequestUrl(string? assetValue)
    {
        if (string.IsNullOrWhiteSpace(assetValue))
            return null;

        if (IsHttpUrl(assetValue))
            return assetValue;

        var firstToken = assetValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[0]
            : null;
        return firstToken is not null && IsHttpUrl(firstToken) ? firstToken : null;

        static bool IsHttpUrl(string candidate) =>
            Uri.TryCreate(candidate, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
    }
}