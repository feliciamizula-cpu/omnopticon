using Argus.Contracts.Assets;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TaskSeeder>();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<ScanOrchestratorDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddScoped<IScanPlanStore, EfScanPlanStore>();
}
else
{
    builder.Services.AddSingleton<IScanPlanStore, InMemoryScanPlanStore>();
}

var app = builder.Build();

await app.InitializeScanPlanStoreAsync();
app.MapDefaultEndpoints();

app.MapGet("/scan-plans", (IScanPlanStore store, CancellationToken cancellationToken) =>
    store.GetPlansAsync(cancellationToken));

app.MapPost("/scan-plans/domain-discovery", async (
    CreateDomainDiscoveryPlanRequest request,
    IScanPlanStore store,
    TaskSeeder taskSeeder,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Domain))
    {
        return Results.BadRequest("Domain is required.");
    }

    var plan = await store.CreateDomainDiscoveryPlanAsync(request, cancellationToken);
    var seeded = await taskSeeder.SeedAsync(plan, cancellationToken);
    plan = await store.MarkSeededAsync(plan.ScanPlanId, seeded, cancellationToken);

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

internal interface IScanPlanStore
{
    Task<IReadOnlyCollection<ScanPlanDto>> GetPlansAsync(CancellationToken cancellationToken);
    Task<ScanPlanDto> CreateDomainDiscoveryPlanAsync(CreateDomainDiscoveryPlanRequest request, CancellationToken cancellationToken);
    Task<ScanPlanDto> MarkSeededAsync(Guid scanPlanId, SeededScanPlan seeded, CancellationToken cancellationToken);
}

internal sealed class InMemoryScanPlanStore : IScanPlanStore
{
    private readonly ConcurrentDictionary<Guid, ScanPlanDto> _plans = new();

    public Task<IReadOnlyCollection<ScanPlanDto>> GetPlansAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ScanPlanDto> plans = _plans.Values
            .OrderByDescending(plan => plan.CreatedAt)
            .ToArray();

        return Task.FromResult(plans);
    }

    public Task<ScanPlanDto> CreateDomainDiscoveryPlanAsync(CreateDomainDiscoveryPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = ScanPlanFactory.CreateDomainDiscoveryPlan(request);
        _plans[plan.ScanPlanId] = plan;

        return Task.FromResult(plan);
    }

    public Task<ScanPlanDto> MarkSeededAsync(Guid scanPlanId, SeededScanPlan seeded, CancellationToken cancellationToken)
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

        return Task.FromResult(updated);
    }
}

internal sealed class EfScanPlanStore(ScanOrchestratorDbContext dbContext) : IScanPlanStore
{
    public async Task<IReadOnlyCollection<ScanPlanDto>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var records = await dbContext.ScanPlans
            .AsNoTracking()
            .OrderByDescending(plan => plan.CreatedAt)
            .ToArrayAsync(cancellationToken);

        return records.Select(plan => plan.ToDto()).ToArray();
    }

    public async Task<ScanPlanDto> CreateDomainDiscoveryPlanAsync(CreateDomainDiscoveryPlanRequest request, CancellationToken cancellationToken)
    {
        var dto = ScanPlanFactory.CreateDomainDiscoveryPlan(request);
        var record = ScanPlanRecord.FromDto(dto);

        dbContext.ScanPlans.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return record.ToDto();
    }

    public async Task<ScanPlanDto> MarkSeededAsync(Guid scanPlanId, SeededScanPlan seeded, CancellationToken cancellationToken)
    {
        var record = await dbContext.ScanPlans.FirstOrDefaultAsync(plan => plan.ScanPlanId == scanPlanId, cancellationToken);

        if (record is null)
        {
            throw new InvalidOperationException($"Scan plan {scanPlanId} was not found.");
        }

        record.State = "Seeded";
        record.SeededDomainAssetId = seeded.DomainAssetId;
        record.CreatedTaskIdsJson = JsonSerializer.Serialize(seeded.TaskIds, ScanPlanJson.Options);
        await dbContext.SaveChangesAsync(cancellationToken);

        return record.ToDto();
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

        using var assetResponse = await assetClient.PostAsJsonAsync("/assets", assetRequest, ScanPlanJson.Options, cancellationToken);
        assetResponse.EnsureSuccessStatusCode();
        var domainAsset = await assetResponse.Content.ReadFromJsonAsync<AssetDto>(ScanPlanJson.Options, cancellationToken);

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
                task.WorkerCapability,
                RequiredAssetType: null);

            using var taskResponse = await taskClient.PostAsJsonAsync("/tasks", request, ScanPlanJson.Options, cancellationToken);
            taskResponse.EnsureSuccessStatusCode();
            var createdTask = await taskResponse.Content.ReadFromJsonAsync<ReconTaskDto>(ScanPlanJson.Options, cancellationToken);

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

internal sealed class ScanOrchestratorDbContext(DbContextOptions<ScanOrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<ScanPlanRecord> ScanPlans => Set<ScanPlanRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var plan = modelBuilder.Entity<ScanPlanRecord>();
        plan.ToTable("scan_plans");
        plan.HasKey(record => record.ScanPlanId);
        plan.HasIndex(record => new { record.ProgramId, record.State });
        plan.HasIndex(record => record.CreatedAt);
        plan.Property(record => record.WorkflowType).HasMaxLength(128);
        plan.Property(record => record.Target).HasMaxLength(2048);
        plan.Property(record => record.State).HasMaxLength(64);
        plan.Property(record => record.PlannedTasksJson).HasColumnType("jsonb");
        plan.Property(record => record.CreatedTaskIdsJson).HasColumnType("jsonb");
    }
}

internal sealed class ScanPlanRecord
{
    public Guid ScanPlanId { get; set; }
    public string WorkflowType { get; set; } = string.Empty;
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = string.Empty;
    public string PlannedTasksJson { get; set; } = "[]";
    public Guid? SeededDomainAssetId { get; set; }
    public string CreatedTaskIdsJson { get; set; } = "[]";

    public static ScanPlanRecord FromDto(ScanPlanDto dto) =>
        new()
        {
            ScanPlanId = dto.ScanPlanId,
            WorkflowType = dto.WorkflowType,
            ProgramId = dto.ProgramId,
            ScopeId = dto.ScopeId,
            Target = dto.Target,
            CreatedAt = dto.CreatedAt,
            State = dto.State,
            PlannedTasksJson = JsonSerializer.Serialize(dto.PlannedTasks, ScanPlanJson.Options),
            SeededDomainAssetId = dto.SeededDomainAssetId,
            CreatedTaskIdsJson = JsonSerializer.Serialize(dto.CreatedTaskIds, ScanPlanJson.Options)
        };

    public ScanPlanDto ToDto() =>
        new(
            ScanPlanId,
            WorkflowType,
            ProgramId,
            ScopeId,
            Target,
            CreatedAt,
            State,
            JsonSerializer.Deserialize<ReconTaskSpec[]>(PlannedTasksJson, ScanPlanJson.Options) ?? [],
            SeededDomainAssetId,
            JsonSerializer.Deserialize<Guid[]>(CreatedTaskIdsJson, ScanPlanJson.Options) ?? []);
}

internal static class ScanPlanFactory
{
    public static ScanPlanDto CreateDomainDiscoveryPlan(CreateDomainDiscoveryPlanRequest request)
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

        return new ScanPlanDto(
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
    }

    private static ReconTaskSpec NewTaskSpec(
        string taskType,
        string workerCapability,
        CreateDomainDiscoveryPlanRequest request)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var payloadJson = workerCapability switch
        {
            "DnsResolverWorker" => $$"""{"host":"{{domain}}"}""",
            "HttpProbeWorker" => $$"""{"host":"{{domain}}"}""",
            "HtmlDomSpiderWorker" => $$"""{"url":"https://{{domain}}/"}""",
            "JsEndpointExtractorWorker" => $$"""{"url":"https://{{domain}}/static/app.js"}""",
            "WordlistDiscoveryWorker" => $$"""{"url":"https://{{domain}}/"}""",
            _ => $$"""{"domain":"{{domain}}"}"""
        };

        return new ReconTaskSpec(taskType, workerCapability, request.ProgramId, request.ScopeId, payloadJson);
    }
}

internal static class ScanPlanJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class ScanPlanStoreInitialization
{
    public static async Task InitializeScanPlanStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<ScanOrchestratorDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
        }
    }
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
