using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Contracts.RequestTool;
using Argus.RequestToolService.Data;
using Argus.RequestToolService.Http;
using Argus.RequestToolService.Options;
using Microsoft.Extensions.Options;

namespace Argus.RequestToolService.Services;

public interface IHttpReplayExecutor
{
    Task<HttpExchangeDetailDto> ExecuteAsync(Guid sessionId, Guid programId, SendHttpRequestToolRequest request, CancellationToken ct);
}

public sealed partial class HttpReplayExecutor : IHttpReplayExecutor
{
    private readonly RequestToolOptions _options;
    private readonly IRequestToolRepository _repository;
    private readonly IBodyStorageService _bodyStorage;
    private readonly IRedactionService _redactionService;
    private readonly IProgramScopeServiceClient _scopeClient;
    private readonly IRateLimitServiceClient _rateLimitClient;
    private readonly IProxyRegistryServiceClient _proxyClient;
    private readonly IAuditService _auditService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpReplayExecutor> _logger;

    private static readonly HashSet<string> HopByHopHeaders =
    [
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection"
    ];

    public HttpReplayExecutor(
        IOptions<RequestToolOptions> options,
        IRequestToolRepository repository,
        IBodyStorageService bodyStorage,
        IRedactionService redactionService,
        IProgramScopeServiceClient scopeClient,
        IRateLimitServiceClient rateLimitClient,
        IProxyRegistryServiceClient proxyClient,
        IAuditService auditService,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpReplayExecutor> logger)
    {
        _options = options.Value;
        _repository = repository;
        _bodyStorage = bodyStorage;
        _redactionService = redactionService;
        _scopeClient = scopeClient;
        _rateLimitClient = rateLimitClient;
        _proxyClient = proxyClient;
        _auditService = auditService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HttpExchangeDetailDto> ExecuteAsync(Guid sessionId, Guid programId, SendHttpRequestToolRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return await CreateExchangeAsync(sessionId, programId, request, "Only absolute HTTP and HTTPS URLs are supported", ct);
        }

        var ssrfCheck = CheckSsrf(uri);
        if (!ssrfCheck.IsAllowed)
        {
            return await CreateExchangeAsync(sessionId, programId, request, ssrfCheck.Reason!, ct);
        }

        var scopeStatus = RequestToolScopeStatus.Unknown;

        var rateLimitKey = $"request-tool:{programId}:{uri.Host}:{request.Method}";
        var rateLimitResult = await _rateLimitClient.CheckRateLimitAsync(rateLimitKey, ct);

        if (!rateLimitResult.IsAllowed)
        {
            var rateLimitedExchange = await CreateExchangeAsync(sessionId, programId, request, null, ct);
            await _auditService.AuditAsync(new InsertAuditCommand(
                rateLimitedExchange.ExchangeId, sessionId, null, programId, null,
                "RequestRateLimited", uri.ToString(), request.Method, scopeStatus.ToString(),
                null, null, RequestToolExchangeOutcome.RateLimited,
                JsonSerializer.Serialize(new { RetryAfterSeconds = rateLimitResult.RetryAfterSeconds })), ct);

            return rateLimitedExchange;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await SendHttpRequestAsync(uri, request, ct);
            stopwatch.Stop();

            await _auditService.AuditAsync(new InsertAuditCommand(
                result.ExchangeId, sessionId, null, programId, null,
                "RequestSent", uri.ToString(), request.Method, scopeStatus.ToString(),
                null, null, result.Outcome, null), ct);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Request failed for {Url}", uri);

            var failedExchange = await CreateExchangeAsync(sessionId, programId, request, ex.Message, ct);

            await _auditService.AuditAsync(new InsertAuditCommand(
                failedExchange.ExchangeId, sessionId, null, programId, null,
                "RequestFailed", uri.ToString(), request.Method, scopeStatus.ToString(),
                null, null, RequestToolExchangeOutcome.NetworkError, null), ct);

            return failedExchange;
        }
    }

    private async Task<HttpExchangeDetailDto> CreateExchangeAsync(
        Guid sessionId,
        Guid programId,
        SendHttpRequestToolRequest request,
        string? errorMessage,
        CancellationToken ct)
    {
        var parsedUri = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri : null;

        var outcome = string.IsNullOrEmpty(errorMessage)
            ? RequestToolExchangeOutcome.Draft
            : RequestToolExchangeOutcome.ValidationError;

        var exchange = await _repository.InsertExchangeAsync(new InsertExchangeCommand(
            SessionId: sessionId,
            AssetId: Guid.Empty,
            ProgramId: programId,
            ParentExchangeId: request.ParentExchangeId,
            Origin: request.ParentExchangeId.HasValue ? RequestToolExchangeOrigin.UserReplay : RequestToolExchangeOrigin.Synthetic,
            Outcome: outcome,
            TabTitle: request.TabTitle ?? $"{request.Method} {parsedUri?.Host ?? "Request"}",
            RequestMethod: request.Method,
            RequestUrl: request.Url,
            RequestScheme: parsedUri?.Scheme ?? "https",
            RequestHost: parsedUri?.Host ?? "",
            RequestPort: parsedUri?.Port > 0 ? parsedUri?.Port : null,
            RequestPath: parsedUri?.AbsolutePath ?? "/",
            RequestQuery: parsedUri?.Query,
            RequestHttpVersion: "HTTP/1.1",
            RequestHeaders: request.Headers,
            RequestCookies: request.Cookies,
            RequestBody: request.Body,
            RequestBodyArtifactId: null,
            RequestBodySha256: null,
            RequestBodySizeBytes: request.Body?.Length,
            RequestContentType: request.ContentType,
            ResponseStatusCode: null,
            ResponseReasonPhrase: errorMessage,
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
            NetworkError: errorMessage,
            ScopeStatus: RequestToolScopeStatus.Unknown,
            RateLimitKey: null,
            ProxyId: request.ProxyId,
            RequestSha256: null,
            ResponseSha256: null,
            CreatedBy: null,
            SentAt: DateTimeOffset.UtcNow,
            CompletedAt: string.IsNullOrEmpty(errorMessage) ? null : DateTimeOffset.UtcNow), ct);

        return exchange;
    }

    private async Task<HttpExchangeDetailDto> SendHttpRequestAsync(
        Uri uri,
        SendHttpRequestToolRequest request,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("request-tool-replay");

        using var requestMessage = new HttpRequestMessage(new HttpMethod(request.Method), uri);

        foreach (var header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            try
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add header {Header}", header.Key);
            }
        }

        if (request.Cookies.Count > 0 && !request.Headers.ContainsKey("Cookie"))
        {
            var cookieHeader = string.Join("; ", request.Cookies.Select(c => $"{c.Key}={c.Value}"));
            requestMessage.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (!string.IsNullOrEmpty(request.Body) && CanHaveBody(request.Method))
        {
            var bodyContent = new StringContent(request.Body, Encoding.UTF8);
            if (!string.IsNullOrEmpty(request.ContentType))
            {
                bodyContent.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }
            requestMessage.Content = bodyContent;
        }

        var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);

        var responseHeaders = new Dictionary<string, string[]>(
            response.Headers.Concat(response.Content.Headers)
                .ToDictionary(h => h.Key, h => h.Value.ToArray()));

        var responseCookies = ParseSetCookies(responseHeaders);

        string? responseBody = null;
        Guid? responseBodyArtifactId = null;
        string? responseBodySha256 = null;
        long? responseBodySizeBytes = null;
        string? responseContentType = response.Content.Headers.ContentType?.ToString();

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var memoryStream = new MemoryStream();
        await responseStream.CopyToAsync(memoryStream, ct);
        var bodyBytes = memoryStream.ToArray();
        responseBodySizeBytes = bodyBytes.Length;

        if (bodyBytes.Length <= _options.InlineBodyThresholdBytes)
        {
            responseBody = Encoding.UTF8.GetString(bodyBytes);
            responseBodySha256 = ComputeSha256(bodyBytes);
        }
        else
        {
            responseBodyArtifactId = Guid.NewGuid();
            responseBodySha256 = ComputeSha256(bodyBytes);
        }

        var exchange = await CreateExchangeAsync(Guid.Empty, Guid.Empty, request, null, ct);

        return exchange with
        {
            Outcome = RequestToolExchangeOutcome.Completed,
            ResponseStatusCode = (int)response.StatusCode,
            ResponseReasonPhrase = response.ReasonPhrase,
            ResponseHttpVersion = "HTTP/1.1",
            ResponseHeaders = responseHeaders,
            ResponseCookies = responseCookies,
            ResponseBody = responseBody,
            ResponseBodyArtifactId = responseBodyArtifactId,
            ResponseBodySha256 = responseBodySha256,
            ResponseBodySizeBytes = responseBodySizeBytes,
            ResponseContentType = responseContentType,
            DurationMs = 0,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private (bool IsAllowed, string? Reason) CheckSsrf(Uri uri)
    {
        if (!_options.AllowLocalhostTargets)
        {
            if (IsLoopback(uri.Host))
                return (false, "Localhost targets are not allowed");
        }

        if (!_options.AllowPrivateNetworkTargets)
        {
            if (IsPrivateRange(uri.Host))
                return (false, "Target resolves to a protected internal network address");
        }

        if (!_options.AllowCloudMetadataTargets)
        {
            if (IsCloudMetadata(uri.Host))
                return (false, "Cloud metadata endpoints are not allowed");
        }

        return (true, null);
    }

    private static bool IsLoopback(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateRange(string host)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            foreach (var ip in addresses)
            {
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool IsCloudMetadata(string host)
    {
        if (host == "169.254.169.254") return true;
        if (host.StartsWith("169.254.169.", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool CanHaveBody(string method)
    {
        return method != "GET" && method != "HEAD" && method != "OPTIONS" && method != "TRACE";
    }

    private static Dictionary<string, string> ParseSetCookies(Dictionary<string, string[]> headers)
    {
        var cookies = new Dictionary<string, string>();
        if (headers.TryGetValue("Set-Cookie", out var setCookieHeaders))
        {
            foreach (var cookieHeader in setCookieHeaders)
            {
                var parts = cookieHeader.Split(';');
                if (parts.Length > 0)
                {
                    var nameValue = parts[0].Split('=');
                    if (nameValue.Length == 2)
                    {
                        cookies[nameValue[0].Trim()] = nameValue[1].Trim();
                    }
                }
            }
        }
        return cookies;
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}