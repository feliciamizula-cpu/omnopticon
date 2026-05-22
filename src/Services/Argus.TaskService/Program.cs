using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<TaskStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/tasks", (Guid? programId, ReconTaskState? state, string? capability, TaskStore store) =>
    store.Query(programId, state, capability));

app.MapPost("/tasks", (CreateReconTaskRequest request, TaskStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.TaskType) || string.IsNullOrWhiteSpace(request.WorkerCapability))
    {
        return Results.BadRequest("Task type and worker capability are required.");
    }

    var task = store.Create(request);
    return Results.Created($"/tasks/{task.TaskId}", task);
});

app.MapGet("/tasks/{taskId:guid}", (Guid taskId, TaskStore store) =>
    store.TryGet(taskId, out var task) ? Results.Ok(task) : Results.NotFound());

app.MapPost("/tasks/lease", (LeaseReconTaskRequest request, TaskStore store) =>
    store.TryLease(request, out var task) ? Results.Ok(task) : Results.NoContent());

app.MapPost("/tasks/{taskId:guid}/start", (Guid taskId, string workerId, TaskStore store) =>
    store.TryStart(taskId, workerId, out var task) ? Results.Ok(task) : Results.NotFound());

app.MapPost("/tasks/{taskId:guid}/progress", (
    Guid taskId,
    UpdateReconTaskProgressRequest request,
    TaskStore store) =>
    store.TryProgress(taskId, request, out var task) ? Results.Ok(task) : Results.NotFound());

app.MapPost("/tasks/{taskId:guid}/complete", (
    Guid taskId,
    CompleteReconTaskRequest request,
    TaskStore store) =>
    store.TryComplete(taskId, request, out var task) ? Results.Ok(task) : Results.NotFound());

app.MapPost("/tasks/{taskId:guid}/fail", (
    Guid taskId,
    FailReconTaskRequest request,
    TaskStore store) =>
    store.TryFail(taskId, request, out var task) ? Results.Ok(task) : Results.NotFound());

app.Run();

internal sealed class TaskStore
{
    private readonly ConcurrentDictionary<Guid, ReconTaskDto> _tasks = new();
    private readonly object _leaseLock = new();

    public IReadOnlyCollection<ReconTaskDto> Query(Guid? programId, ReconTaskState? state, string? capability)
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

        return tasks
            .OrderByDescending(task => task.StartedAt ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.TaskType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ReconTaskDto Create(CreateReconTaskRequest request)
    {
        var task = new ReconTaskDto(
            Guid.NewGuid(),
            request.TaskType.Trim(),
            request.ProgramId,
            request.ScopeId,
            request.InputAssetId,
            request.InputPayloadJson,
            request.WorkerCapability.Trim(),
            ReconTaskState.Requested,
            Attempt: 0,
            MaxAttempts: Math.Max(1, request.MaxAttempts),
            LeaseOwner: null,
            LeaseExpiresAt: null,
            StartedAt: null,
            CompletedAt: null,
            ProgressPercent: 0,
            ProgressMessage: null,
            CheckpointJson: null,
            OutputSummaryJson: null,
            ErrorCode: null,
            ErrorMessage: null);

        _tasks[task.TaskId] = task;

        return task;
    }

    public bool TryGet(Guid taskId, out ReconTaskDto? task) => _tasks.TryGetValue(taskId, out task);

    public bool TryLease(LeaseReconTaskRequest request, out ReconTaskDto? leased)
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
                leased = null;
                return false;
            }

            leased = candidate with
            {
                State = ReconTaskState.Leased,
                Attempt = candidate.Attempt + 1,
                LeaseOwner = request.WorkerId,
                LeaseExpiresAt = now.Add(request.LeaseDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : request.LeaseDuration)
            };

            _tasks[leased.TaskId] = leased;
            return true;
        }
    }

    public bool TryStart(Guid taskId, string workerId, out ReconTaskDto? updated) =>
        TryUpdate(taskId, task => task with
        {
            State = ReconTaskState.Running,
            LeaseOwner = workerId,
            StartedAt = DateTimeOffset.UtcNow,
            ProgressPercent = Math.Max(task.ProgressPercent, 1)
        }, out updated);

    public bool TryProgress(Guid taskId, UpdateReconTaskProgressRequest request, out ReconTaskDto? updated) =>
        TryUpdate(taskId, task => task with
        {
            ProgressPercent = Math.Clamp(request.ProgressPercent, 0, 100),
            ProgressMessage = request.ProgressMessage,
            CheckpointJson = request.CheckpointJson ?? task.CheckpointJson
        }, out updated);

    public bool TryComplete(Guid taskId, CompleteReconTaskRequest request, out ReconTaskDto? updated) =>
        TryUpdate(taskId, task => task with
        {
            State = request.PartiallySucceeded ? ReconTaskState.PartiallySucceeded : ReconTaskState.Succeeded,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            OutputSummaryJson = request.OutputSummaryJson,
            LeaseExpiresAt = null
        }, out updated);

    public bool TryFail(Guid taskId, FailReconTaskRequest request, out ReconTaskDto? updated) =>
        TryUpdate(taskId, task =>
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
        }, out updated);

    private bool TryUpdate(Guid taskId, Func<ReconTaskDto, ReconTaskDto> update, out ReconTaskDto? updated)
    {
        while (_tasks.TryGetValue(taskId, out var current))
        {
            updated = update(current);

            if (_tasks.TryUpdate(taskId, updated, current))
            {
                return true;
            }
        }

        updated = null;
        return false;
    }
}
