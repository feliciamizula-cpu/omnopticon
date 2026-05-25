using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.ServiceDefaults;
using Argus.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddScoped<DevelopmentRealtimeClient>();
builder.Services.AddSingleton<DevelopmentRealtimeNotifier>();
builder.Services.AddSingleton<ProviderUsageCacheWarmer>();
builder.Services.AddHostedService<ProviderUsageBackgroundRefresher>();
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("redis") ?? "localhost:6379";
    options.InstanceName = "argus:web:";
});

var app = builder.Build();

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
    return await gateway.PostJsonAsync(endpoints.ProgramScope, $"/programs/{programId}/scopes", payload, cancellationToken);
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

app.MapGet("/ui/agent-chat/history", ProxyGetChatHistory);
app.MapPost("/ui/agent-chat", ProxyPostChat);

app.MapGet("/ui/todos", ProxyGetTodos);
app.MapPost("/ui/todos", ProxyPostTodo);
app.MapGet("/ui/todos/{todoId:guid}", ProxyGetTodoById);
app.MapPut("/ui/todos/{todoId:guid}", ProxyPutTodo);
app.MapDelete("/ui/todos/{todoId:guid}", ProxyDeleteTodo);

app.MapGet("/ui/code-reviews", ProxyGetCodeReviews);
app.MapGet("/ui/system-reports", ProxyGetSystemReports);

app.MapGet("/ui/provider-usage", ProxyGetProviderUsage);
app.MapPost("/ui/provider-usage/{providerId}/login", ProxyLoginProvider);
app.MapGet("/ui/provider-usage/routing-preview", ProxyGetRoutingPreview);

app.MapGet("/ui/programs", async (
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.ProgramScope, "/programs", cancellationToken);
    return Results.Json(result ?? new JsonArray());
});

app.MapGet("/ui/ops/assets", async (
    Guid? programId,
    int? take,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var pageSize = Math.Clamp(take ?? 500, 1, 1000);
    var path = $"/assets?pageSize={pageSize}";
    if (programId.HasValue) path += $"&programId={programId}";
    var result = await gateway.GetJsonAsync(endpoints.Asset, path, cancellationToken);
    return Results.Json(result ?? new JsonObject { ["items"] = new JsonArray(), ["totalCount"] = 0 });
});

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

async Task<IResult> ProxyGetProviderUsage(IDistributedCache cache, ProviderUsageCacheWarmer warmer, CancellationToken ct)
{
    var result = await DevelopmentCache.GetJsonAsync(cache, DevelopmentCache.ProviderUsage, ct);
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


internal static class ProviderUsageDefaults
{
    public static JsonNode EmptyOverview() => JsonNode.Parse("""{"generatedAt":"1970-01-01T00:00:00Z","providers":[],"recommendedRoute":null}""")!;
}

internal sealed class ArgusUiGateway(IHttpClientFactory httpClientFactory)
{
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
            _ = ex;
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
                return ProviderUsageDefaults.EmptyOverview();
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
}

internal sealed record ArgusServiceEndpoints(
    string Agent,
    string ProgramScope,
    string Asset,
    string Task,
    string RateLimit,
    string ScanOrchestrator,
    string Realtime)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) =>
        new(
            configuration["ARGUS_AGENT_SERVICE"] ?? "https+http://agent-service",
            configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? "https+http://program-scope-service",
            configuration["ARGUS_ASSET_SERVICE"] ?? "https+http://asset-service",
            configuration["ARGUS_TASK_SERVICE"] ?? "https+http://task-service",
            configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? "https+http://rate-limit-service",
            configuration["ARGUS_SCAN_ORCHESTRATOR_SERVICE"] ?? "https+http://scan-orchestrator-service",
            configuration["ARGUS_REALTIME_SERVICE"] ?? "https+http://realtime-service");
}
