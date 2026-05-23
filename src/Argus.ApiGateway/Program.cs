using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpForwarder();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();
var endpoints = ArgusServiceEndpoints.From(app.Configuration);

app.UseCors();

app.MapDefaultEndpoints();

var allowedOrigins = new[] { "http://localhost:5173", "http://localhost:3000" };
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
        new { Name = "findings", Path = "/findings", Service = "asset", Description = "Vulnerability findings" },
        new { Name = "artifacts", Path = "/artifacts", Service = "asset", Description = "Scan artifacts" },
        new { Name = "tasks", Path = "/tasks", Service = "task", Description = "Task orchestration" },
        new { Name = "workers", Path = "/workers", Service = "realtime", Description = "Worker management" },
        new { Name = "worker-types", Path = "/worker-types", Service = "realtime", Description = "Worker type definitions" },
        new { Name = "worker-subscriptions", Path = "/worker-subscriptions", Service = "realtime", Description = "Worker subscription management" },
        new { Name = "events", Path = "/events", Service = "realtime", Description = "Event stream and subscriptions" },
        new { Name = "event-router", Path = "/event-router", Service = "realtime", Description = "Event routing configuration" },
        new { Name = "event-routes", Path = "/event-routes", Service = "realtime", Description = "Event route management" },
        new { Name = "rate-limits", Path = "/rate-limits", Service = "rate-limit", Description = "Rate limit configuration" },
        new { Name = "settings", Path = "/settings", Service = "program-scope", Description = "System settings" }
    }
})).RequireCors("AllowLocalUI");

app.MapForwarder("/health", "https+http://apisix-health");
app.MapForwarder("/healthz", "https+http://apisix-health");

MapService(app, "/programs", endpoints.ProgramScope);
MapService(app, "/scopes", endpoints.ProgramScope);
MapService(app, "/scope-validation", endpoints.ProgramScope);
MapService(app, "/targets", endpoints.ProgramScope);
MapService(app, "/assets", endpoints.Asset);
MapService(app, "/asset-types", endpoints.Asset);
MapService(app, "/artifacts", endpoints.Artifact);
MapService(app, "/findings", endpoints.Finding);
MapService(app, "/tasks", endpoints.Task);
MapService(app, "/workers", endpoints.Realtime);
MapService(app, "/worker-types", endpoints.Realtime);
MapService(app, "/worker-subscriptions", endpoints.Realtime);
MapService(app, "/events", endpoints.Realtime);
MapService(app, "/event-router", endpoints.Realtime);
MapService(app, "/event-routes", endpoints.Realtime);
MapService(app, "/rate-limits", endpoints.RateLimit);
MapService(app, "/settings", endpoints.ProgramScope);
MapService(app, "/agents", endpoints.Agent);
MapService(app, "/agent-tasks", endpoints.Agent);
MapService(app, "/agent-chat", endpoints.Agent);

app.Run();

static void MapService(WebApplication app, string pathPrefix, string destinationPrefix)
{
    app.MapForwarder(pathPrefix, destinationPrefix);
    app.MapForwarder($"{pathPrefix}/{{**catch-all}}", destinationPrefix);
}

internal sealed record ArgusServiceEndpoints(
    string Agent,
    string ProgramScope,
    string Asset,
    string Artifact,
    string Finding,
    string Task,
    string RateLimit,
    string Realtime)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) =>
        new(
            configuration["ARGUS_AGENT_SERVICE"] ?? "https+http://agent-service",
            configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? "https+http://program-scope-service",
            configuration["ARGUS_ASSET_SERVICE"] ?? "https+http://asset-service",
            configuration["ARGUS_ARTIFACT_SERVICE"] ?? "https+http://artifact-service",
            configuration["ARGUS_FINDING_SERVICE"] ?? "https+http://finding-service",
            configuration["ARGUS_TASK_SERVICE"] ?? "https+http://task-service",
            configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? "https+http://rate-limit-service",
            configuration["ARGUS_REALTIME_SERVICE"] ?? "https+http://realtime-service");
}
