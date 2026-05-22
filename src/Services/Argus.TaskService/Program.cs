using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.TaskService");
builder.Services.AddProblemDetails();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<TaskDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<TaskDbContext>();
    builder.Services.AddScoped<ITaskStore, EfTaskStore>();
    builder.Services.AddHostedService<TaskMaintenanceService>();
}
else
{
    builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();
}

var app = builder.Build();

await app.InitializeTaskStoreAsync();
app.MapDefaultEndpoints();

app.MapGet("/tasks", (
    Guid? programId,
    ReconTaskState? state,
    string? capability,
    ITaskStore store,
    CancellationToken cancellationToken) =>
    store.QueryAsync(programId, state, capability, cancellationToken));

app.MapPost("/tasks", async (
    CreateReconTaskRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.TaskType) || string.IsNullOrWhiteSpace(request.WorkerCapability))
    {
        return Results.BadRequest("Task type and worker capability are required.");
    }

    var task = await store.CreateAsync(request, cancellationToken);
    await events.PublishAsync(
        new TaskRequested(task.TaskId, task.TaskType, task.ProgramId, task.InputAssetId),
        nameof(TaskRequested),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Created($"/tasks/{task.TaskId}", task);
});

app.MapGet("/tasks/{taskId:guid}", async (
    Guid taskId,
    ITaskStore store,
    CancellationToken cancellationToken) =>
{
    var task = await store.FindAsync(taskId, cancellationToken);
    return task is not null ? Results.Ok(task) : Results.NotFound();
});

app.MapPost("/tasks/lease", async (
    LeaseReconTaskRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.LeaseAsync(request, cancellationToken);

    if (task is null)
    {
        return Results.NoContent();
    }

    await events.PublishAsync(
        new TaskLeased(task.TaskId, request.WorkerId, task.LeaseExpiresAt ?? DateTimeOffset.UtcNow),
        nameof(TaskLeased),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/start", async (
    Guid taskId,
    string workerId,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.StartAsync(taskId, workerId, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new TaskStarted(task.TaskId, workerId, task.StartedAt ?? DateTimeOffset.UtcNow),
        nameof(TaskStarted),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/progress", async (
    Guid taskId,
    UpdateReconTaskProgressRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.ProgressAsync(taskId, request, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new TaskProgressed(task.TaskId, task.ProgressPercent, task.ProgressMessage ?? string.Empty),
        nameof(TaskProgressed),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/complete", async (
    Guid taskId,
    CompleteReconTaskRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.CompleteAsync(taskId, request, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new TaskCompleted(task.TaskId, task.OutputSummaryJson ?? "{}"),
        nameof(TaskCompleted),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/fail", async (
    Guid taskId,
    FailReconTaskRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.FailAsync(taskId, request, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new TaskFailed(task.TaskId, task.ErrorCode ?? "Unknown", task.ErrorMessage ?? string.Empty),
        nameof(TaskFailed),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/cancel", async (
    Guid taskId,
    CancelTaskRequest request,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.CancelAsync(taskId, request.Reason, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/retry", async (
    Guid taskId,
    ITaskStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var task = await store.RetryAsync(taskId, cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new TaskRequested(task.TaskId, task.TaskType, task.ProgramId, task.InputAssetId),
        nameof(TaskRequested),
        "Argus.TaskService",
        cancellationToken: cancellationToken);

    return Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/heartbeat", async (
    Guid taskId,
    string workerId,
    ITaskStore store,
    CancellationToken cancellationToken) =>
{
    var task = await store.HeartbeatAsync(taskId, workerId, cancellationToken);
    return task is not null ? Results.Ok(task) : Results.NotFound();
});

app.MapGet("/tasks/{taskId:guid}/history", async (
    Guid taskId,
    ITaskStore store,
    CancellationToken cancellationToken) =>
{
    var history = await store.GetHistoryAsync(taskId, cancellationToken);
    return Results.Ok(history);
});

app.Run();

internal interface ITaskStore
{
    Task<IReadOnlyCollection<ReconTaskDto>> QueryAsync(Guid? programId, ReconTaskState? state, string? capability, CancellationToken cancellationToken);
    Task<ReconTaskDto> CreateAsync(CreateReconTaskRequest request, CancellationToken cancellationToken);
    Task<ReconTaskDto?> FindAsync(Guid taskId, CancellationToken cancellationToken);
    Task<ReconTaskDto?> LeaseAsync(LeaseReconTaskRequest request, CancellationToken cancellationToken);
    Task<ReconTaskDto?> StartAsync(Guid taskId, string workerId, CancellationToken cancellationToken);
    Task<ReconTaskDto?> ProgressAsync(Guid taskId, UpdateReconTaskProgressRequest request, CancellationToken cancellationToken);
    Task<ReconTaskDto?> CompleteAsync(Guid taskId, CompleteReconTaskRequest request, CancellationToken cancellationToken);
    Task<ReconTaskDto?> FailAsync(Guid taskId, FailReconTaskRequest request, CancellationToken cancellationToken);
    Task<ReconTaskDto?> CancelAsync(Guid taskId, string reason, CancellationToken cancellationToken);
    Task<ReconTaskDto?> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<ReconTaskDto?> HeartbeatAsync(Guid taskId, string workerId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TaskHistoryDto>> GetHistoryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<ReconTaskDto?> FindByDedupeAsync(string dedupeHash, CancellationToken cancellationToken);
}

internal sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<Guid, ReconTaskDto> _tasks = new();
    private readonly object _leaseLock = new();

    public Task<IReadOnlyCollection<ReconTaskDto>> QueryAsync(Guid? programId, ReconTaskState? state, string? capability, CancellationToken cancellationToken)
    {
        var tasks = _tasks.Values.AsEnumerable();

        if (programId is not null)
        {
            tasks = tasks.Where(task => task.ProgramId == programId);
        }

        if (state is not null)
        {
            tasks = tasks.Where(task => task.State == state);
        }

        if (!string.IsNullOrWhiteSpace(capability))
        {
            tasks = tasks.Where(task => string.Equals(task.WorkerCapability, capability, StringComparison.OrdinalIgnoreCase));
        }

        IReadOnlyCollection<ReconTaskDto> result = tasks
            .OrderByDescending(task => task.StartedAt ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.TaskType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<ReconTaskDto> CreateAsync(CreateReconTaskRequest request, CancellationToken cancellationToken)
    {
        var task = TaskMapping.CreateDto(request);
        _tasks[task.TaskId] = task;

        return Task.FromResult(task);
    }

    public Task<ReconTaskDto?> FindAsync(Guid taskId, CancellationToken cancellationToken)
    {
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public Task<ReconTaskDto?> LeaseAsync(LeaseReconTaskRequest request, CancellationToken cancellationToken)
    {
        lock (_leaseLock)
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = _tasks.Values
                .Where(task => string.Equals(task.WorkerCapability, request.WorkerCapability, StringComparison.OrdinalIgnoreCase))
                .Where(task => task.State is ReconTaskState.Requested or ReconTaskState.Queued or ReconTaskState.RetryPending)
                .OrderBy(task => task.Attempt)
                .FirstOrDefault();

            if (candidate is null)
            {
                return Task.FromResult<ReconTaskDto?>(null);
            }

            var leased = candidate with
            {
                State = ReconTaskState.Leased,
                Attempt = candidate.Attempt + 1,
                LeaseOwner = request.WorkerId,
                LeaseExpiresAt = now.Add(TaskMapping.NormalizeLeaseDuration(request.LeaseDuration))
            };

            _tasks[leased.TaskId] = leased;
            return Task.FromResult<ReconTaskDto?>(leased);
        }
    }

    public Task<ReconTaskDto?> StartAsync(Guid taskId, string workerId, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task => task with
        {
            State = ReconTaskState.Running,
            LeaseOwner = workerId,
            StartedAt = DateTimeOffset.UtcNow,
            ProgressPercent = Math.Max(task.ProgressPercent, 1)
        });

    public Task<ReconTaskDto?> ProgressAsync(Guid taskId, UpdateReconTaskProgressRequest request, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task => task with
        {
            ProgressPercent = Math.Clamp(request.ProgressPercent, 0, 100),
            ProgressMessage = request.ProgressMessage,
            CheckpointJson = request.CheckpointJson ?? task.CheckpointJson
        });

    public Task<ReconTaskDto?> CompleteAsync(Guid taskId, CompleteReconTaskRequest request, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task => task with
        {
            State = request.PartiallySucceeded ? ReconTaskState.PartiallySucceeded : ReconTaskState.Succeeded,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            OutputSummaryJson = request.OutputSummaryJson,
            LeaseExpiresAt = null
        });

    public Task<ReconTaskDto?> FailAsync(Guid taskId, FailReconTaskRequest request, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task =>
        {
            var canRetry = request.Retryable && task.Attempt < task.MaxAttempts;

            return task with
            {
                State = canRetry ? ReconTaskState.RetryPending : ReconTaskState.Failed,
                CompletedAt = canRetry ? null : DateTimeOffset.UtcNow,
                ErrorCode = request.ErrorCode,
                ErrorMessage = request.ErrorMessage,
                CheckpointJson = request.CheckpointJson ?? task.CheckpointJson,
                LeaseExpiresAt = null
            };
        });

    public Task<ReconTaskDto?> CancelAsync(Guid taskId, string reason, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task => task with
        {
            State = ReconTaskState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = reason,
            LeaseExpiresAt = null
        });

    public Task<ReconTaskDto?> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task => task with
        {
            State = ReconTaskState.Requested,
            Attempt = 0,
            CompletedAt = null,
            ErrorCode = null,
            ErrorMessage = null,
            LeaseExpiresAt = null
        });

    public Task<ReconTaskDto?> HeartbeatAsync(Guid taskId, string workerId, CancellationToken cancellationToken) =>
        TryUpdateAsync(taskId, task =>
        {
            if (task.State != ReconTaskState.Running || task.LeaseOwner != workerId)
            {
                return task;
            }

            return task with
            {
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
        });

    public Task<IReadOnlyCollection<TaskHistoryDto>> GetHistoryAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<TaskHistoryDto>>([]);

    public Task<ReconTaskDto?> FindByDedupeAsync(string dedupeHash, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.Values.FirstOrDefault(t => t.TaskId.ToString() == dedupeHash));

    private Task<ReconTaskDto?> TryUpdateAsync(Guid taskId, Func<ReconTaskDto, ReconTaskDto> update)
    {
        while (_tasks.TryGetValue(taskId, out var current))
        {
            var updated = update(current);

            if (_tasks.TryUpdate(taskId, updated, current))
            {
                return Task.FromResult<ReconTaskDto?>(updated);
            }
        }

        return Task.FromResult<ReconTaskDto?>(null);
    }
}

internal sealed class EfTaskStore(TaskDbContext dbContext) : ITaskStore
{
    public async Task<IReadOnlyCollection<ReconTaskDto>> QueryAsync(Guid? programId, ReconTaskState? state, string? capability, CancellationToken cancellationToken)
    {
        var tasks = dbContext.Tasks.AsNoTracking().AsQueryable();

        if (programId is not null)
        {
            tasks = tasks.Where(task => task.ProgramId == programId);
        }

        if (state is not null)
        {
            tasks = tasks.Where(task => task.State == state);
        }

        if (!string.IsNullOrWhiteSpace(capability))
        {
            tasks = tasks.Where(task => task.WorkerCapability == capability);
        }

        var records = await tasks
            .OrderByDescending(task => task.StartedAt ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.TaskType)
            .ToArrayAsync(cancellationToken);

        return records.Select(task => task.ToDto()).ToArray();
    }

    public async Task<ReconTaskDto> CreateAsync(CreateReconTaskRequest request, CancellationToken cancellationToken)
    {
        var record = TaskMapping.CreateRecord(request);
        dbContext.Tasks.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return record.ToDto();
    }

    public async Task<ReconTaskDto?> FindAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(task => task.TaskId == taskId, cancellationToken);
        return task?.ToDto();
    }

    public async Task<ReconTaskDto?> LeaseAsync(LeaseReconTaskRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockDuration = TaskMapping.NormalizeLeaseDuration(request.LeaseDuration);

        var rowsAffected = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE recon_tasks
            SET "State" = {0},
                "Attempt" = "Attempt" + 1,
                "LeaseOwner" = {1},
                "LeaseExpiresAt" = {2}
            WHERE "TaskId" = (
                SELECT "TaskId" FROM recon_tasks
                WHERE "WorkerCapability" = {3}
                AND ("State" = 'Requested' OR "State" = 'Queued' OR "State" = 'RetryPending')
                ORDER BY "Attempt", "TaskId"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            """,
            ReconTaskState.Leased.ToString(),
            request.WorkerId,
            now.Add(lockDuration),
            request.WorkerCapability);

        if (rowsAffected == 0)
        {
            return null;
        }

        var task = await dbContext.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(task => task.LeaseOwner == request.WorkerId && task.LeaseExpiresAt == now.Add(lockDuration), cancellationToken);

        return task?.ToDto();
    }

    public Task<ReconTaskDto?> StartAsync(Guid taskId, string workerId, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            task.State = ReconTaskState.Running;
            task.LeaseOwner = workerId;
            task.StartedAt = DateTimeOffset.UtcNow;
            task.ProgressPercent = Math.Max(task.ProgressPercent, 1);
        }, cancellationToken);

    public Task<ReconTaskDto?> ProgressAsync(Guid taskId, UpdateReconTaskProgressRequest request, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            task.ProgressPercent = Math.Clamp(request.ProgressPercent, 0, 100);
            task.ProgressMessage = request.ProgressMessage;
            task.CheckpointJson = request.CheckpointJson ?? task.CheckpointJson;
        }, cancellationToken);

    public Task<ReconTaskDto?> CompleteAsync(Guid taskId, CompleteReconTaskRequest request, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            task.State = request.PartiallySucceeded ? ReconTaskState.PartiallySucceeded : ReconTaskState.Succeeded;
            task.ProgressPercent = 100;
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.OutputSummaryJson = request.OutputSummaryJson;
            task.LeaseExpiresAt = null;
        }, cancellationToken);

    public Task<ReconTaskDto?> FailAsync(Guid taskId, FailReconTaskRequest request, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            var canRetry = request.Retryable && task.Attempt < task.MaxAttempts;
            task.State = canRetry ? ReconTaskState.RetryPending : ReconTaskState.Failed;
            task.CompletedAt = canRetry ? null : DateTimeOffset.UtcNow;
            task.ErrorCode = request.ErrorCode;
            task.ErrorMessage = request.ErrorMessage;
            task.CheckpointJson = request.CheckpointJson ?? task.CheckpointJson;
            task.LeaseExpiresAt = null;
        }, cancellationToken);

    public Task<ReconTaskDto?> CancelAsync(Guid taskId, string reason, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            task.State = ReconTaskState.Cancelled;
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.ErrorMessage = reason;
            task.LeaseExpiresAt = null;
        }, cancellationToken);

    public Task<ReconTaskDto?> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            task.State = ReconTaskState.Requested;
            task.Attempt = 0;
            task.CompletedAt = null;
            task.ErrorCode = null;
            task.ErrorMessage = null;
            task.LeaseExpiresAt = null;
        }, cancellationToken);

    public Task<ReconTaskDto?> HeartbeatAsync(Guid taskId, string workerId, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, task =>
        {
            if (task.State != ReconTaskState.Running || task.LeaseOwner != workerId)
            {
                return;
            }

            task.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        }, cancellationToken);

    public async Task<IReadOnlyCollection<TaskHistoryDto>> GetHistoryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        return await dbContext.TaskHistory
            .AsNoTracking()
            .Where(h => h.TaskId == taskId)
            .OrderBy(h => h.Timestamp)
            .Select(h => new TaskHistoryDto(h.HistoryId, h.TaskId, h.State, h.Timestamp, h.WorkerId, h.Message, h.CheckpointSummary))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ReconTaskDto?> FindByDedupeAsync(string dedupeHash, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.DedupeHash == dedupeHash, cancellationToken);
        return task?.ToDto();
    }

    private async Task<ReconTaskDto?> UpdateAsync(Guid taskId, Action<TaskRecord> update, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.FirstOrDefaultAsync(task => task.TaskId == taskId, cancellationToken);

        if (task is null)
        {
            return null;
        }

        update(task);
        await dbContext.SaveChangesAsync(cancellationToken);

        return task.ToDto();
    }
}

internal sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<TaskHistoryRecord> TaskHistory => Set<TaskHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskRecord>();
        task.ToTable("recon_tasks");
        task.HasKey(record => record.TaskId);
        task.HasIndex(record => new { record.ProgramId, record.State });
        task.HasIndex(record => new { record.WorkerCapability, record.State, record.Attempt });
        task.HasIndex(record => record.LeaseExpiresAt);
        task.HasIndex(record => record.DedupeHash);
        task.Property(record => record.State).HasConversion<string>().HasMaxLength(64);
        task.Property(record => record.TaskType).HasMaxLength(128);
        task.Property(record => record.WorkerCapability).HasMaxLength(128);
        task.Property(record => record.LeaseOwner).HasMaxLength(256);
        task.Property(record => record.ErrorCode).HasMaxLength(128);
        task.Property(record => record.InputPayloadJson).HasColumnType("jsonb");
        task.Property(record => record.CheckpointJson).HasColumnType("jsonb");
        task.Property(record => record.OutputSummaryJson).HasColumnType("jsonb");

        var history = modelBuilder.Entity<TaskHistoryRecord>();
        history.ToTable("task_history");
        history.HasKey(record => record.HistoryId);
        history.HasIndex(record => record.TaskId);
        history.Property(record => record.State).HasConversion<string>().HasMaxLength(64);
        history.Property(record => record.Message).HasMaxLength(1024);

        modelBuilder.ConfigureArgusOutbox();
    }
}

internal sealed class TaskRecord
{
    public Guid TaskId { get; set; }
    public string TaskType { get; set; } = string.Empty;
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public Guid? InputAssetId { get; set; }
    public string? InputPayloadJson { get; set; }
    public string WorkerCapability { get; set; } = string.Empty;
    public ReconTaskState State { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }
    public string? CheckpointJson { get; set; }
    public string? OutputSummaryJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DedupeHash { get; set; }

    public ReconTaskDto ToDto() =>
        new(
            TaskId,
            TaskType,
            ProgramId,
            ScopeId,
            InputAssetId,
            InputPayloadJson,
            WorkerCapability,
            State,
            Attempt,
            MaxAttempts,
            LeaseOwner,
            LeaseExpiresAt,
            StartedAt,
            CompletedAt,
            ProgressPercent,
            ProgressMessage,
            CheckpointJson,
            OutputSummaryJson,
            ErrorCode,
            ErrorMessage);
}

internal sealed class TaskHistoryRecord
{
    public Guid HistoryId { get; set; }
    public Guid TaskId { get; set; }
    public ReconTaskState State { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? WorkerId { get; set; }
    public string? Message { get; set; }
    public string? CheckpointSummary { get; set; }
}

internal sealed class TaskMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<TaskMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error running task maintenance");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
        var now = DateTimeOffset.UtcNow;

        var expiredLeases = await dbContext.Tasks
            .Where(t => t.State == ReconTaskState.Leased && t.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);

        foreach (var task in expiredLeases)
        {
            var canRetry = task.Attempt < task.MaxAttempts;
            var previousState = task.State;

            task.State = canRetry ? ReconTaskState.RetryPending : ReconTaskState.Expired;
            task.CompletedAt = canRetry ? null : now;

            dbContext.TaskHistory.Add(new TaskHistoryRecord
            {
                HistoryId = Guid.NewGuid(),
                TaskId = task.TaskId,
                State = previousState,
                Timestamp = now,
                Message = canRetry
                    ? $"Lease expired, requeued for retry (attempt {task.Attempt}/{task.MaxAttempts})"
                    : $"Lease expired, marked as expired after {task.Attempt} attempts"
            });

            logger.LogInformation("Task {TaskId} lease expired, state: {State}", task.TaskId, task.State);
        }

        var orphanedRunning = await dbContext.Tasks
            .Where(t => t.State == ReconTaskState.Running && t.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);

        foreach (var task in orphanedRunning)
        {
            task.State = ReconTaskState.HeartbeatLost;
            task.CompletedAt = now;

            dbContext.TaskHistory.Add(new TaskHistoryRecord
            {
                HistoryId = Guid.NewGuid(),
                TaskId = task.TaskId,
                State = ReconTaskState.Running,
                Timestamp = now,
                Message = "Heartbeat lost, marked as abandoned"
            });

            logger.LogWarning("Task {TaskId} heartbeat lost", task.TaskId);
        }

        if (expiredLeases.Count > 0 || orphanedRunning.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

internal static class TaskMapping
{
    public static ReconTaskDto CreateDto(CreateReconTaskRequest request)
    {
        var record = CreateRecord(request);
        return record.ToDto();
    }

    public static TaskRecord CreateRecord(CreateReconTaskRequest request) =>
        new()
        {
            TaskId = Guid.NewGuid(),
            TaskType = request.TaskType.Trim(),
            ProgramId = request.ProgramId,
            ScopeId = request.ScopeId,
            InputAssetId = request.InputAssetId,
            InputPayloadJson = request.InputPayloadJson,
            WorkerCapability = request.WorkerCapability.Trim(),
            State = ReconTaskState.Requested,
            Attempt = 0,
            MaxAttempts = Math.Max(1, request.MaxAttempts),
            ProgressPercent = 0
        };

    public static TimeSpan NormalizeLeaseDuration(TimeSpan leaseDuration) =>
        leaseDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : leaseDuration;
}

internal static class TaskStoreInitialization
{
    public static async Task InitializeTaskStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<TaskDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
        }
    }
}
