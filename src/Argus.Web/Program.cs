using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapRazorComponents<Argus.Web.Components.Routes>()
    .AddInteractiveServerRenderMode();

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

app.MapGet("/ui/agent-chat/history", ProxyGetChatHistory);
app.MapPost("/ui/agent-chat", ProxyPostChat);

app.Run();

// Agent proxy handlers
async Task<IResult> ProxyGetAgent(IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, "/agents", ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostAgent(JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, "/agents", payload, ct);
}

async Task<IResult> ProxyGetAgentById(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agents/{agentId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPutAgent(Guid agentId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, $"/agents/{agentId}", payload, ct);
}

async Task<IResult> ProxyDeleteAgent(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agents/{agentId}", ct);
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode((int)response.StatusCode);
}

async Task<IResult> ProxyPauseAgent(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, $"/agents/{agentId}/pause", new JsonObject(), ct);
}

async Task<IResult> ProxyResumeAgent(Guid agentId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, $"/agents/{agentId}/resume", new JsonObject(), ct);
}

async Task<IResult> ProxyAssignTask(Guid agentId, string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, $"/agents/{agentId}/assign/{taskId}", new JsonObject(), ct);
}

async Task<IResult> ProxyGetTasks(string? status, string? priority, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var path = "/agent-tasks";
    if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(priority))
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={status}");
        if (!string.IsNullOrWhiteSpace(priority)) query.Add($"priority={priority}");
        path += "?" + string.Join("&", query);
    }
    var result = await gateway.GetJsonAsync(endpoints.Agent, path, ct);
    return Results.Json(result ?? new JsonObject());
}

async Task<IResult> ProxyPostTask(JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, "/agent-tasks", payload, ct);
}

async Task<IResult> ProxyGetTaskById(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var result = await gateway.GetJsonAsync(endpoints.Agent, $"/agent-tasks/{taskId}", ct);
    return result is not null ? Results.Json(result) : Results.NotFound();
}

async Task<IResult> ProxyPutTask(string taskId, JsonObject payload, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.Agent, $"/agent-tasks/{taskId}", payload, ct);
}

async Task<IResult> ProxyDeleteTask(string taskId, IHttpClientFactory httpClientFactory, CancellationToken ct)
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(endpoints.Agent);
    var response = await client.DeleteAsync($"/agent-tasks/{taskId}", ct);
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
            return path.Contains("assets", StringComparison.OrdinalIgnoreCase)
                ? JsonNode.Parse("""{"items":[],"page":1,"pageSize":100,"totalCount":0}""")
                : JsonNode.Parse("[]");
        }
    }

    public async Task<IResult> PostJsonAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
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