using Argus.Contracts.Assets;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
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
builder.Services.AddSingleton<IScanPlanStore, InMemoryScanPlanStore>();
builder.Services.AddSingleton<IScanSchedulerStore, InMemoryScanSchedulerStore>();

var app = builder.Build();
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

app.MapGet("/scheduled-scans", (IScanSchedulerStore store, CancellationToken cancellationToken) =>
    store.GetAllAsync(cancellationToken));

app.MapPost("/scheduled-scans", async (
    CreateScheduledScanRequest request,
    IScanSchedulerStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CronExpression))
        return Results.BadRequest("Cron expression is required.");
    if (!CronExpression.Validate(request.CronExpression))
        return Results.BadRequest("Invalid cron expression.");
    var scheduled = await store.CreateAsync(request, cancellationToken);
    return Results.Created($"/scheduled-scans/{scheduled.ScheduledScanId}", scheduled);
});

app.MapDelete("/scheduled-scans/{scheduledScanId:guid}", async (Guid scheduledScanId, IScanSchedulerStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteAsync(scheduledScanId, cancellationToken);
    return Results.NoContent();
});

app.Run();

internal static class CronExpression
{
    public static bool Validate(string expression)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts.All(ValidateField);
    }
    private static bool ValidateField(string field) => field == "*" || field.Split(',').All(segment => ValidateSegment(segment.Trim()));
    private static bool ValidateSegment(string segment)
    {
        if (segment.Contains('/'))
        {
            var stepParts = segment.Split('/');
            if (stepParts.Length != 2 || !int.TryParse(stepParts[1], out _)) return false;
            segment = stepParts[0];
        }
        if (segment.Contains('-'))
        {
            var rangeParts = segment.Split('-');
            if (rangeParts.Length != 2 || !int.TryParse(rangeParts[0], out _) || !int.TryParse(rangeParts[1], out _)) return false;
        }
        else if (!int.TryParse(segment, out _)) return false;
        return true;
    }
    public static DateTimeOffset? GetNextOccurrence(string expression, DateTimeOffset from)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;
        var minute = ParseField(parts[0], 0, 59, from.Minute);
        var hour = ParseField(parts[1], 0, 23, from.Hour);
        var dayOfMonth = ParseField(parts[2], 1, 31, from.Day);
        var month = ParseField(parts[3], 1, 12, from.Month);
        var dayOfWeek = ParseField(parts[4], 0, 6, (int)from.DayOfWeek);
        var next = new DateTimeOffset(from.Year, from.Month, from.Day, hour ?? from.Hour, minute ?? from.Minute, 0, from.Offset);
        for (var i = 0; i < 366 * 2; i++)
        {
            if (month.HasValue && next.Month != month.Value) { next = next.AddMonths(1); next = new DateTimeOffset(next.Year, next.Month, 1, hour ?? from.Hour, minute ?? from.Minute, 0, from.Offset); continue; }
            if (dayOfMonth.HasValue && next.Day != dayOfMonth.Value) { next = next.AddDays(1); continue; }
            if (dayOfWeek.HasValue && (int)next.DayOfWeek != dayOfWeek.Value) { next = next.AddDays(1); continue; }
            return next;
        }
        return null;
    }
    private static int? ParseField(string field, int min, int max, int current) => field == "*" ? null : (int.TryParse(field, out var value) && value >= min && value <= max ? value : null);
}

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
            try { await ProcessScheduledScansAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error processing scheduled scans"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
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
        if (assetsResult is null || assetsResult.Items.Count == 0) { scheduled.NextRunAt = CronExpression.GetNextOccurrence(scheduled.CronExpression, DateTimeOffset.UtcNow); await dbContext.SaveChangesAsync(cancellationToken); return; }
        using var taskClient = new HttpClient { BaseAddress = taskSvc };
        foreach (var asset in assetsResult.Items.Take(10))
        {
            var request = new CreateReconTaskRequest(TaskType: "http-probe", ProgramId: scheduled.ProgramId, ScopeId: scheduled.ScopeId, InputAssetId: asset.AssetId, InputPayloadJson: $$"""{"host":"{{asset.Value}}"}""", WorkerCapability: "HttpProbeWorker", RequiredAssetType: null, MaxAttempts: 2, Priority: WorkerPriority.High, DedupeHash: null);
            using var taskResponse = await taskClient.PostAsJsonAsync("/tasks", request, ScanPlanJson.Options, cancellationToken);
        }
        scheduled.LastRunAt = DateTimeOffset.UtcNow;
        scheduled.NextRunAt = CronExpression.GetNextOccurrence(scheduled.CronExpression, DateTimeOffset.UtcNow);
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
        var dto = new ScheduledScanDto(ScheduledScanId: Guid.NewGuid(), ProgramId: request.ProgramId, ScopeId: request.ScopeId, Name: request.Name, WorkflowType: request.WorkflowType, CronExpression: request.CronExpression, StalenessThreshold: request.StalenessThreshold, LastRunAt: null, NextRunAt: CronExpression.GetNextOccurrence(request.CronExpression, DateTimeOffset.UtcNow), CreatedAt: DateTimeOffset.UtcNow, State: "Active");
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
        var dto = new ScheduledScanDto(ScheduledScanId: Guid.NewGuid(), ProgramId: request.ProgramId, ScopeId: request.ScopeId, Name: request.Name, WorkflowType: request.WorkflowType, CronExpression: request.CronExpression, StalenessThreshold: request.StalenessThreshold, LastRunAt: null, NextRunAt: CronExpression.GetNextOccurrence(request.CronExpression, DateTimeOffset.UtcNow), CreatedAt: DateTimeOffset.UtcNow, State: "Active");
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

internal sealed class ScanOrchestratorDbContext(DbContextOptions<ScanOrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<ScanPlanRecord> ScanPlans => Set<ScanPlanRecord>();
    public DbSet<ScheduledScanRecord> ScheduledScans => Set<ScheduledScanRecord>();

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
    string InputPayloadJson,
    WorkerPriority Priority = WorkerPriority.Normal);
internal sealed record SeededScanPlan(Guid DomainAssetId, IReadOnlyCollection<Guid> TaskIds);
