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

app.Run();
