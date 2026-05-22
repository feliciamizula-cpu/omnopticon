using Argus.Contracts.Tasks;
using Argus.Contracts.Assets;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ScanPlanStore>();
builder.Services.AddSingleton<TaskSeeder>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/scan-plans", (ScanPlanStore store) => store.GetPlans());

app.MapPost("/scan-plans/domain-discovery", async (
    CreateDomainDiscoveryPlanRequest request,
    ScanPlanStore store,
    TaskSeeder taskSeeder,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Domain))
    {
        return Results.BadRequest("Domain is required.");
    }

    var plan = store.CreateDomainDiscoveryPlan(request);
    var seeded = await taskSeeder.SeedAsync(plan, cancellationToken);
    plan = store.MarkSeeded(plan.ScanPlanId, seeded);

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
            NewTaskSpec("js-endpoint-extraction", "JsEndpointExtractorWorker", request),
            NewTaskSpec("wordlist-discovery", "WordlistDiscoveryWorker", request)
        };

        var plan = new ScanPlanDto(
            Guid.NewGuid(),
            "domain-discovery",
            request.ProgramId,
            request.ScopeId,
            request.Domain.Trim().ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            "Planned",
            tasks,
            SeededDomainAssetId: null,
            CreatedTaskIds: []);

        _plans[plan.ScanPlanId] = plan;

        return plan;
    }

    public ScanPlanDto MarkSeeded(Guid scanPlanId, SeededScanPlan seeded)
    {
        if (!_plans.TryGetValue(scanPlanId, out var plan))
        {
            throw new InvalidOperationException($"Scan plan {scanPlanId} was not found.");
        }

        var updated = plan with
        {
            State = "Seeded",
            SeededDomainAssetId = seeded.DomainAssetId,
            CreatedTaskIds = seeded.TaskIds
        };

        _plans[scanPlanId] = updated;

        return updated;
    }

    private static ReconTaskSpec NewTaskSpec(
        string taskType,
        string workerCapability,
        CreateDomainDiscoveryPlanRequest request)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var payloadJson = workerCapability switch
        {
            "DnsResolverWorker" => $"{{\"host\":\"{domain}\"}}",
            "HttpProbeWorker" => $"{{\"host\":\"{domain}\"}}",
            "HtmlDomSpiderWorker" => $"{{\"url\":\"https://{domain}/\"}}",
            "JsEndpointExtractorWorker" => $"{{\"url\":\"https://{domain}/static/app.js\"}}",
            "WordlistDiscoveryWorker" => $"{{\"url\":\"https://{domain}/\"}}",
            _ => $"{{\"domain\":\"{domain}\"}}"
        };

        return new ReconTaskSpec(taskType, workerCapability, request.ProgramId, request.ScopeId, payloadJson);
    }
}

internal sealed class TaskSeeder(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    private readonly Uri _assetServiceBaseAddress = GetUri(configuration["ARGUS_ASSET_SERVICE"], "http://asset-service");
    private readonly Uri _taskServiceBaseAddress = GetUri(configuration["ARGUS_TASK_SERVICE"], "http://task-service");

    public async Task<SeededScanPlan> SeedAsync(ScanPlanDto plan, CancellationToken cancellationToken)
    {
        var assetClient = CreateClient(_assetServiceBaseAddress);
        var assetRequest = new CreateAssetRequest(
            plan.ProgramId,
            plan.ScopeId,
            AssetType.Domain,
            plan.Target,
            Subtype: null,
            DiscoveredByTaskId: "scan-plan",
            Metadata: new Dictionary<string, string> { ["scan_plan_id"] = plan.ScanPlanId.ToString() },
            Tags: ["seed"]);

        using var assetResponse = await assetClient.PostAsJsonAsync("/assets", assetRequest, cancellationToken);
        assetResponse.EnsureSuccessStatusCode();
        var domainAsset = await assetResponse.Content.ReadFromJsonAsync<AssetDto>(cancellationToken);

        if (domainAsset is null)
        {
            throw new InvalidOperationException("AssetService returned an empty asset response.");
        }

        var taskClient = CreateClient(_taskServiceBaseAddress);
        var taskIds = new List<Guid>(plan.PlannedTasks.Count);

        foreach (var task in plan.PlannedTasks)
        {
            var request = new CreateReconTaskRequest(
                task.TaskType,
                task.ProgramId,
                task.ScopeId,
                domainAsset.AssetId,
                task.InputPayloadJson,
                task.WorkerCapability);

            using var taskResponse = await taskClient.PostAsJsonAsync("/tasks", request, cancellationToken);
            taskResponse.EnsureSuccessStatusCode();
            var createdTask = await taskResponse.Content.ReadFromJsonAsync<ReconTaskDto>(cancellationToken);

            if (createdTask is not null)
            {
                taskIds.Add(createdTask.TaskId);
            }
        }

        return new SeededScanPlan(domainAsset.AssetId, taskIds);
    }

    private HttpClient CreateClient(Uri baseAddress)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = baseAddress;
        return client;
    }

    private static Uri GetUri(string? configured, string fallback) =>
        Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : new Uri(fallback);
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
    IReadOnlyCollection<ReconTaskSpec> PlannedTasks,
    Guid? SeededDomainAssetId,
    IReadOnlyCollection<Guid> CreatedTaskIds);
internal sealed record ReconTaskSpec(
    string TaskType,
    string WorkerCapability,
    Guid ProgramId,
    Guid? ScopeId,
    string InputPayloadJson);
internal sealed record SeededScanPlan(Guid DomainAssetId, IReadOnlyCollection<Guid> TaskIds);
