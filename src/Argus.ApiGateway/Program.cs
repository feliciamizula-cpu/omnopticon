using Argus.ServiceDefaults;

const string CorsPolicyName = "AllowLocalUI";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpForwarder();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[]
    {
        "http://localhost:5173",
        "http://localhost:3000",
        "http://localhost:8080"
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();
var endpoints = ArgusServiceEndpoints.From(app.Configuration);

app.UseCors(CorsPolicyName);
app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    Name = "Argus API Gateway",
    Version = "1.0",
    Routes = new[]
    {
        new { Name = "programs", Path = "/programs", Service = "program-scope", Description = "Bug bounty programs" },
        new { Name = "scopes", Path = "/scopes", Service = "program-scope", Description = "Program scope management" },
        new { Name = "scope-validation", Path = "/scope-validation", Service = "program-scope", Description = "Scope validation endpoints" },
        new { Name = "targets", Path = "/targets", Service = "program-scope", Description = "Target management" },
        new { Name = "assets", Path = "/assets", Service = "asset", Description = "Asset discovery and management" },
        new { Name = "asset-types", Path = "/asset-types", Service = "asset", Description = "Asset type definitions" },
        new { Name = "findings", Path = "/findings", Service = "finding", Description = "Vulnerability findings" },
        new { Name = "artifacts", Path = "/artifacts", Service = "artifact", Description = "Scan artifacts" },
        new { Name = "tasks", Path = "/tasks", Service = "task", Description = "Task orchestration" },
        new { Name = "agents", Path = "/agents", Service = "agent", Description = "Development agent management" },
        new { Name = "agent-tasks", Path = "/agent-tasks", Service = "agent", Description = "Development agent task queue" },
        new { Name = "agent-chat", Path = "/agent-chat", Service = "agent", Description = "Development agent coordinator chat" },
        new { Name = "code-reviews", Path = "/code-reviews", Service = "agent", Description = "Agent-generated code reviews" },
        new { Name = "system-reports", Path = "/system-reports", Service = "agent", Description = "Agent-generated system reports" },
            new { Name = "workers", Path = "/workers", Service = "realtime", Description = "Worker management" },
            new { Name = "worker-types", Path = "/worker-types", Service = "realtime", Description = "Worker type definitions" },
            new { Name = "worker-subscriptions", Path = "/worker-subscriptions", Service = "realtime", Description = "Worker subscription management" },
            new { Name = "worker-scale-commands", Path = "/worker-scale-commands", Service = "realtime", Description = "Worker scaling command audit log" },
        new { Name = "events", Path = "/events", Service = "realtime", Description = "Event stream and subscriptions" },
        new { Name = "event-router", Path = "/event-router", Service = "event-router", Description = "Event routing configuration" },
        new { Name = "event-routes", Path = "/event-routes", Service = "event-router", Description = "Event route management" },
        new { Name = "rate-limits", Path = "/rate-limits", Service = "rate-limit", Description = "Rate limit configuration" },
        new { Name = "settings", Path = "/settings", Service = "program-scope", Description = "System settings" },
        new { Name = "provider-usage", Path = "/provider-usage", Service = "agent", Description = "Development provider usage monitoring" },
        new { Name = "request-tool", Path = "/request-tool", Service = "request-tool", Description = "HTTP request/response viewer and repeater" }
    }
})).RequireCors(CorsPolicyName);

MapService(app, "/programs", endpoints.ProgramScope);
MapService(app, "/scopes", endpoints.ProgramScope);
MapService(app, "/scope-validation", endpoints.ProgramScope);
MapService(app, "/targets", endpoints.ProgramScope);
MapService(app, "/assets", endpoints.Asset);
MapService(app, "/asset-types", endpoints.Asset);
MapService(app, "/artifacts", endpoints.Artifact);
MapService(app, "/findings", endpoints.Finding);
MapService(app, "/tasks", endpoints.Task);
MapService(app, "/agents", endpoints.Agent);
MapService(app, "/agent-tasks", endpoints.Agent);
MapService(app, "/agent-chat", endpoints.Agent);
MapService(app, "/code-reviews", endpoints.Agent);
MapService(app, "/system-reports", endpoints.Agent);
MapService(app, "/workers", endpoints.Realtime);
MapService(app, "/worker-types", endpoints.Realtime);
MapService(app, "/worker-subscriptions", endpoints.Realtime);
MapService(app, "/worker-scale-commands", endpoints.Realtime);
MapService(app, "/events", endpoints.Realtime);
MapService(app, "/event-router", endpoints.EventRouter);
MapService(app, "/event-routes", endpoints.EventRouter);
MapService(app, "/rate-limits", endpoints.RateLimit);
MapService(app, "/settings", endpoints.ProgramScope);
MapService(app, "/provider-usage", endpoints.Agent);
MapService(app, "/request-tool", endpoints.RequestTool);

app.Run();

static void MapService(WebApplication app, string pathPrefix, string destinationPrefix)
{
    app.MapForwarder(pathPrefix, destinationPrefix);
    app.MapForwarder($"{pathPrefix}/{{**catch-all}}", destinationPrefix);
}

internal sealed record ArgusServiceEndpoints(
    string ProgramScope,
    string Asset,
    string Artifact,
    string Finding,
    string Task,
    string Agent,
    string RateLimit,
    string Realtime,
    string EventRouter,
    string RequestTool)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) => new(
        configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? configuration["services:program-scope-service:http:0"] ?? "http://program-scope-service",
        configuration["ARGUS_ASSET_SERVICE"] ?? configuration["services:asset-service:http:0"] ?? "http://asset-service",
        configuration["ARGUS_ARTIFACT_SERVICE"] ?? configuration["services:artifact-service:http:0"] ?? "http://artifact-service",
        configuration["ARGUS_FINDING_SERVICE"] ?? configuration["services:finding-service:http:0"] ?? "http://finding-service",
        configuration["ARGUS_TASK_SERVICE"] ?? configuration["services:task-service:http:0"] ?? "http://task-service",
        configuration["ARGUS_AGENT_SERVICE"] ?? configuration["services:agent-service:http:0"] ?? "http://agent-service",
        configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? configuration["services:rate-limit-service:http:0"] ?? "http://rate-limit-service",
        configuration["ARGUS_REALTIME_SERVICE"] ?? configuration["services:realtime-service:http:0"] ?? "http://realtime-service",
        configuration["ARGUS_EVENT_ROUTER_SERVICE"] ?? configuration["services:event-router-service:http:0"] ?? "http://event-router-service",
        configuration["ARGUS_REQUEST_TOOL_SERVICE"] ?? configuration["services:request-tool-service:http:0"] ?? "http://request-tool-service");
}
