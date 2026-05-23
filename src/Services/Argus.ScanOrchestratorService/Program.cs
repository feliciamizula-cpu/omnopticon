using Argus.Contracts.Assets;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    builder.Services.AddScoped<IScanSchedulerStore, EfScanSchedulerStore>();
    builder.Services.AddHostedService<ScanSchedulerBackgroundService>();
}
else
{
    builder.Services.AddSingleton<IScanPlanStore, InMemoryScanPlanStore>();
    builder.Services.AddSingleton<IScanSchedulerStore, InMemoryScanSchedulerStore>();
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

RecurrencePolicyEndpoints.Map(app);

app.MapGet("/scheduled-scans", (IScanSchedulerStore store, CancellationToken cancellationToken) =>
    store.GetAllAsync(cancellationToken));

app.MapPost("/scheduled-scans", async (
    CreateScheduledScanRequest request,
    IScanSchedulerStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CronExpression))
        return Results.BadRequest("Cron expression is required.");
    try { Cronos.CronExpression.Parse(request.CronExpression); }
    catch (CronFormatException) { return Results.BadRequest("Invalid cron expression."); }
    var scheduled = await store.CreateAsync(request, cancellationToken);
    return Results.Created($"/scheduled-scans/{scheduled.ScheduledScanId}", scheduled);
});

app.MapDelete("/scheduled-scans/{scheduledScanId:guid}", async (Guid scheduledScanId, IScanSchedulerStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteAsync(scheduledScanId, cancellationToken);
    return Results.NoContent();
});

app.Run();

internal interface IScanSchedulerStore
{
    Task<IReadOnlyCollection<ScheduledScanDto>> GetAllAsync(CancellationToken cancellationToken);
    Task<ScheduledScanDto> CreateAsync(CreateScheduledScanRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid scheduledScanId, CancellationToken cancellationToken);
}

internal sealed record CreateScheduledScanRequest(Guid ProgramId, Guid? ScopeId, string Name, string WorkflowType, string CronExpression, int StalenessThreshold = 50);
internal sealed record ScheduledScanDto(Guid ScheduledScanId, Guid ProgramId, Guid? ScopeId, string Name, string WorkflowType, string CronExpression, int StalenessThreshold, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, DateTimeOffset CreatedAt, string State);

internal sealed class ScheduledScanRecord
{
    public Guid ScheduledScanId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public int StalenessThreshold { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = "Active";
    public ScheduledScanDto ToDto() => new(ScheduledScanId, ProgramId, ScopeId, Name, WorkflowType, CronExpression, StalenessThreshold, LastRunAt, NextRunAt, CreatedAt, State);
}

internal sealed class ScanSchedulerBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScanSchedulerBackgroundService> _logger;
    public ScanSchedulerBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ScanSchedulerBackgroundService> logger) { _scopeFactory = scopeFactory; _logger = logger; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessRecurringScanPlansAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error processing recurring scan plans"); }
            try { await ProcessScheduledScansAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error processing scheduled scans"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
    private async Task ProcessRecurringScanPlansAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ScanOrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var duePlans = await dbContext.ScanPlans
            .Where(p => p.CronExpression != null && p.State == "Seeded" && (!p.NextRunAt.HasValue || p.NextRunAt <= now))
            .ToListAsync(cancellationToken);
        foreach (var plan in duePlans)
        {
            try { await ProcessRecurringScanPlanAsync(plan, dbContext, cancellationToken); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to process recurring scan plan {PlanId}", plan.ScanPlanId); }
        }
    }
    private async Task ProcessRecurringScanPlanAsync(ScanPlanRecord plan, ScanOrchestratorDbContext dbContext, CancellationToken cancellationToken)
    {
        var cloned = new ScanPlanRecord
        {
            ScanPlanId = Guid.NewGuid(),
            WorkflowType = plan.WorkflowType,
            ProgramId = plan.ProgramId,
            ScopeId = plan.ScopeId,
            Target = plan.Target,
            CreatedAt = DateTimeOffset.UtcNow,
            State = "Planned",
            PlannedTasksJson = plan.PlannedTasksJson,
            CronExpression = plan.CronExpression,
            LastRunAt = null,
            NextRunAt = null,
            RecurrencePolicyId = plan.RecurrencePolicyId
        };
        dbContext.ScanPlans.Add(cloned);
        plan.LastRunAt = DateTimeOffset.UtcNow;
        plan.NextRunAt = Cronos.CronExpression.Parse(plan.CronExpression!).GetNextOccurrence(DateTimeOffset.UtcNow, true);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Recurring scan plan {PlanId} spawned clone {CloneId}, next run at {NextRun}", plan.ScanPlanId, cloned.ScanPlanId, plan.NextRunAt);
    }
    private async Task ProcessScheduledScansAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ScanOrchestratorDbContext>();
        var assetSvc = GetUri(scope.ServiceProvider.GetRequiredService<IConfiguration>()["ARGUS_ASSET_SERVICE"], "http://asset-service");
        var taskSvc = GetUri(scope.ServiceProvider.GetRequiredService<IConfiguration>()["ARGUS_TASK_SERVICE"], "http://task-service");
        var now = DateTimeOffset.UtcNow;
        var dueScans = await dbContext.ScheduledScans.Where(s => s.State == "Active" && (!s.NextRunAt.HasValue || s.NextRunAt <= now)).ToListAsync(cancellationToken);
        foreach (var scheduled in dueScans)
        {
            try { await ProcessScheduledScanAsync(scheduled, dbContext, assetSvc, taskSvc, cancellationToken); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to process scheduled scan {ScanId}", scheduled.ScheduledScanId); }
        }
    }
    private async Task ProcessScheduledScanAsync(ScheduledScanRecord scheduled, ScanOrchestratorDbContext dbContext, Uri assetSvc, Uri taskSvc, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = assetSvc };
        using var response = await client.GetAsync($"/assets?programId={scheduled.ProgramId}&minStalenessScore={scheduled.StalenessThreshold}&maxStalenessScore=100&sort=staleness_score&direction=desc&pageSize=100", cancellationToken);
        if (!response.IsSuccessStatusCode) { _logger.LogWarning("Failed to query assets for scheduled scan {ScanId}: {StatusCode}", scheduled.ScheduledScanId, response.StatusCode); return; }
        var assetsResult = await response.Content.ReadFromJsonAsync<PagedResult<AssetDto>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        if (assetsResult is null || assetsResult.Items.Count == 0) { scheduled.NextRunAt = Cronos.CronExpression.Parse(scheduled.CronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, true); await dbContext.SaveChangesAsync(cancellationToken); return; }
        using var taskClient = new HttpClient { BaseAddress = taskSvc };
        foreach (var asset in assetsResult.Items.Take(10))
        {
            var request = new CreateReconTaskRequest(TaskType: "http-probe", ProgramId: scheduled.ProgramId, ScopeId: scheduled.ScopeId, InputAssetId: asset.AssetId, InputPayloadJson: $$"""{"host":"{{asset.Value}}"}""", WorkerCapability: "HttpProbeWorker", RequiredAssetType: null, MaxAttempts: 2, Priority: WorkerPriority.High, DedupeHash: null);
            using var taskResponse = await taskClient.PostAsJsonAsync("/tasks", request, ScanPlanJson.Options, cancellationToken);
        }
        scheduled.LastRunAt = DateTimeOffset.UtcNow;
        scheduled.NextRunAt = Cronos.CronExpression.Parse(scheduled.CronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, true);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Scheduled scan {ScanId} triggered re-scan of {Count} assets", scheduled.ScheduledScanId, Math.Min(assetsResult.Items.Count, 10));
    }
    private static Uri GetUri(string? configured, string fallback) => Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : new Uri(fallback);
}

internal sealed class InMemoryScanSchedulerStore : IScanSchedulerStore
{
    private readonly ConcurrentDictionary<Guid, ScheduledScanDto> _scheduledScans = new();
    public Task<IReadOnlyCollection<ScheduledScanDto>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<ScheduledScanDto>>(_scheduledScans.Values.ToArray());
    public Task<ScheduledScanDto> CreateAsync(CreateScheduledScanRequest request, CancellationToken cancellationToken)
    {
        var dto = new ScheduledScanDto(ScheduledScanId: Guid.NewGuid(), ProgramId: request.ProgramId, ScopeId: request.ScopeId, Name: request.Name, WorkflowType: request.WorkflowType, CronExpression: request.CronExpression, StalenessThreshold: request.StalenessThreshold, LastRunAt: null, NextRunAt: Cronos.CronExpression.Parse(request.CronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, true), CreatedAt: DateTimeOffset.UtcNow, State: "Active");
        _scheduledScans[dto.ScheduledScanId] = dto;
        return Task.FromResult(dto);
    }
    public Task DeleteAsync(Guid scheduledScanId, CancellationToken cancellationToken) { _scheduledScans.TryRemove(scheduledScanId, out _); return Task.CompletedTask; }
}

internal sealed class EfScanSchedulerStore(ScanOrchestratorDbContext dbContext) : IScanSchedulerStore
{
    public async Task<IReadOnlyCollection<ScheduledScanDto>> GetAllAsync(CancellationToken cancellationToken) { var records = await dbContext.ScheduledScans.AsNoTracking().OrderByDescending(s => s.CreatedAt).ToArrayAsync(cancellationToken); return records.Select(s => s.ToDto()).ToArray(); }
    public async Task<ScheduledScanDto> CreateAsync(CreateScheduledScanRequest request, CancellationToken cancellationToken)
    {
        var dto = new ScheduledScanDto(ScheduledScanId: Guid.NewGuid(), ProgramId: request.ProgramId, ScopeId: request.ScopeId, Name: request.Name, WorkflowType: request.WorkflowType, CronExpression: request.CronExpression, StalenessThreshold: request.StalenessThreshold, LastRunAt: null, NextRunAt: Cronos.CronExpression.Parse(request.CronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, true), CreatedAt: DateTimeOffset.UtcNow, State: "Active");
        dbContext.ScheduledScans.Add(new ScheduledScanRecord { ScheduledScanId = dto.ScheduledScanId, ProgramId = dto.ProgramId, ScopeId = dto.ScopeId, Name = dto.Name, WorkflowType = dto.WorkflowType, CronExpression = dto.CronExpression, StalenessThreshold = dto.StalenessThreshold, LastRunAt = dto.LastRunAt, NextRunAt = dto.NextRunAt, CreatedAt = dto.CreatedAt, State = dto.State });
        await dbContext.SaveChangesAsync(cancellationToken);
        return dto;
    }
    public async Task DeleteAsync(Guid scheduledScanId, CancellationToken cancellationToken) { var record = await dbContext.ScheduledScans.FirstOrDefaultAsync(s => s.ScheduledScanId == scheduledScanId, cancellationToken); if (record is not null) { dbContext.ScheduledScans.Remove(record); await dbContext.SaveChangesAsync(cancellationToken); } }
}

internal interface IScanPlanStore
{
    Task<IReadOnlyCollection<ScanPlanDto>> GetPlansAsync(CancellationToken cancellationToken);
    Task<ScanPlanDto> CreateDomainDiscoveryPlanAsync(CreateDomainDiscoveryPlanRequest request, CancellationToken cancellationToken);
    Task<ScanPlanDto> MarkSeededAsync(Guid scanPlanId, SeededScanPlan seeded, CancellationToken cancellationToken);
    Task<ScanPlanDto?> GetPlanByIdAsync(Guid scanPlanId, CancellationToken cancellationToken);
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
                RequiredAssetType: null,
                Priority: task.Priority);

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

internal sealed class RecurrencePolicyRecord
{
    public Guid RecurrencePolicyId { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ScanOrchestratorDbContext(DbContextOptions<ScanOrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<ScanPlanRecord> ScanPlans => Set<ScanPlanRecord>();
    public DbSet<ScheduledScanRecord> ScheduledScans => Set<ScheduledScanRecord>();
    public DbSet<RecurrencePolicyRecord> RecurrencePolicies => Set<RecurrencePolicyRecord>();

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
        plan.Property(record => record.CronExpression).HasMaxLength(128);
        plan.HasIndex(record => record.NextRunAt);

        var recurrence = modelBuilder.Entity<RecurrencePolicyRecord>();
        recurrence.ToTable("recurrence_policies");
        recurrence.HasKey(r => r.RecurrencePolicyId);
        recurrence.Property(r => r.CronExpression).HasMaxLength(128).IsRequired();
        recurrence.Property(r => r.Description).HasMaxLength(512);
        recurrence.Property(r => r.WorkflowType).HasMaxLength(128);
        recurrence.Property(r => r.IsActive).IsRequired();
        recurrence.HasIndex(r => r.IsActive);

        var scheduled = modelBuilder.Entity<ScheduledScanRecord>();
        scheduled.ToTable("scheduled_scans");
        scheduled.HasKey(record => record.ScheduledScanId);
        scheduled.HasIndex(record => new { record.ProgramId, record.State });
        scheduled.Property(record => record.Name).HasMaxLength(256);
        scheduled.Property(record => record.WorkflowType).HasMaxLength(128);
        scheduled.Property(record => record.CronExpression).HasMaxLength(128);
        scheduled.Property(record => record.State).HasMaxLength(64);
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
    public string? CronExpression { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public Guid? RecurrencePolicyId { get; set; }

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
            CreatedTaskIdsJson = JsonSerializer.Serialize(dto.CreatedTaskIds, ScanPlanJson.Options),
            CronExpression = dto.CronExpression,
            LastRunAt = dto.LastRunAt,
            NextRunAt = dto.NextRunAt,
            RecurrencePolicyId = dto.RecurrencePolicyId
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
            JsonSerializer.Deserialize<Guid[]>(CreatedTaskIdsJson, ScanPlanJson.Options) ?? [],
            CronExpression,
            LastRunAt,
            NextRunAt,
            RecurrencePolicyId);
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
            CreatedTaskIds: [],
            CronExpression: request.CronExpression,
            LastRunAt: null,
            NextRunAt: request.CronExpression is not null ? Cronos.CronExpression.Parse(request.CronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, true) : null,
            RecurrencePolicyId: null);
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

        var priority = workerCapability switch
        {
            "HttpProbeWorker" => WorkerPriority.High,
            "WordlistDiscoveryWorker" => WorkerPriority.Low,
            _ => WorkerPriority.Normal
        };

        return new ReconTaskSpec(taskType, workerCapability, request.ProgramId, request.ScopeId, payloadJson, priority);
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

internal static class RecurrencePolicyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/recurrence-policies", async (ScanOrchestratorDbContext db, CancellationToken ct) =>
        {
            var policies = await db.RecurrencePolicies
                .AsNoTracking()
                .Where(r => r.IsActive)
                .OrderByDescending(r => r.CreatedAt)
                .ToArrayAsync(ct);
            return Results.Ok(policies);
        });

        app.MapPost("/recurrence-policies", async (CreateRecurrencePolicyRequest request, ScanOrchestratorDbContext db, CancellationToken ct) =>
        {
            try { Cronos.CronExpression.Parse(request.CronExpression); }
            catch (CronFormatException) { return Results.BadRequest("Invalid cron expression."); }

            if (string.IsNullOrWhiteSpace(request.WorkflowType))
                return Results.BadRequest("WorkflowType is required.");

            var policy = new RecurrencePolicyRecord
            {
                RecurrencePolicyId = Guid.NewGuid(),
                CronExpression = request.CronExpression.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                WorkflowType = request.WorkflowType.Trim(),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.RecurrencePolicies.Add(policy);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/recurrence-policies/{policy.RecurrencePolicyId}", policy);
        });

        app.MapDelete("/recurrence-policies/{policyId:guid}", async (Guid policyId, ScanOrchestratorDbContext db, CancellationToken ct) =>
        {
            var policy = await db.RecurrencePolicies.FirstOrDefaultAsync(r => r.RecurrencePolicyId == policyId, ct);
            if (policy is null) return Results.NotFound();
            db.RecurrencePolicies.Remove(policy);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}

internal sealed record CreateRecurrencePolicyRequest(string CronExpression, string Description, string WorkflowType);
internal sealed record CreateDomainDiscoveryPlanRequest(Guid ProgramId, Guid? ScopeId, string Domain, string? CronExpression = null);
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
    IReadOnlyCollection<Guid> CreatedTaskIds,
    string? CronExpression = null,
    DateTimeOffset? LastRunAt = null,
    DateTimeOffset? NextRunAt = null,
    Guid? RecurrencePolicyId = null);
internal sealed record ReconTaskSpec(
    string TaskType,
    string WorkerCapability,
    Guid ProgramId,
    Guid? ScopeId,
    string InputPayloadJson,
    WorkerPriority Priority = WorkerPriority.Normal);
internal sealed record SeededScanPlan(Guid DomainAssetId, IReadOnlyCollection<Guid> TaskIds);
