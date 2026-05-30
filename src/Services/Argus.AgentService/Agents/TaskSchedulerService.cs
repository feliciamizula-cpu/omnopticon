namespace Argus.AgentService.Agents;

using Argus.AgentService.Stores;
using Argus.Contracts.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class TaskSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(int.TryParse(
            Environment.GetEnvironmentVariable("TASK_SCHEDULER_INTERVAL_SECONDS"), out var s)
            ? s : (int)DefaultInterval.TotalSeconds);
        var staggerMs = int.TryParse(
            Environment.GetEnvironmentVariable("TASK_SCHEDULER_STAGGER_MS"), out var st) ? st : 500;

        logger.LogInformation("TaskSchedulerService started: interval={Interval}s stagger={StaggerMs}ms",
            interval.TotalSeconds, staggerMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchDueSchedulesAsync(staggerMs, stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task DispatchDueSchedulesAsync(int staggerMs, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
            var executor = scope.ServiceProvider.GetRequiredService<AgentExecutionService>();

            var now = DateTimeOffset.UtcNow;
            var due = await store.ListDueSchedulesAsync(now, ct);
            foreach (var schedule in due)
            {
                // Recompute NextRunAt BEFORE dispatch to prevent double-fire on long runs.
                await store.RecomputeScheduleNextRunAsync(schedule.ScheduleId, now, lastRunAt: now, ct);
                logger.LogInformation("Dispatching scheduled task '{TaskId}' (schedule {ScheduleId})",
                    schedule.TaskId, schedule.ScheduleId);
                _ = executor.ExecuteAsync(schedule.TaskId, schedule.ScheduleId, null, "schedule", ct);
                if (staggerMs > 0) await Task.Delay(staggerMs, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TaskSchedulerService dispatch error");
        }
    }
}
