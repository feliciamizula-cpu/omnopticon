using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.ServiceDefaults;
using Argus.Web;
using Argus.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Argus.Contracts.Assets;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    // Interactive-server Blazor calls the app's own /ui/* endpoints from the SERVER, so the base must
    // be the in-container Kestrel address (http://localhost:8080), not the browser-facing URL
    // (NavigationManager.BaseUri => :8082), which the server can't reach inside the container.
    var cfg = sp.GetRequiredService<IConfiguration>();
    var port = cfg["ASPNETCORE_HTTP_PORTS"] ?? "8080";
    return new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
});
builder.Services.AddMudServices();
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddHttpClient<WorkersApiClient>(client =>
    {
        // Worker endpoints (/workers, /worker-types/*, /worker-scale-commands) live on the realtime
        // service. Without this, the client inherited the web app's own base URL and 404'd.
        client.BaseAddress = new Uri(ArgusServiceEndpoints.From(builder.Configuration).Realtime);
    });

builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
    o.DetailedErrors = true);

builder.Services.AddSignalR();
builder.Services.AddScoped<DevelopmentRealtimeClient>();
builder.Services.AddSingleton<DevelopmentRealtimeNotifier>();
builder.Services.AddSingleton<ProviderUsageCacheWarmer>();
builder.Services.AddHostedService<ProviderUsageBackgroundRefresher>();
builder.Services.AddHostedService<AssetDeltaBroadcastService>();
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("redis") ?? "localhost:6379";
    options.InstanceName = "argus:web:";
});

var app = builder.Build();

app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    KnownIPNetworks = { },
    KnownProxies = { },
});

app.MapHub<ArgusHub>("/hubs/argus");
app.MapDefaultEndpoints();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/ui/state", async (IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);

    var assetsTask = gateway.GetJsonAsync(endpoints.Asset, "/assets?pageSize=200", cancellationToken);
    var tasksTask = gateway.GetJsonAsync(endpoints.Task, "/tasks", cancellationToken);
    var programsTask = gateway.GetJsonAsync(endpoints.ProgramScope, "/programs", cancellationToken);
    var scopesTask = gateway.GetJsonAsync(endpoints.ProgramScope, "/scopes", cancellationToken);
    var eventsTask = gateway.GetJsonAsync(endpoints.Realtime, "/events?take=80", cancellationToken);
    var workersTask = gateway.GetJsonAsync(endpoints.Realtime, "/workers", cancellationToken);
    var rateLimitsTask = gateway.GetJsonAsync(endpoints.RateLimit, "/rate-limits", cancellationToken);
    var scanPlansTask = gateway.GetJsonAsync(endpoints.ScanOrchestrator, "/scan-plans", cancellationToken);
    var webhooksTask = gateway.GetJsonAsync(endpoints.Realtime, "/webhooks", cancellationToken);

    await Task.WhenAll(assetsTask, tasksTask, programsTask, scopesTask, eventsTask, workersTask, rateLimitsTask, scanPlansTask, webhooksTask);

    return Results.Json(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        assets = assetsTask.Result,
        tasks = tasksTask.Result,
        programs = programsTask.Result,
        scopes = scopesTask.Result,
        events = eventsTask.Result,
        workers = workersTask.Result,
        rateLimits = rateLimitsTask.Result,
        scanPlans = scanPlansTask.Result,
        webhooks = webhooksTask.Result
    });
});

app.MapGet("/ui/assets/{assetId:guid}/relationships", async (
    Guid assetId,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var relationships = await gateway.GetJsonAsync(endpoints.Asset, $"/assets/{assetId}/relationships", cancellationToken);

    return Results.Json(relationships ?? new JsonArray());
});

app.MapGet("/ui/programs", async (
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.ProgramScope, "/programs", cancellationToken);
    return Results.Json(result ?? new JsonArray());
});

app.MapGet("/ui/programs/{programId:guid}", async (
    Guid programId,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.ProgramScope, $"/programs/{programId}", cancellationToken);
    return result is null ? Results.NotFound() : Results.Json(result);
});

app.MapPut("/ui/programs/{programId:guid}", async (
    Guid programId,
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PutJsonAsync(endpoints.ProgramScope, $"/programs/{programId}", payload, cancellationToken);
});

app.MapDelete("/ui/programs/{programId:guid}", async (
    Guid programId,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.DeleteJsonAsync(endpoints.ProgramScope, $"/programs/{programId}", cancellationToken);
});

app.MapDelete("/ui/programs/{programId:guid}/scopes/{scopeId:guid}", async (
    Guid programId,
    Guid scopeId,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    try
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(endpoints.ProgramScope);
        var response = await client.DeleteAsync($"/programs/{programId}/scopes/{scopeId}", cancellationToken);
        return Results.StatusCode((int)response.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Unable to reach program-scope service: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/ui/ops/assets", async (
    Guid? programId,
    string? type,
    string? status,
    string? search,
    string? tag,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("Argus.Web.OpsAssets");
    var gateway = new ArgusUiGateway(httpClientFactory, logger);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);

    // Use the throwing variant so a downstream failure surfaces as a real 502 the grid can show,
    // instead of a fabricated empty result that silently masks an asset-service outage.
    try
    {
        var items = new JsonArray();
        var page = 1;
        const int pageSize = 500;

        while (!cancellationToken.IsCancellationRequested)
        {
            var path = AssetProxyHelpers.BuildAssetsPath(page, pageSize, programId, type, status, search, tag);
            var result = await gateway.GetJsonOrThrowAsync(endpoints.Asset, path, cancellationToken);
            var pageItems = AssetProxyHelpers.ExtractItems(result);

            if (pageItems.Count == 0)
            {
                break;
            }

            foreach (var item in pageItems)
            {
                items.Add(item?.DeepClone());
            }

            if (pageItems.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return Results.Json(new JsonObject
        {
            ["items"] = items,
            ["page"] = 1,
            ["pageSize"] = items.Count,
            ["totalCount"] = items.Count
        });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Failed to load assets for program {ProgramId}", programId);
        return Results.Problem(
            title: "Asset service unavailable",
            detail: "The asset service could not be reached. See server logs for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/ui/programs", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.ProgramScope, "/programs", payload, cancellationToken);
});

app.MapPost("/ui/programs/{programId:guid}/scopes", async (
    Guid programId,
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    payload["programId"] = programId;
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);

    var scopeResult = await gateway.PostJsonRawAsync(endpoints.ProgramScope, $"/programs/{programId}/scopes", payload, cancellationToken);
    var scopeBody = await scopeResult.Content.ReadAsStringAsync(cancellationToken);
    var contentType = scopeResult.Content.Headers.ContentType?.ToString() ?? "application/json";

    if (scopeResult.IsSuccessStatusCode)
    {
        var scopeNode = JsonNode.Parse(scopeBody);
        var scopeType = scopeNode?["scopeType"]?.GetValue<string>() ?? payload["scopeType"]?.GetValue<string>() ?? "";
        var pattern = scopeNode?["pattern"]?.GetValue<string>() ?? payload["pattern"]?.GetValue<string>() ?? "";
        var scopeId = scopeNode?["scopeId"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(pattern) &&
            (string.Equals(scopeType, "domain", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(scopeType, "WildcardDomain", StringComparison.OrdinalIgnoreCase)))
        {
            var rootDomain = pattern.TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(rootDomain))
            {
                var assetPayload = new JsonObject
                {
                    ["programId"]          = programId,
                    ["scopeId"]            = scopeId,
                    ["type"]               = (int)Argus.Contracts.Assets.AssetType.Domain,
                    ["value"]              = rootDomain,
                    ["subtype"]            = "ScopeRoot",
                    ["confidence"]         = 1.0,
                    ["discoveredByTaskId"] = $"scope:{scopeId}",
                    ["tags"]               = new JsonArray("scope-root", "seed")
                };
                _ = Task.Run(async () =>
                {
                    try { await gateway.PostJsonAsync(endpoints.Asset, "/assets", assetPayload, CancellationToken.None); }
                    catch { }
                });
            }
        }
    }

    return Results.Content(scopeBody, contentType, statusCode: (int)scopeResult.StatusCode);
});

app.MapPost("/ui/scan-plans/domain-discovery", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.ScanOrchestrator, "/scan-plans/domain-discovery", payload, cancellationToken);
});

app.MapPost("/ui/assets/bulk/tag", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Asset, "/assets/bulk/tag", payload, cancellationToken);
});

app.MapPost("/ui/assets/bulk/enqueue", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Asset, "/assets/bulk/enqueue", payload, cancellationToken);
});

app.MapGet("/ui/asset-type-actions", async (
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.GetJsonAsync(endpoints.Asset, "/asset-type-actions", cancellationToken);
});

app.MapPost("/ui/asset-type-actions", async (JsonObject payload, IHttpClientFactory hcf, CancellationToken ct) =>
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = hcf.CreateClient(); client.BaseAddress = new Uri(endpoints.Asset);
    var resp = await client.PostAsJsonAsync("/asset-type-actions", payload, ct);
    return Results.StatusCode((int)resp.StatusCode);
});

app.MapPatch("/ui/asset-type-actions/{actionId:guid}", async (Guid actionId, JsonObject payload, IHttpClientFactory hcf, CancellationToken ct) =>
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = hcf.CreateClient(); client.BaseAddress = new Uri(endpoints.Asset);
    var resp = await client.PatchAsJsonAsync($"/asset-type-actions/{actionId}", payload, ct);
    return Results.StatusCode((int)resp.StatusCode);
});

app.MapDelete("/ui/asset-type-actions/{actionId:guid}", async (Guid actionId, IHttpClientFactory hcf, CancellationToken ct) =>
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = hcf.CreateClient(); client.BaseAddress = new Uri(endpoints.Asset);
    var resp = await client.DeleteAsync($"/asset-type-actions/{actionId}", ct);
    return Results.StatusCode((int)resp.StatusCode);
});

app.MapPost("/ui/assets", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Asset, "/assets", payload, cancellationToken);
});

app.MapPost("/ui/assets/bulk", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Asset, "/assets/bulk", payload, cancellationToken);
});

app.MapGet("/ui/webhooks/{id:guid}/logs", async (
    Guid id,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Realtime, $"/webhooks/{id}/logs", cancellationToken);
    return result is not null ? Results.Ok(result as object) : Results.NotFound();
});

app.MapPost("/ui/webhooks", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Realtime, "/webhooks", payload, cancellationToken);
});

app.MapPut("/ui/webhooks/{id:guid}", async (
    Guid id,
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Realtime, $"/webhooks/{id}", payload, cancellationToken);
});

app.MapDelete("/ui/webhooks/{id:guid}", async (
    Guid id,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    try
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(endpoints.Realtime);
        var response = await client.DeleteAsync($"/webhooks/{id}", cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Unable to reach backend service: {ex.Message}");
    }
});

app.MapGet("/ui/events/stream", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    string? lastEventId = null;

    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await gateway.GetJsonAsync(endpoints.Realtime, "/events?take=1", cancellationToken);
        var eventId = events is JsonArray { Count: > 0 } eventArray
            ? eventArray[0]?["eventId"]?.GetValue<string>()
            : null;

        if (!string.IsNullOrWhiteSpace(eventId) && eventId != lastEventId)
        {
            lastEventId = eventId;
            await context.Response.WriteAsync($"event: argus-event\n", cancellationToken);
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { eventId, observedAt = DateTimeOffset.UtcNow })}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        else
        {
            await context.Response.WriteAsync($": heartbeat {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});


app.MapPost("/ui/ops/send", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var method = payload["method"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "GET";
    var url = payload["url"]?.GetValue<string>()?.Trim();

    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return Results.BadRequest(new { message = "A valid absolute URL is required." });
    }

    if (uri.Scheme is not ("http" or "https"))
    {
        return Results.BadRequest(new { message = "Only http and https URLs are supported." });
    }

    var allowedMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    if (!allowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"HTTP method '{method}' is not allowed." });
    }

    var allowedHosts = (configuration["ARGUS_UI_OPS_ALLOWED_HOSTS"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (allowedHosts.Length > 0 && !allowedHosts.Any(pattern => HostMatches(uri.Host, pattern)))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var allowPrivate = bool.TryParse(configuration["ARGUS_UI_OPS_ALLOW_PRIVATE"], out var parsedAllowPrivate) && parsedAllowPrivate;
    if (!allowPrivate && await ResolvesToPrivateAddressAsync(uri, cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(int.TryParse(configuration["ARGUS_UI_OPS_TIMEOUT_SECONDS"], out var timeoutSeconds)
        ? Math.Clamp(timeoutSeconds, 1, 60)
        : 20);

    using var request = new HttpRequestMessage(new HttpMethod(method), uri);

    var body = payload["body"]?.GetValue<string>() ?? string.Empty;
    if (method is not ("GET" or "HEAD") || !string.IsNullOrEmpty(body))
    {
        request.Content = new StringContent(body, Encoding.UTF8);
    }

    if (payload["headers"] is JsonArray headers)
    {
        foreach (var headerNode in headers)
        {
            if (headerNode is not JsonArray pair || pair.Count < 2)
            {
                continue;
            }

            var name = pair[0]?.GetValue<string>()?.Trim();
            var value = pair[1]?.GetValue<string>() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) || IsBlockedHeader(name))
            {
                continue;
            }

            if (request.Content is not null && IsContentHeader(name))
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    try
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        stopwatch.Stop();

        const int maxBodyBytes = 256 * 1024;
        var truncated = bytes.Length > maxBodyBytes;
        var returnedBytes = truncated ? bytes[..maxBodyBytes] : bytes;
        var responseBody = Encoding.UTF8.GetString(returnedBytes);

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .Select(h => new[] { h.Key, string.Join(", ", h.Value) })
            .ToArray();

        return Results.Json(new
        {
            status = (int)response.StatusCode,
            statusText = response.ReasonPhrase ?? response.StatusCode.ToString(),
            headers = responseHeaders,
            body = responseBody,
            time = stopwatch.ElapsedMilliseconds,
            size = bytes.Length,
            truncated
        });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Unable to send request: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

// Agent management endpoints (BFF proxy)
app.MapGet("/ui/agents", ProxyGetAgent);
app.MapPost("/ui/agents", ProxyPostAgent);
app.MapGet("/ui/agents/{agentId:guid}", ProxyGetAgentById);
app.MapPut("/ui/agents/{agentId:guid}", ProxyPutAgent);
app.MapDelete("/ui/agents/{agentId:guid}", ProxyDeleteAgent);
app.MapPatch("/ui/agents/{agentId:guid}/pause", ProxyPauseAgent);
app.MapPatch("/ui/agents/{agentId:guid}/resume", ProxyResumeAgent);
app.MapPost("/ui/agents/{agentId:guid}/assign/{taskId}", ProxyAssignTask);

app.MapGet("/ui/agent-tasks", ProxyGetTasks);
app.MapPost("/ui/agent-tasks", ProxyPostTask);
app.MapGet("/ui/agent-tasks/{taskId}", ProxyGetTaskById);
app.MapPut("/ui/agent-tasks/{taskId}", ProxyPutTask);
app.MapDelete("/ui/agent-tasks/{taskId}", ProxyDeleteTask);
app.MapPost("/ui/agent-tasks/{taskId}/run", ProxyRunTask);

// Agent task schedules
app.MapGet   ("/ui/agent-tasks/{taskId}/schedules",           ProxyGetSchedulesForTask);
app.MapPost  ("/ui/agent-tasks/{taskId}/schedules",           ProxyPostScheduleForTask);
app.MapPut   ("/ui/agent-task-schedules/{scheduleId:guid}",   ProxyPutSchedule);
app.MapDelete("/ui/agent-task-schedules/{scheduleId:guid}",   ProxyDeleteSchedule);

// Agent task triggers
app.MapGet   ("/ui/agent-tasks/{taskId}/triggers",            ProxyGetTriggersForTask);
app.MapPost  ("/ui/agent-tasks/{taskId}/triggers",            ProxyPostTriggerForTask);
app.MapPut   ("/ui/agent-task-triggers/{triggerId:guid}",     ProxyPutTrigger);
app.MapDelete("/ui/agent-task-triggers/{triggerId:guid}",     ProxyDeleteTrigger);

// Agent task runs
app.MapGet   ("/ui/agent-tasks/{taskId}/runs",                ProxyGetRunsForTask);
app.MapGet   ("/ui/agents/{agentId:guid}/runs",               ProxyGetRunsForAgent);

app.MapGet("/ui/agent-chat/history", ProxyGetChatHistory);
app.MapPost("/ui/agent-chat", ProxyPostChat);

app.MapGet("/ui/todos", ProxyGetTodos);
app.MapPost("/ui/todos", ProxyPostTodo);
app.MapGet("/ui/todos/{todoId:guid}", ProxyGetTodoById);
app.MapPut("/ui/todos/{todoId:guid}", ProxyPutTodo);
app.MapDelete("/ui/todos/{todoId:guid}", ProxyDeleteTodo);

app.MapGet("/ui/code-reviews", ProxyGetCodeReviews);
app.MapGet("/ui/system-reports", ProxyGetSystemReports);

app.MapGet("/ui/request-tool/assets/{assetId:guid}/session", ProxyGetRequestToolSession);
app.MapPost("/ui/request-tool/assets/{assetId:guid}/session", ProxyCreateRequestToolSession);
app.MapGet("/ui/request-tool/sessions/{sessionId:guid}", ProxyGetRequestToolSessionById);
app.MapGet("/ui/request-tool/sessions/{sessionId:guid}/exchanges", ProxyGetRequestToolExchanges);
app.MapGet("/ui/request-tool/exchanges/{exchangeId:guid}", ProxyGetRequestToolExchange);
app.MapPost("/ui/request-tool/exchanges/{exchangeId:guid}/clone", ProxyCloneRequestToolExchange);
app.MapPatch("/ui/request-tool/exchanges/{exchangeId:guid}/title", ProxyRenameRequestToolExchange);
app.MapPatch("/ui/request-tool/exchanges/{exchangeId:guid}/pin", ProxyPinRequestToolExchange);
app.MapPost("/ui/request-tool/sessions/{sessionId:guid}/send", ProxySendRequestTool);
app.MapPost("/ui/request-tool/compare", ProxyCompareRequestTool);
app.MapGet("/ui/request-tool/exchanges/{exchangeId:guid}/raw-request", ProxyGetRawRequest);
app.MapGet("/ui/request-tool/exchanges/{exchangeId:guid}/raw-response", ProxyGetRawResponse);

app.MapGet("/ui/provider-usage", ProxyGetProviderUsage);
app.MapPost("/ui/provider-usage/{providerId}/login", ProxyLoginProvider);
app.MapGet("/ui/provider-usage/routing-preview", ProxyGetRoutingPreview);
app.MapGet("/ui/development-environment", async (IConfiguration configuration, CancellationToken ct) =>
{
    var summary = await DevelopmentEnvironmentApi.GetSummaryAsync(configuration, ct);
    return Results.Json(summary);
});
app.MapPost("/ui/development-environment/{action}", async (
    string action,
    IConfiguration configuration,
    DevelopmentRealtimeNotifier notifier,
    CancellationToken ct) =>
{
    var result = await DevelopmentEnvironmentApi.RunActionAsync(action, configuration, ct);
    await notifier.NotifyAsync("development-environment", action, ct);
    return Results.Json(result, statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});

app.MapPost("/ui/asset-change", async (JsonObject payload, DevelopmentRealtimeNotifier notifier, ILogger<Program> logger, CancellationToken ct) =>
{
    var action = payload["action"]?.GetValue<string>() ?? "";
    var assetId = payload["assetId"]?.GetValue<Guid>() ?? Guid.Empty;
    var assetType = payload["type"]?.GetValue<string>() ?? "";
    var value = payload["value"]?.GetValue<string>() ?? "";
    var riskScore = payload["riskScore"]?.GetValue<double?>();
    var firstSeenAt = payload["firstSeenAt"]?.GetValue<DateTimeOffset?>();
    var lastSeenAt = payload["lastSeenAt"]?.GetValue<DateTimeOffset?>();

    var delta = new AssetDelta(action, assetId, assetType, value, riskScore, firstSeenAt, lastSeenAt);
    await notifier.NotifyAssetDeltaAsync(delta, ct);
    logger.LogDebug("Asset delta broadcast: {Action} {AssetId}", action, assetId);
    return Results.Ok();
});

app.MapPost("/ui/processing/toggle", async (JsonObject payload, IHttpClientFactory hcf, IConfiguration configuration, ILogger<Program> logger, CancellationToken ct) =>
{
    var enabled = payload["enabled"]?.GetValue<bool>() ?? true;
    var endpoints = ArgusServiceEndpoints.From(configuration);
    var client = hcf.CreateClient();
    client.BaseAddress = new Uri(endpoints.Realtime);

    try
    {
        var endpoint = enabled ? "/worker-types/pause-all" : "/worker-types/resume-all";
        using var resp = await client.PostAsync(endpoint, null, ct);
        if (resp.IsSuccessStatusCode)
        {
            logger.LogInformation("Processing {State} via RealtimeService", enabled ? "paused" : "resumed");
            return Results.Ok(new { enabled });
        }
        logger.LogWarning("Failed to toggle processing: {Status}", resp.StatusCode);
        return Results.StatusCode((int)resp.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error toggling processing");
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/ui/processing/state", async (IHttpClientFactory hcf, IConfiguration configuration, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var endpoints = ArgusServiceEndpoints.From(configuration);
        var client = hcf.CreateClient();
        client.BaseAddress = new Uri(endpoints.Realtime);
        using var resp = await client.GetAsync("/worker-types/processing-state", ct);
        if (resp.IsSuccessStatusCode)
        {
            return Results.Ok(await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct));
        }
    }
    catch { }
    return Results.Ok(new { paused = false });
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Agent proxy handlers
async Task<IResult> ProxyGetAgent(IHttpClientFactory httpClientFactory, IDistributedCache cache, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await DevelopmentCache.GetOrCreateJsonAsync(
        cache,
        DevelopmentCache.Agents,
        () => gateway.GetJsonAsync(endpoints.Agent, "/agents", ct),
        ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostAgent(JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, "/agents", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agents", "created", ct);
    return result;
}

async Task<IResult> ProxyGetAgentById(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agents/{agentId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPutAgent(Guid agentId, JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PutJsonAsync(endpoints.Agent, $"/agents/{agentId}", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agents", "updated", ct);
    return result;
}

async Task<IResult> ProxyDeleteAgent(Guid agentId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agents/{agentId}", ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agents", "deleted", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

async Task<IResult> ProxyPauseAgent(Guid agentId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PatchJsonAsync(endpoints.Agent, $"/agents/{agentId}/pause", new JsonObject(), ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agents", "paused", ct);
    return result;
}

async Task<IResult> ProxyResumeAgent(Guid agentId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PatchJsonAsync(endpoints.Agent, $"/agents/{agentId}/resume", new JsonObject(), ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agents", "resumed", ct);
    return result;
}

async Task<IResult> ProxyAssignTask(Guid agentId, string taskId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, $"/agents/{agentId}/assign/{taskId}", new JsonObject(), ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Agents, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("agent-tasks", "assigned", ct);
    return result;
}

async Task<IResult> ProxyGetTasks(string? status, string? priority, IHttpClientFactory httpClientFactory, IDistributedCache cache, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var path = "/agent-tasks";
    if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(priority))
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(priority)) query.Add($"priority={Uri.EscapeDataString(priority)}");
        path += "?" + string.Join("&", query);
    }
    var result = await DevelopmentCache.GetOrCreateJsonAsync(
        cache,
        DevelopmentCache.QueryKey(DevelopmentCache.AgentTasks, ("status", status), ("priority", priority)),
        () => gateway.GetJsonAsync(endpoints.Agent, path, ct),
        ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostTask(JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, "/agent-tasks", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.AgentTasks, DevelopmentCache.Agents, DevelopmentCache.CodeReviews, DevelopmentCache.SystemReports);
    await notifier.NotifyAsync("agent-tasks", "created", ct);
    return result;
}

async Task<IResult> ProxyGetTaskById(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agent-tasks/{taskId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPutTask(string taskId, JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PutJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.AgentTasks, DevelopmentCache.Agents, DevelopmentCache.CodeReviews, DevelopmentCache.SystemReports);
    await notifier.NotifyAsync("agent-tasks", "updated", ct);
    return result;
}

async Task<IResult> ProxyDeleteTask(string taskId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agent-tasks/{taskId}", ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.AgentTasks, DevelopmentCache.Agents, DevelopmentCache.CodeReviews, DevelopmentCache.SystemReports);
    await notifier.NotifyAsync("agent-tasks", "deleted", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

async Task<IResult> ProxyRunTask(string taskId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/run", new JsonObject(), ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.AgentTasks, DevelopmentCache.Agents, DevelopmentCache.CodeReviews, DevelopmentCache.SystemReports);
    await notifier.NotifyAsync("agent-tasks", "run", ct);
    return result;
}

// Schedule proxy handlers
async Task<IResult> ProxyGetSchedulesForTask(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/schedules", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPostScheduleForTask(string taskId, JsonObject payload, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/schedules", payload, ct);
    await notifier.NotifyAsync("agent-task-schedules", "created", ct);
    return result;
}

async Task<IResult> ProxyPutSchedule(Guid scheduleId, JsonObject payload, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PutJsonAsync(endpoints.Agent, $"/agent-task-schedules/{scheduleId}", payload, ct);
    await notifier.NotifyAsync("agent-task-schedules", "updated", ct);
    return result;
}

async Task<IResult> ProxyDeleteSchedule(Guid scheduleId, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agent-task-schedules/{scheduleId}", ct);
    await notifier.NotifyAsync("agent-task-schedules", "deleted", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

// Trigger proxy handlers
async Task<IResult> ProxyGetTriggersForTask(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/triggers", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPostTriggerForTask(string taskId, JsonObject payload, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/triggers", payload, ct);
    await notifier.NotifyAsync("agent-task-triggers", "created", ct);
    return result;
}

async Task<IResult> ProxyPutTrigger(Guid triggerId, JsonObject payload, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PutJsonAsync(endpoints.Agent, $"/agent-task-triggers/{triggerId}", payload, ct);
    await notifier.NotifyAsync("agent-task-triggers", "updated", ct);
    return result;
}

async Task<IResult> ProxyDeleteTrigger(Guid triggerId, IHttpClientFactory httpClientFactory, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agent-task-triggers/{triggerId}", ct);
    await notifier.NotifyAsync("agent-task-triggers", "deleted", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

// Run proxy handlers
async Task<IResult> ProxyGetRunsForTask(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agent-tasks/{Uri.EscapeDataString(taskId)}/runs", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyGetRunsForAgent(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agents/{agentId}/runs", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyGetTodos(string? status, string? priority, IHttpClientFactory httpClientFactory, IDistributedCache cache, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var path = "/todos";
    var query = new List<string>();
    if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
    if (!string.IsNullOrWhiteSpace(priority)) query.Add($"priority={Uri.EscapeDataString(priority)}");
    if (query.Count > 0) path += "?" + string.Join("&", query);
    var result = await DevelopmentCache.GetOrCreateJsonAsync(
        cache,
        DevelopmentCache.QueryKey(DevelopmentCache.Todos, ("status", status), ("priority", priority)),
        () => gateway.GetJsonAsync(endpoints.Agent, path, ct),
        ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostTodo(JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, "/todos", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Todos, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("todos", "created", ct);
    return result;
}

async Task<IResult> ProxyGetTodoById(Guid todoId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/todos/{todoId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPutTodo(Guid todoId, JsonObject payload, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PutJsonAsync(endpoints.Agent, $"/todos/{todoId}", payload, ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Todos, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("todos", "updated", ct);
    return result;
}

async Task<IResult> ProxyDeleteTodo(Guid todoId, IHttpClientFactory httpClientFactory, IDistributedCache cache, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/todos/{todoId}", ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.Todos, DevelopmentCache.AgentTasks);
    await notifier.NotifyAsync("todos", "deleted", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

async Task<IResult> ProxyGetChatHistory(IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, "/agent-chat/history", ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostChat(JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, "/agent-chat", payload, ct);
}

async Task<IResult> ProxyGetCodeReviews(int? take, IHttpClientFactory httpClientFactory, IDistributedCache cache, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var path = $"/code-reviews?take={Math.Clamp(take ?? 100, 1, 500)}";
    var result = await DevelopmentCache.GetOrCreateJsonAsync(
        cache,
        DevelopmentCache.QueryKey(DevelopmentCache.CodeReviews, ("take", (take ?? 100).ToString())),
        () => gateway.GetJsonAsync(endpoints.Agent, path, ct),
        ct);
    return Results.Json(result ?? new JsonObject { ["items"] = new JsonArray(), ["count"] = 0 });
}

async Task<IResult> ProxyGetSystemReports(int? take, IHttpClientFactory httpClientFactory, IDistributedCache cache, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var path = $"/system-reports?take={Math.Clamp(take ?? 100, 1, 500)}";
    var result = await DevelopmentCache.GetOrCreateJsonAsync(
        cache,
        DevelopmentCache.QueryKey(DevelopmentCache.SystemReports, ("take", (take ?? 100).ToString())),
        () => gateway.GetJsonAsync(endpoints.Agent, path, ct),
        ct);
    return Results.Json(result ?? new JsonObject { ["items"] = new JsonArray(), ["count"] = 0 });
}

async Task<IResult> ProxyGetRequestToolSession(Guid assetId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/assets/{assetId}/session", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyCreateRequestToolSession(Guid assetId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.RequestTool, $"/request-tool/assets/{assetId}/session", payload, ct);
}

async Task<IResult> ProxyGetRequestToolSessionById(Guid sessionId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/sessions/{sessionId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyGetRequestToolExchanges(Guid sessionId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/sessions/{sessionId}/exchanges", ct);
    return Results.Json(result ?? new JsonArray());
}

async Task<IResult> ProxyGetRequestToolExchange(Guid exchangeId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyCloneRequestToolExchange(Guid exchangeId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}/clone", payload, ct);
}

async Task<IResult> ProxyRenameRequestToolExchange(Guid exchangeId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PatchJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}/title", payload, ct);
}

async Task<IResult> ProxyPinRequestToolExchange(Guid exchangeId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PatchJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}/pin", payload, ct);
}

async Task<IResult> ProxySendRequestTool(Guid sessionId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.RequestTool, $"/request-tool/sessions/{sessionId}/send", payload, ct);
}

async Task<IResult> ProxyCompareRequestTool(JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.RequestTool, "/request-tool/compare", payload, ct);
}

async Task<IResult> ProxyGetRawRequest(Guid exchangeId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}/raw-request", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyGetRawResponse(Guid exchangeId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.RequestTool, $"/request-tool/exchanges/{exchangeId}/raw-response", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyGetProviderUsage(IHttpClientFactory httpClientFactory, IDistributedCache cache, ProviderUsageCacheWarmer warmer, CancellationToken ct)
{
    var result = await DevelopmentCache.GetJsonAsync(cache, DevelopmentCache.ProviderUsage, ct);
    if (ProviderUsageDefaults.IsEmptyOverview(result))
    {
        var gateway = new ArgusUiGateway(httpClientFactory);
        var endpoints = ArgusServiceEndpoints.From(app.Configuration);
        var live = await gateway.GetJsonAsync(endpoints.Agent, "/provider-usage", ct);
        if (!ProviderUsageDefaults.IsEmptyOverview(live))
        {
            await DevelopmentCache.SetJsonAsync(cache, DevelopmentCache.ProviderUsage, live!, ct);
            result = live;
        }
    }

    warmer.QueueWarm();
    return Results.Json(result ?? ProviderUsageDefaults.EmptyOverview());
}

async Task<IResult> ProxyLoginProvider(string providerId, IHttpClientFactory httpClientFactory, IDistributedCache cache, ProviderUsageCacheWarmer warmer, DevelopmentRealtimeNotifier notifier, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.PostJsonAsync(endpoints.Agent, $"/provider-usage/{Uri.EscapeDataString(providerId)}/login", new JsonObject(), ct);
    await DevelopmentCache.RemoveAsync(cache, ct, DevelopmentCache.ProviderUsage, DevelopmentCache.ProviderRouting);
    await warmer.WarmAsync(forceRefresh: true, ct);
    return result;
}

async Task<IResult> ProxyGetRoutingPreview(IDistributedCache cache, ProviderUsageCacheWarmer warmer, CancellationToken ct)
{
    var result = await DevelopmentCache.GetJsonAsync(cache, DevelopmentCache.ProviderRouting, ct);
    warmer.QueueWarm();
    return Results.Json(result ?? new JsonObject
    {
        ["providerId"] = null,
        ["providerName"] = null,
        ["toolId"] = null,
        ["agentId"] = null,
        ["agentName"] = null,
        ["isRunnable"] = false,
        ["routingScore"] = 0,
        ["reason"] = "AgentService is unavailable."
    });
}

static bool HostMatches(string host, string pattern)
{
    host = host.Trim().TrimEnd('.').ToLowerInvariant();
    pattern = pattern.Trim().TrimEnd('.').ToLowerInvariant();

    if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
    {
        return true;
    }

    if (pattern.StartsWith("*."))
    {
        var suffix = pattern[1..];
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
    }

    return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}

static bool IsBlockedHeader(string headerName)
{
    var blocked = new[]
    {
        "Host",
        "Connection",
        "Content-Length",
        "Transfer-Encoding",
        "Keep-Alive",
        "Expect",
        "Upgrade",
        "Proxy-Authorization",
        "Proxy-Authenticate"
    };

    return blocked.Contains(headerName, StringComparer.OrdinalIgnoreCase);
}

static bool IsContentHeader(string headerName)
{
    var contentHeaders = new[]
    {
        "Content-Type",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Expires",
        "Last-Modified"
    };

    return contentHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase);
}

static async Task<bool> ResolvesToPrivateAddressAsync(Uri uri, CancellationToken cancellationToken)
{
    if (uri.HostNameType == UriHostNameType.Dns && IsLocalHostName(uri.Host))
    {
        return true;
    }

    IPAddress[] addresses;
    if (IPAddress.TryParse(uri.Host, out var parsedAddress))
    {
        addresses = new[] { parsedAddress };
    }
    else
    {
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    return addresses.Any(IsPrivateOrLoopback);
}

static bool IsLocalHostName(string host)
{
    return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
}

static bool IsPrivateOrLoopback(IPAddress address)
{
    if (IPAddress.IsLoopback(address))
    {
        return true;
    }

    if (address.IsIPv4MappedToIPv6)
    {
        address = address.MapToIPv4();
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] == 0
            || bytes[0] >= 224;
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.Equals(IPAddress.IPv6Loopback)
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.IPv6Any);
    }

    return false;
}

internal static class DevelopmentEnvironmentApi
{
    public static async Task<DevelopmentEnvironmentSummary> GetSummaryAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = DevelopmentEnvironmentSettings.From(configuration);
        var describe = await RunGcloudAsync(DescribeArgs(settings), cancellationToken);
        var node = TryParseJson(describe.Output);
        var status = node?["status"]?.GetValue<string>() ?? "unknown";
        var creationTimestamp = ReadDate(node, "creationTimestamp");
        var latestDeployment = BuildDeployment(node, configuration);
        var cost = BuildCost(settings, status, creationTimestamp, latestDeployment.DeployedAtValue, configuration);
        var externalIp = node?["networkInterfaces"]?.AsArray().FirstOrDefault()?["accessConfigs"]?.AsArray().FirstOrDefault()?["natIP"]?.GetValue<string>();

        return new DevelopmentEnvironmentSummary(
            settings.EnvironmentName,
            settings.ProjectId,
            settings.Zone,
            settings.VmName,
            settings.MachineType,
            settings.DiskGb,
            settings.DiskPolicy,
            NormalizeStatus(status, describe),
            settings.MutationsEnabled,
            cost,
            latestDeployment.Display,
            BuildComponents(settings, describe, status, cost),
            BuildServices(configuration, externalIp));
    }

    public static async Task<DevelopmentEnvironmentActionResult> RunActionAsync(string action, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = DevelopmentEnvironmentSettings.From(configuration);
        action = action.Trim().ToLowerInvariant();
        if (!new[] { "create", "start", "stop", "restart", "delete" }.Contains(action))
        {
            return new(false, action, "Unsupported environment action.", string.Empty);
        }

        if ((action is "create" or "start" or "restart") && settings.EstimatedHourlyUsd > settings.HourlyCapUsd)
        {
            return new(false, action, $"Refused: estimated ${settings.EstimatedHourlyUsd:0.00}/hour exceeds the ${settings.HourlyCapUsd:0.00}/hour cap.", BuildCommand(ActionArgs(action, settings)));
        }

        var args = ActionArgs(action, settings);
        var command = BuildCommand(args);
        if (!settings.MutationsEnabled)
        {
            return new(false, action, "Dry run only. Set ARGUS_DEVELOPMENT_ENVIRONMENT_ENABLE_MUTATIONS=true in the web runtime to permit GCP changes.", command);
        }

        var result = await RunGcloudAsync(args, cancellationToken);
        var message = result.ExitCode == 0
            ? $"{action} completed."
            : $"{action} failed: {TrimCommandOutput(result.Error)}";

        return new(result.ExitCode == 0, action, message, command);
    }

    private static string[] DescribeArgs(DevelopmentEnvironmentSettings settings) =>
    [
        "compute", "instances", "describe", settings.VmName,
        "--project", settings.ProjectId,
        "--zone", settings.Zone,
        "--format=json"
    ];

    private static string[] ActionArgs(string action, DevelopmentEnvironmentSettings settings) => action switch
    {
        "create" =>
        [
            "compute", "instances", "create", settings.VmName,
            "--project", settings.ProjectId,
            "--zone", settings.Zone,
            "--machine-type", settings.MachineType,
            "--boot-disk-size", $"{settings.DiskGb}GB",
            "--boot-disk-type", settings.DiskType,
            "--image-family", "debian-12",
            "--image-project", "debian-cloud",
            "--tags", "argus-development",
            "--labels", $"argus-environment=development,argus-hourly-cap={settings.HourlyCapUsd:0-00}",
            "--metadata", $"startup-script={StartupScript()}"
        ],
        "start" =>
        [
            "compute", "instances", "start", settings.VmName,
            "--project", settings.ProjectId,
            "--zone", settings.Zone
        ],
        "stop" =>
        [
            "compute", "instances", "stop", settings.VmName,
            "--project", settings.ProjectId,
            "--zone", settings.Zone,
            "--quiet"
        ],
        "restart" =>
        [
            "compute", "instances", "reset", settings.VmName,
            "--project", settings.ProjectId,
            "--zone", settings.Zone,
            "--quiet"
        ],
        "delete" =>
        [
            "compute", "instances", "delete", settings.VmName,
            "--project", settings.ProjectId,
            "--zone", settings.Zone,
            "--quiet"
        ],
        _ => []
    };

    private static DevelopmentEnvironmentCost BuildCost(
        DevelopmentEnvironmentSettings settings,
        string status,
        DateTimeOffset? createdAt,
        DateTimeOffset? deployedAt,
        IConfiguration configuration)
    {
        var runningHours = status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) && createdAt.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - createdAt.Value).TotalHours)
            : 0;
        var deploymentHours = status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) && deployedAt.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - deployedAt.Value).TotalHours)
            : 0;
        var explicitDeploymentSpend = ReadDecimal(configuration["ARGUS_DEVELOPMENT_DEPLOYMENT_SPEND_USD"]);
        var runningEstimate = (decimal)runningHours * settings.EstimatedHourlyUsd;
        var deploymentEstimate = explicitDeploymentSpend ?? (decimal)deploymentHours * settings.EstimatedHourlyUsd;
        var isOverCap = settings.EstimatedHourlyUsd > settings.HourlyCapUsd;

        return new DevelopmentEnvironmentCost(
            FormatUsd(settings.EstimatedHourlyUsd),
            FormatUsd(settings.HourlyCapUsd),
            FormatUsd(runningEstimate),
            FormatUsd(deploymentEstimate),
            isOverCap ? "over cap" : "within cap",
            explicitDeploymentSpend.HasValue ? "configured spend ledger" : "estimated from VM runtime",
            isOverCap);
    }

    private static (DevelopmentEnvironmentDeployment Display, DateTimeOffset? DeployedAtValue) BuildDeployment(JsonNode? node, IConfiguration configuration)
    {
        var metadata = node?["metadata"]?["items"]?.AsArray();
        var sha = configuration["ARGUS_DEVELOPMENT_DEPLOYMENT_SHA"] ?? MetadataValue(metadata, "argus-deploy-sha") ?? "unknown";
        var branch = configuration["ARGUS_DEVELOPMENT_DEPLOYMENT_BRANCH"] ?? MetadataValue(metadata, "argus-deploy-branch") ?? "unknown";
        var workflow = configuration["ARGUS_DEVELOPMENT_DEPLOYMENT_WORKFLOW"] ?? MetadataValue(metadata, "argus-deploy-workflow") ?? "cd-gcp-development";
        var deployedAtRaw = configuration["ARGUS_DEVELOPMENT_DEPLOYMENT_AT"] ?? MetadataValue(metadata, "argus-deploy-at");
        DateTimeOffset? deployedAt = DateTimeOffset.TryParse(deployedAtRaw, out var parsed) ? parsed : null;
        return (new DevelopmentEnvironmentDeployment(ShortSha(sha), branch, workflow, deployedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown"), deployedAt);
    }

    private static DevelopmentEnvironmentComponent[] BuildComponents(
        DevelopmentEnvironmentSettings settings,
        GcloudResult describe,
        string status,
        DevelopmentEnvironmentCost cost) =>
    [
        new("GCP VM", status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ? "running" : NormalizeStatus(status, describe), describe.ExitCode == 0 ? settings.VmName : TrimCommandOutput(describe.Error)),
        new("Cost guard", cost.IsOverCap ? "warning" : "healthy", $"{cost.EstimatedHourlyUsd} / {cost.HourlyCapUsd}"),
        new("Deploy workflow", "unknown", "Status comes from the latest development deployment metadata."),
        new("Agent stack", status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ? "unknown" : "stopped", "Health probes will report here once the VM exposes service URLs.")
    ];

    private static DevelopmentEnvironmentService[] BuildServices(IConfiguration configuration, string? externalIp)
    {
        var configured = configuration["ARGUS_DEVELOPMENT_SERVICE_URLS"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new DevelopmentEnvironmentService(parts[0], parts[1], "configured"))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(externalIp))
        {
            return
            [
                new("Argus Web", $"http://{externalIp}:8080", "unverified"),
                new("Aspire Dashboard", $"http://{externalIp}:18888", "unverified")
            ];
        }

        return [new("Argus Web", "pending", "VM external IP unavailable")];
    }

    private static async Task<GcloudResult> RunGcloudAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gcloud",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
            {
                return new(1, string.Empty, "Unable to start gcloud.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(1, string.Empty, ex.Message);
        }
    }

    private static JsonNode? TryParseJson(string value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? null : JsonNode.Parse(value);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDate(JsonNode? node, string propertyName) =>
        DateTimeOffset.TryParse(node?[propertyName]?.GetValue<string>(), out var value) ? value : null;

    private static string? MetadataValue(JsonArray? metadata, string key) =>
        metadata?.FirstOrDefault(item => item?["key"]?.GetValue<string>().Equals(key, StringComparison.OrdinalIgnoreCase) == true)?["value"]?.GetValue<string>();

    private static decimal? ReadDecimal(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string NormalizeStatus(string status, GcloudResult describe)
    {
        if (describe.ExitCode != 0)
        {
            return "unknown";
        }

        return string.IsNullOrWhiteSpace(status) ? "unknown" : status.ToLowerInvariant();
    }

    private static string FormatUsd(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static string ShortSha(string value) =>
        value.Length > 7 ? value[..7] : value;

    private static string TrimCommandOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No details.";
        }

        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length > 240 ? value[..240] + "..." : value;
    }

    private static string BuildCommand(string[] args) =>
        "gcloud " + string.Join(' ', args.Select(arg => arg.Contains(' ') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg));

    private static string StartupScript() =>
        "#!/usr/bin/env bash\nset -euo pipefail\napt-get update\napt-get install -y git docker.io docker-compose-plugin\nsystemctl enable --now docker\nmkdir -p /opt/argus\n";
}

internal static class AssetProxyHelpers
{
    public static string BuildAssetsPath(
        int page,
        int pageSize,
        Guid? programId,
        string? type,
        string? status,
        string? search,
        string? tag)
    {
        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (programId.HasValue) query.Add($"programId={programId.Value}");
        AddQuery(query, "type", type);
        AddQuery(query, "status", status);
        AddQuery(query, "search", search);
        AddQuery(query, "tag", tag);

        return "/assets?" + string.Join('&', query);
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={WebUtility.UrlEncode(value)}");
        }
    }

    public static JsonArray ExtractItems(JsonNode? result)
    {
        if (result is JsonArray array)
        {
            return array;
        }

        if (result is JsonObject obj && obj["items"] is JsonArray items)
        {
            return items;
        }

        return [];
    }

    public static int? ReadInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record DevelopmentEnvironmentSettings(
    string EnvironmentName,
    string ProjectId,
    string Zone,
    string VmName,
    string MachineType,
    int DiskGb,
    string DiskType,
    string DiskPolicy,
    decimal EstimatedHourlyUsd,
    decimal HourlyCapUsd,
    bool MutationsEnabled)
{
    public static DevelopmentEnvironmentSettings From(IConfiguration configuration)
    {
        var spendEnabled = !bool.TryParse(configuration["ARGUS_DEVELOPMENT_SPEND_ENABLED"], out var spend) || spend;
        var diskGb = int.TryParse(configuration["ARGUS_DEVELOPMENT_DISK_GB"], out var disk)
            ? Math.Max(30, disk)
            : spendEnabled ? 1024 : 30;
        var machineType = configuration["ARGUS_DEVELOPMENT_MACHINE_TYPE"] ?? (spendEnabled ? "e2-standard-8" : "e2-micro");
        var hourlyCap = decimal.TryParse(configuration["ARGUS_DEVELOPMENT_HOURLY_CAP_USD"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cap)
            ? cap
            : 2.00m;
        var estimatedHourly = decimal.TryParse(configuration["ARGUS_DEVELOPMENT_ESTIMATED_HOURLY_USD"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var estimate)
            ? estimate
            : spendEnabled ? 0.40m : 0.00m;
        var diskPolicy = spendEnabled
            ? "spend enabled; budget guard active"
            : "free-tier-safe default";

        return new(
            configuration["ARGUS_DEVELOPMENT_ENVIRONMENT_NAME"] ?? "development",
            configuration["ARGUS_DEVELOPMENT_PROJECT_ID"] ?? configuration["GCP_PROJECT_ID"] ?? "project-30b3b95e-ed2b-4573-98a",
            configuration["ARGUS_DEVELOPMENT_ZONE"] ?? "us-central1-a",
            configuration["ARGUS_DEVELOPMENT_VM_NAME"] ?? "argus-development-agents",
            machineType,
            diskGb,
            configuration["ARGUS_DEVELOPMENT_DISK_TYPE"] ?? "pd-standard",
            diskPolicy,
            estimatedHourly,
            hourlyCap,
            bool.TryParse(configuration["ARGUS_DEVELOPMENT_ENVIRONMENT_ENABLE_MUTATIONS"], out var enabled) && enabled);
    }
}

internal sealed record DevelopmentEnvironmentSummary(
    string EnvironmentName,
    string ProjectId,
    string Zone,
    string VmName,
    string MachineType,
    int DiskGb,
    string DiskPolicy,
    string Status,
    bool MutationsEnabled,
    DevelopmentEnvironmentCost Cost,
    DevelopmentEnvironmentDeployment LatestDeployment,
    DevelopmentEnvironmentComponent[] Components,
    DevelopmentEnvironmentService[] Services);

internal sealed record DevelopmentEnvironmentCost(
    string EstimatedHourlyUsd,
    string HourlyCapUsd,
    string RunningSessionUsd,
    string DeploymentSpendUsd,
    string CapStatus,
    string Source,
    bool IsOverCap);

internal sealed record DevelopmentEnvironmentDeployment(string Sha, string Branch, string Workflow, string DeployedAt);
internal sealed record DevelopmentEnvironmentComponent(string Name, string Status, string Detail);
internal sealed record DevelopmentEnvironmentService(string Name, string Url, string Health);
internal sealed record DevelopmentEnvironmentActionResult(bool Success, string Action, string Message, string Command);
internal sealed record GcloudResult(int ExitCode, string Output, string Error);


internal static class ProviderUsageDefaults
{
    public static JsonNode EmptyOverview() => JsonNode.Parse("""{"generatedAt":"1970-01-01T00:00:00Z","providers":[],"recommendedRoute":null}""")!;

    public static bool IsEmptyOverview(JsonNode? node)
    {
        if (node is null)
        {
            return true;
        }

        var providers = node["providers"];
        return providers is JsonArray { Count: 0 }
            && string.Equals(node["generatedAt"]?.GetValue<string>(), "1970-01-01T00:00:00Z", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ArgusUiGateway(IHttpClientFactory httpClientFactory, ILogger? logger = null)
{
    /// <summary>
    /// Like <see cref="GetJsonAsync"/> but never fabricates a fallback: it logs and rethrows on
    /// failure so the caller can surface a real error instead of a silent empty result.
    /// </summary>
    public async Task<JsonNode?> GetJsonOrThrowAsync(string baseAddress, string path, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            return await client.GetFromJsonAsync<JsonNode>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Argus UI gateway: downstream GET {BaseAddress}{Path} failed", baseAddress, path);
            throw;
        }
    }

    public async Task<JsonNode?> GetJsonAsync(string baseAddress, string path, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            return await client.GetFromJsonAsync<JsonNode>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Argus UI gateway: downstream GET {BaseAddress}{Path} failed; returning fallback", baseAddress, path);
            if (path.Contains("assets", StringComparison.OrdinalIgnoreCase))
            {
                return JsonNode.Parse("""{"items":[],"page":1,"pageSize":100,"totalCount":0}""");
            }

            if (path.Contains("provider-usage/routing-preview", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (path.Contains("provider-usage", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (path.Contains("agents", StringComparison.OrdinalIgnoreCase)
                || path.Contains("agent-tasks", StringComparison.OrdinalIgnoreCase)
                || path.Contains("agent-chat", StringComparison.OrdinalIgnoreCase)
                || path.Contains("todos", StringComparison.OrdinalIgnoreCase)
                || path.Contains("code-reviews", StringComparison.OrdinalIgnoreCase)
                || path.Contains("system-reports", StringComparison.OrdinalIgnoreCase))
            {
                return JsonNode.Parse("""{"items":[],"count":0}""");
            }

            return JsonNode.Parse("[]");
        }
    }

    public Task<IResult> PostJsonAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, baseAddress, path, payload, cancellationToken);

    public async Task<JsonNode?> PostJsonNodeAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public Task<IResult> PutJsonAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Put, baseAddress, path, payload, cancellationToken);

    public Task<IResult> PatchJsonAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, baseAddress, path, payload, cancellationToken);

    public async Task<IResult> DeleteJsonAsync(string baseAddress, string path, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            using var response = await client.DeleteAsync(path, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(content, contentType, statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem($"Unable to reach backend service: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> SendJsonAsync(HttpMethod method, string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(payload)
            };
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            return Results.Content(content, contentType, statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem($"Unable to reach backend service: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public async Task<HttpResponseMessage> PostJsonRawAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseAddress);
        return await client.PostAsJsonAsync(path, payload, cancellationToken);
    }

    public async Task<IResult> ResultFromResponse(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)response.StatusCode);
    }
}

internal sealed record ArgusServiceEndpoints(
    string Agent,
    string ProgramScope,
    string Asset,
    string Task,
    string RateLimit,
    string ScanOrchestrator,
    string Realtime,
    string RequestTool)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) =>
        new(
            configuration["ARGUS_AGENT_SERVICE"] ?? "https+http://agent-service",
            configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? "https+http://program-scope-service",
            configuration["ARGUS_ASSET_SERVICE"] ?? "https+http://asset-service",
            configuration["ARGUS_TASK_SERVICE"] ?? "https+http://task-service",
            configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? "https+http://rate-limit-service",
            configuration["ARGUS_SCAN_ORCHESTRATOR_SERVICE"] ?? "https+http://scan-orchestrator-service",
            configuration["ARGUS_REALTIME_SERVICE"] ?? "https+http://realtime-service",
            configuration["ARGUS_REQUEST_TOOL_SERVICE"] ?? "https+http://request-tool-service");
}
