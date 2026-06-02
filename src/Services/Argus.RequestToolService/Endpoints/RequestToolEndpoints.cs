using Argus.Contracts.RequestTool;
using Argus.RequestToolService.Data;
using Argus.RequestToolService.Services;
using Microsoft.AspNetCore.Mvc;

namespace Argus.RequestToolService.Endpoints;

public static class RequestToolEndpoints
{
    public static void MapRoutes(WebApplication app)
    {
        var group = app.MapGroup("/request-tool").WithTags("request-tool");

        group.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

        group.MapGet("/assets/{assetId:guid}/session", GetSessionByAssetId);
        group.MapPost("/assets/{assetId:guid}/session", CreateOrGetSession);

        group.MapGet("/sessions/{sessionId:guid}", GetSession);
        group.MapGet("/sessions/{sessionId:guid}/exchanges", GetSessionExchanges);

        group.MapGet("/exchanges/{exchangeId:guid}", GetExchange);
        group.MapPost("/exchanges/{exchangeId:guid}/clone", CloneExchange);
        group.MapPatch("/exchanges/{exchangeId:guid}/title", RenameExchange);
        group.MapPatch("/exchanges/{exchangeId:guid}/pin", PinExchange);

        group.MapPost("/sessions/{sessionId:guid}/send", SendRequest);

        group.MapPost("/compare", CompareExchanges);

        group.MapGet("/exchanges/{exchangeId:guid}/raw-request", GetRawRequest);
        group.MapGet("/exchanges/{exchangeId:guid}/raw-response", GetRawResponse);

        // ── Fuzzer ────────────────────────────────────────────────────────────
        group.MapPost("/sessions/{sessionId:guid}/fuzz/runs", CreateFuzzRun);
        group.MapGet("/sessions/{sessionId:guid}/fuzz/runs", GetSessionFuzzRuns);
        group.MapGet("/fuzz/runs/{runId:guid}", GetFuzzRun);
        group.MapGet("/fuzz/runs/{runId:guid}/results", GetFuzzResults);
        group.MapPost("/fuzz/runs/{runId:guid}/stop", StopFuzzRun);
        group.MapGet("/fuzz/built-ins", GetBuiltInLists);
    }

    private static async Task<IResult> GetSessionByAssetId(
        [FromRoute] Guid assetId,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        var session = await repository.GetSessionByAssetIdAsync(assetId, ct);
        if (session is null)
            return Results.NotFound();

        return Results.Ok(session);
    }

    private static async Task<IResult> CreateOrGetSession(
        [FromRoute] Guid assetId,
        [FromServices] ISessionService sessionService,
        CancellationToken ct)
    {
        try
        {
            var session = await sessionService.GetOrCreateSessionAsync(assetId, ct);
            return Results.Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }
    }

    private static async Task<IResult> GetSession(
        [FromRoute] Guid sessionId,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(sessionId, ct);
        if (session is null)
            return Results.NotFound();

        return Results.Ok(session);
    }

    private static async Task<IResult> GetSessionExchanges(
        [FromRoute] Guid sessionId,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        var exchanges = await repository.GetExchangeSummariesAsync(sessionId, ct);
        return Results.Ok(exchanges);
    }

    private static async Task<IResult> GetExchange(
        [FromRoute] Guid exchangeId,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        var exchange = await repository.GetExchangeAsync(exchangeId, ct);
        if (exchange is null)
            return Results.NotFound();

        return Results.Ok(exchange);
    }

    private static async Task<IResult> CloneExchange(
        [FromRoute] Guid exchangeId,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        var exchange = await repository.GetExchangeAsync(exchangeId, ct);
        if (exchange is null)
            return Results.NotFound();

        var request = new SendHttpRequestToolRequest(
            ParentExchangeId: exchangeId,
            Method: exchange.RequestMethod,
            Url: exchange.RequestUrl,
            Headers: exchange.RequestHeaders,
            Cookies: exchange.RequestCookies,
            Body: exchange.RequestBody,
            ContentType: exchange.RequestContentType,
            FollowRedirects: true,
            ProxyId: null,
            TabTitle: $"Clone of {exchange.TabTitle}");

        return Results.Ok(request);
    }

    private static async Task<IResult> RenameExchange(
        [FromRoute] Guid exchangeId,
        [FromBody] RenameHttpExchangeRequest request,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        await repository.RenameExchangeAsync(exchangeId, request.TabTitle, ct);
        return Results.Ok();
    }

    private static async Task<IResult> PinExchange(
        [FromRoute] Guid exchangeId,
        [FromBody] PinHttpExchangeRequest request,
        [FromServices] IRequestToolRepository repository,
        CancellationToken ct)
    {
        await repository.PinExchangeAsync(exchangeId, request.IsPinned, ct);
        return Results.Ok();
    }

    private static async Task<IResult> SendRequest(
        [FromRoute] Guid sessionId,
        [FromBody] SendHttpRequestToolRequest request,
        [FromServices] ISessionService sessionService,
        [FromServices] IRequestToolRepository repository,
        [FromServices] IHttpReplayExecutor executor,
        CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(sessionId, ct);
        if (session is null)
            return Results.NotFound();

        var result = await executor.ExecuteAsync(sessionId, session.ProgramId, request, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CompareExchanges(
        [FromBody] CompareHttpExchangeRequest request,
        [FromServices] IRequestToolDiffService diffService,
        CancellationToken ct)
    {
        try
        {
            var result = await diffService.CompareAsync(request, ct);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }
    }

    private static async Task<IResult> GetRawRequest(
        [FromRoute] Guid exchangeId,
        [FromServices] IRequestToolRepository repository,
        [FromServices] IRawHttpRenderer renderer,
        [FromQuery] bool download = false,
        CancellationToken ct = default)
    {
        var exchange = await repository.GetExchangeAsync(exchangeId, ct);
        if (exchange is null)
            return Results.NotFound();

        var rawRequest = await renderer.RenderRequestAsync(ToRecord(exchange), ct);

        if (download)
        {
            return Results.Text(rawRequest, "text/plain", System.Text.Encoding.UTF8);
        }

        return Results.Ok(new RawHttpMessageDto(
            exchangeId,
            "text/plain",
            rawRequest,
            false,
            rawRequest.Length,
            null));
    }

    private static async Task<IResult> GetRawResponse(
        [FromRoute] Guid exchangeId,
        [FromServices] IRequestToolRepository repository,
        [FromServices] IRawHttpRenderer renderer,
        [FromQuery] bool download = false,
        CancellationToken ct = default)
    {
        var exchange = await repository.GetExchangeAsync(exchangeId, ct);
        if (exchange is null)
            return Results.NotFound();

        var rawResponse = await renderer.RenderResponseAsync(ToRecord(exchange), ct);

        if (download)
        {
            return Results.Text(rawResponse, "text/plain", System.Text.Encoding.UTF8);
        }

        return Results.Ok(new RawHttpMessageDto(
            exchangeId,
            "text/plain",
            rawResponse,
            false,
            rawResponse.Length,
            null));
    }

    private static HttpExchangeRecord ToRecord(HttpExchangeDetailDto dto) =>
        new()
        {
            ExchangeId = dto.ExchangeId,
            SessionId = dto.SessionId,
            AssetId = dto.AssetId,
            ProgramId = dto.ProgramId,
            ParentExchangeId = dto.ParentExchangeId,
            Origin = dto.Origin.ToString(),
            Outcome = dto.Outcome.ToString(),
            TabTitle = dto.TabTitle,
            IsPinned = dto.IsPinned,
            RequestMethod = dto.RequestMethod,
            RequestUrl = dto.RequestUrl,
            RequestScheme = dto.RequestScheme,
            RequestHost = dto.RequestHost,
            RequestPort = dto.RequestPort,
            RequestPath = dto.RequestPath,
            RequestQuery = dto.RequestQuery,
            RequestHttpVersion = dto.RequestHttpVersion,
            RequestHeaders = System.Text.Json.JsonSerializer.Serialize(dto.RequestHeaders),
            RequestCookies = System.Text.Json.JsonSerializer.Serialize(dto.RequestCookies),
            RequestBodyInline = dto.RequestBody,
            RequestBodyArtifactId = dto.RequestBodyArtifactId,
            RequestBodySha256 = dto.RequestBodySha256,
            RequestBodySizeBytes = dto.RequestBodySizeBytes,
            RequestContentType = dto.RequestContentType,
            ResponseStatusCode = dto.ResponseStatusCode,
            ResponseReasonPhrase = dto.ResponseReasonPhrase,
            ResponseHttpVersion = dto.ResponseHttpVersion,
            ResponseHeaders = dto.ResponseHeaders is not null ? System.Text.Json.JsonSerializer.Serialize(dto.ResponseHeaders) : null,
            ResponseCookies = dto.ResponseCookies is not null ? System.Text.Json.JsonSerializer.Serialize(dto.ResponseCookies) : null,
            ResponseBodyInline = dto.ResponseBody,
            ResponseBodyArtifactId = dto.ResponseBodyArtifactId,
            ResponseBodySha256 = dto.ResponseBodySha256,
            ResponseBodySizeBytes = dto.ResponseBodySizeBytes,
            ResponseContentType = dto.ResponseContentType,
            DurationMs = dto.DurationMs,
            RedirectChain = System.Text.Json.JsonSerializer.Serialize(dto.RedirectChain),
            TlsInfo = dto.TlsInfo is not null ? System.Text.Json.JsonSerializer.Serialize(dto.TlsInfo) : null,
            NetworkError = dto.NetworkError,
            ScopeStatus = dto.ScopeStatus.ToString(),
            RateLimitKey = dto.RateLimitKey,
            ProxyId = dto.ProxyId,
            RequestSha256 = null,
            ResponseSha256 = null,
            CreatedBy = null,
            CreatedAt = dto.CreatedAt,
            SentAt = dto.SentAt,
            CompletedAt = dto.CompletedAt
        };

    // ── Fuzz endpoints ────────────────────────────────────────────────────────

    private static async Task<IResult> CreateFuzzRun(
        [FromRoute] Guid sessionId,
        [FromBody] FuzzRunRequest request,
        [FromServices] IFuzzExecutor executor,
        CancellationToken ct)
    {
        try
        {
            var run = await executor.CreateRunAsync(sessionId, request, ct);
            return Results.Ok(new FuzzRunStatusDto(
                run.RunId, run.SessionId, run.Status,
                Enum.Parse<FuzzAttackType>(run.AttackType),
                run.TotalCount, run.CompletedCount,
                run.CreatedAt, run.StartedAt, run.CompletedAt));
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { Error = ex.Message }); }
    }

    private static async Task<IResult> GetSessionFuzzRuns(
        [FromRoute] Guid sessionId,
        [FromServices] IFuzzExecutor executor,
        CancellationToken ct)
    {
        var runs = await executor.GetSessionRunsAsync(sessionId, ct);
        return Results.Ok(runs);
    }

    private static async Task<IResult> GetFuzzRun(
        [FromRoute] Guid runId,
        [FromServices] IFuzzExecutor executor,
        CancellationToken ct)
    {
        var run = await executor.GetRunAsync(runId, ct);
        if (run is null) return Results.NotFound();
        return Results.Ok(new FuzzRunStatusDto(
            run.RunId, run.SessionId, run.Status,
            Enum.Parse<FuzzAttackType>(run.AttackType),
            run.TotalCount, run.CompletedCount,
            run.CreatedAt, run.StartedAt, run.CompletedAt));
    }

    private static async Task<IResult> GetFuzzResults(
        [FromRoute] Guid runId,
        [FromServices] IFuzzExecutor executor,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var page = await executor.GetResultsAsync(runId, offset, Math.Min(limit, 500), ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> StopFuzzRun(
        [FromRoute] Guid runId,
        [FromServices] IFuzzExecutor executor,
        CancellationToken ct)
    {
        await executor.StopRunAsync(runId, ct);
        return Results.Ok(new { stopped = true });
    }

    private static IResult GetBuiltInLists() =>
        Results.Ok(FuzzPayloadGenerator.GetBuiltInNames());
}