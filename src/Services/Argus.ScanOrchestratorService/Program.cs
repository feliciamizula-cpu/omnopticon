using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ScanPlanStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/scan-plans", (ScanPlanStore store) => store.GetPlans());

app.MapPost("/scan-plans/domain-discovery", (CreateDomainDiscoveryPlanRequest request, ScanPlanStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Domain))
    {
        return Results.BadRequest("Domain is required.");
    }

    var plan = store.CreateDomainDiscoveryPlan(request);
    return Results.Created($"/scan-plans/{plan.ScanPlanId}", plan);
});

app.MapGet("/workflow-types", () => new[]
{
    new WorkflowTypeDto(
        "domain-discovery",
        "Domain -> subdomain -> URL -> content discovery",
        [
            "DomainAssetCreated",
            "SubdomainEnumerationRequested",
            "SubdomainAssetCreated",
            "HttpProbeRequested",
            "UrlAssetCreated",
            "HtmlExtractionRequested",
            "JavaScriptExtractionRequested"
        ])
});

app.Run();

internal sealed class ScanPlanStore
{
    private readonly ConcurrentDictionary<Guid, ScanPlanDto> _plans = new();

    public IReadOnlyCollection<ScanPlanDto> GetPlans() =>
        _plans.Values
            .OrderByDescending(plan => plan.CreatedAt)
            .ToArray();

    public ScanPlanDto CreateDomainDiscoveryPlan(CreateDomainDiscoveryPlanRequest request)
    {
        var tasks = new[]
        {
            NewTaskSpec("amass-enumeration", "AmassWorker", request),
            NewTaskSpec("subfinder-enumeration", "SubfinderWorker", request),
            NewTaskSpec("dns-resolution", "DnsResolverWorker", request),
            NewTaskSpec("http-probe", "HttpProbeWorker", request),
            NewTaskSpec("html-dom-spider", "HtmlDomSpiderWorker", request),
            NewTaskSpec("js-endpoint-extraction", "JsEndpointExtractorWorker", request)
        };

        var plan = new ScanPlanDto(
            Guid.NewGuid(),
            "domain-discovery",
            request.ProgramId,
            request.ScopeId,
            request.Domain.Trim().ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            "Planned",
            tasks);

        _plans[plan.ScanPlanId] = plan;

        return plan;
    }

    private static ReconTaskSpec NewTaskSpec(
        string taskType,
        string workerCapability,
        CreateDomainDiscoveryPlanRequest request) =>
        new(taskType, workerCapability, request.ProgramId, request.ScopeId, $"{{\"domain\":\"{request.Domain.Trim().ToLowerInvariant()}\"}}");
}

internal sealed record CreateDomainDiscoveryPlanRequest(Guid ProgramId, Guid? ScopeId, string Domain);
internal sealed record WorkflowTypeDto(string WorkflowType, string Description, IReadOnlyCollection<string> States);
internal sealed record ScanPlanDto(
    Guid ScanPlanId,
    string WorkflowType,
    Guid ProgramId,
    Guid? ScopeId,
    string Target,
    DateTimeOffset CreatedAt,
    string State,
    IReadOnlyCollection<ReconTaskSpec> PlannedTasks);
internal sealed record ReconTaskSpec(
    string TaskType,
    string WorkerCapability,
    Guid ProgramId,
    Guid? ScopeId,
    string InputPayloadJson);
