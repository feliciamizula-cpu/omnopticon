using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddRazorComponents();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapRazorComponents<Argus.Web.Components.Routes>();

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

app.Run();

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
    string ProgramScope,
    string Asset,
    string Task,
    string RateLimit,
    string ScanOrchestrator,
    string Realtime)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) =>
        new(
            configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? "https+http://program-scope-service",
            configuration["ARGUS_ASSET_SERVICE"] ?? "https+http://asset-service",
            configuration["ARGUS_TASK_SERVICE"] ?? "https+http://task-service",
            configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? "https+http://rate-limit-service",
            configuration["ARGUS_SCAN_ORCHESTRATOR_SERVICE"] ?? "https+http://scan-orchestrator-service",
            configuration["ARGUS_REALTIME_SERVICE"] ?? "https+http://realtime-service");
}