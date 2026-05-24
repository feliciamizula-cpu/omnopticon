namespace Argus.AgentService.Agents;

using Argus.AgentService.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background service that polls for scheduled tasks and dispatches them for execution.
/// Interval defaults to 5 minutes in Development, configurable via TASK_SCHEDULER_INTERVAL_SECONDS.
/// </summary>
public sealed class TaskSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("TASK_SCHEDULER_INTERVAL_SECONDS"), out var s) ? s : (int)DefaultInterval.TotalSeconds;
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("TaskSchedulerService started with {Interval}s interval", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            await DispatchDueTasksAsync(stoppingToken);
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
                // Trigger-based tasks: wait for external events, not the scheduler
                if (task.TriggerEvent is not null)
                    continue;

                // Schedule-based tasks: check NextRunAt
                if (task.NextRunAt is not null && task.NextRunAt > now)
                    continue;

                logger.LogInformation("Dispatching scheduled task '{TaskId}': {Description}", task.TaskId, task.Description);

                _ = Task.Run(async () =>
                {
                    try { await executor.ExecuteAsync(task.TaskId, ct); }
                    catch (Exception ex) { logger.LogError(ex, "Scheduled task '{TaskId}' execution error", task.TaskId); }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TaskSchedulerService dispatch error");
        }
    }
}
