using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpForwarder();

var app = builder.Build();
var endpoints = ArgusServiceEndpoints.From(app.Configuration);

app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    Name = "Argus API Gateway",
    Routes = new[]
    {
        new { Name = "program-scope", BasePath = "/programs, /scopes, /scope-validation/check" },
        new { Name = "assets", BasePath = "/assets" },
        new { Name = "tasks", BasePath = "/tasks" },
        new { Name = "rate-limits", BasePath = "/rate-limits" },
        new { Name = "scan-orchestrator", BasePath = "/scan-plans, /workflow-types" },
        new { Name = "realtime", BasePath = "/events, /workers" }
    }
}));

MapService(app, "/programs", endpoints.ProgramScope);
MapService(app, "/scopes", endpoints.ProgramScope);
MapService(app, "/scope-validation", endpoints.ProgramScope);
MapService(app, "/assets", endpoints.Asset);
MapService(app, "/tasks", endpoints.Task);
MapService(app, "/rate-limits", endpoints.RateLimit);
MapService(app, "/scan-plans", endpoints.ScanOrchestrator);
MapService(app, "/workflow-types", endpoints.ScanOrchestrator);
MapService(app, "/events", endpoints.Realtime);
MapService(app, "/workers", endpoints.Realtime);

app.Run();

static void MapService(WebApplication app, string pathPrefix, string destinationPrefix)
{
    app.MapForwarder(pathPrefix, destinationPrefix);
    app.MapForwarder($"{pathPrefix}/{{**catch-all}}", destinationPrefix);
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
