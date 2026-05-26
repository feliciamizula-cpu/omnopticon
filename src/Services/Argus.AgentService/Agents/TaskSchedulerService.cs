namespace Argus.AgentService.Agents;

using Argus.AgentService.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background service that polls for scheduled tasks and dispatches them for execution.
/// Interval defaults to 10 seconds, configurable via TASK_SCHEDULER_INTERVAL_SECONDS.
/// </summary>
public sealed class TaskSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("TASK_SCHEDULER_INTERVAL_SECONDS"), out var s) ? s : (int)DefaultInterval.TotalSeconds;
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("TaskSchedulerService started with {Interval}s interval", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchDueTasksAsync(stoppingToken);
            await DispatchPendingTodoTasksAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task DispatchDueTasksAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
            var executor = scope.ServiceProvider.GetRequiredService<TaskExecutionService>();

            var tasks = await store.ListTasksAsync(status: "scheduled", cancellationToken: ct);
            var now = DateTimeOffset.UtcNow;

            foreach (var task in tasks)
            {
                // Trigger-based tasks wait for explicit trigger dispatch.
                if (task.TriggerEvent is not null)
                    continue;

                // Schedule-based tasks: check NextRunAt
                if (task.NextRunAt is not null && task.NextRunAt > now)
                    continue;

                logger.LogInformation("Dispatching scheduled task '{TaskId}': {Description}", task.TaskId, task.Description);

                await executor.ExecuteAsync(task.TaskId, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TaskSchedulerService dispatch error");
        }
    }

    private async Task DispatchPendingTodoTasksAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
            var executor = scope.ServiceProvider.GetRequiredService<TaskExecutionService>();

            var pending = await store.ListTasksAsync(status: "pending", cancellationToken: ct);
            foreach (var task in pending.OrderBy(PriorityRank).ThenBy(t => t.CreatedAt))
            {
                logger.LogInformation("Dispatching pending task '{TaskId}': {Description}", task.TaskId, task.Description);
                await executor.ExecuteAsync(task.TaskId, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TaskSchedulerService pending task dispatch error");
        }
    }

    private static int PriorityRank(Argus.Contracts.Agents.AgentTaskDto task) =>
        (task.Priority ?? "").Trim().ToLowerInvariant() switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" or "normal" => 2,
            "low" => 3,
            _ => 4
        };
}
