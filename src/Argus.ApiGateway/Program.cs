using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

var app = builder.Build();

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

MapService(app, "/programs", "https+http://program-scope-service");
MapService(app, "/scopes", "https+http://program-scope-service");
MapService(app, "/scope-validation", "https+http://program-scope-service");
MapService(app, "/assets", "https+http://asset-service");
MapService(app, "/tasks", "https+http://task-service");
MapService(app, "/rate-limits", "https+http://rate-limit-service");
MapService(app, "/scan-plans", "https+http://scan-orchestrator-service");
MapService(app, "/workflow-types", "https+http://scan-orchestrator-service");
MapService(app, "/events", "https+http://realtime-service");
MapService(app, "/workers", "https+http://realtime-service");

app.Run();

static void MapService(WebApplication app, string pathPrefix, string destinationPrefix)
{
    app.MapForwarder(pathPrefix, destinationPrefix);
    app.MapForwarder($"{pathPrefix}/{{**catch-all}}", destinationPrefix);
}
