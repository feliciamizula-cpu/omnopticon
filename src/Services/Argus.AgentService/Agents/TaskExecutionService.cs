namespace Argus.AgentService.Agents;

using Argus.AgentService.ProviderUsage;
using Argus.AgentService.Stores;
using Argus.Contracts.Agents;
using Argus.AgentService.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Executes an agent task: selects the right agent via IChatClient, runs the CLI tool,
/// records output, and writes code review or system report records as needed.
/// </summary>
public sealed class TaskExecutionService(
    IAgentStore store,
    AgentSelectionService selectionService,
    IServiceProvider serviceProvider,
    ILogger<TaskExecutionService> logger)
{
    public async Task<AgentTaskDto?> ExecuteAsync(string taskId, CancellationToken ct = default)
    {
        var task = await store.GetTaskAsync(taskId, ct);
        if (task is null)
        {
            logger.LogWarning("Task '{TaskId}' not found for execution", taskId);
            return null;
        }

        var role = task.TargetRole ?? "developer";
        var context = await SelectAgentForTaskAsync(task, role, ct);
        if (context is null)
        {
            logger.LogWarning("No agent available for role '{Role}' to execute task '{TaskId}'", role, taskId);
            return await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(
                Status: "blocked",
                ResultOutput: $"No available agent with enough provider capacity for role '{role}'."), ct);
        }

        // Mark task in_progress
        var updatedTask = await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(
            Status: "in_progress",
            AssignedTo: context.Agent.AgentId.ToString()), ct);
        await store.UpdateAgentAsync(context.Agent.AgentId, new UpdateAgentRequest(
            CurrentTaskId: taskId,
            WorkStatus: "working",
            LastHeartbeatAt: DateTimeOffset.UtcNow,
            LastError: ""), ct);

        string? resultOutput = null;
        string finalStatus;

        var providerUsageService = serviceProvider.GetService<ProviderUsageService>();

        try
        {
            if (providerUsageService is not null)
                await providerUsageService.RecordInvocationStartedAsync(
                    context.Agent.Tool, context.Agent.Model, context.Agent.AgentId, taskId, ct);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildSystemPrompt(task, context.Agent.Role)),
                new(ChatRole.User, task.Description)
            };

            var completion = await context.ChatClient.GetResponseAsync(messages, cancellationToken: ct);
            resultOutput = completion.Text;
            finalStatus = IsRecurringTask(task) ? "scheduled" : "completed";

            if (providerUsageService is not null)
                await providerUsageService.RecordInvocationCompletedAsync(
                    context.Agent.Tool, context.Agent.Model, 0, resultOutput, null, ct);

            await WriteArtifactAsync(task, context.Agent.AgentId, resultOutput, ct);
            await CreateTasksFromActionsAsync(resultOutput, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task '{TaskId}' execution failed", taskId);
            finalStatus = IsRecurringTask(task) ? "scheduled" : "failed";
            resultOutput = ex.Message;
            await store.UpdateAgentAsync(context.Agent.AgentId, new UpdateAgentRequest(
                WorkStatus: "error",
                LastHeartbeatAt: DateTimeOffset.UtcNow,
                LastError: ex.Message), ct);

            if (providerUsageService is not null)
                await providerUsageService.RecordInvocationCompletedAsync(
                    context.Agent.Tool, context.Agent.Model, 1, null, ex.Message, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var nextRunAt = task.ScheduleExpression is not null
            ? AgentScheduleCalculator.GetNextRun(task.ScheduleExpression, now)
            : null;

        var saved = await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(
            Status: finalStatus,
            ResultOutput: resultOutput,
            LastRunAt: now,
            NextRunAt: nextRunAt), ct);
        await store.UpdateAgentAsync(context.Agent.AgentId, new UpdateAgentRequest(
            CurrentTaskId: "",
            WorkStatus: finalStatus == "failed" ? "error" : "idle",
            LastHeartbeatAt: now,
            LastError: finalStatus == "failed" ? resultOutput : ""), ct);

        if (finalStatus == "completed" && task.TaskType is not "code_review")
        {
            await DispatchTriggeredTasksAsync("agent_task_completed", task, ct);
        }

        return saved;
    }

    private async Task<AgentExecutionContext?> SelectAgentForTaskAsync(
        AgentTaskDto task,
        string role,
        CancellationToken ct)
    {
        if (Guid.TryParse(task.AssignedTo, out var assignedAgentId))
        {
            return await selectionService.SelectByIdAsync(assignedAgentId, ct);
        }

        return await selectionService.SelectAsync(role, ct);
    }

    private static string BuildSystemPrompt(AgentTaskDto task, string role)
    {
        return $"""
            You are an AI agent with role '{role}' in the Argus security platform.
            Complete the assigned task and output the result concisely.
            If you discover follow-up implementation work, emit lines in this exact format:
            ACTION:CREATE_TASK priority="high|normal|low" targetRole="junior_developer|developer|senior_developer|devops|system_architect" taskType="implementation|bugfix|review|ops" description="detailed implementation instructions"
            Task type: {task.TaskType ?? "general"}
            """;
    }

    private static bool IsRecurringTask(AgentTaskDto task) =>
        !string.IsNullOrWhiteSpace(task.ScheduleExpression)
        || !string.IsNullOrWhiteSpace(task.TriggerEvent);

    private async Task DispatchTriggeredTasksAsync(string triggerEvent, AgentTaskDto sourceTask, CancellationToken ct)
    {
        var tasks = await store.ListTasksAsync(status: "scheduled", cancellationToken: ct);
        foreach (var task in tasks.Where(t => string.Equals(t.TriggerEvent, triggerEvent, StringComparison.OrdinalIgnoreCase)))
        {
            if (task.TaskId == sourceTask.TaskId)
                continue;

            logger.LogInformation(
                "Dispatching triggered task '{TaskId}' from event '{TriggerEvent}' after source task '{SourceTaskId}'",
                task.TaskId,
                triggerEvent,
                sourceTask.TaskId);

            await ExecuteAsync(task.TaskId, ct);
        }
    }

    private async Task CreateTasksFromActionsAsync(string? output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("ACTION:CREATE_TASK", StringComparison.OrdinalIgnoreCase))
                continue;

            var priority = ExtractQuotedValue(line, "priority") ?? "normal";
            var targetRole = ExtractQuotedValue(line, "targetRole") ?? "developer";
            var taskType = ExtractQuotedValue(line, "taskType") ?? "implementation";
            var description = ExtractQuotedValue(line, "description");

            if (string.IsNullOrWhiteSpace(description))
                continue;

            await store.CreateTaskAsync(new CreateAgentTaskRequest(
                Description: description,
                Priority: priority,
                TargetRole: targetRole,
                TaskType: taskType), ct);
        }
    }

    private static string? ExtractQuotedValue(string line, string key)
    {
        var marker = key + "=\"";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    private async Task WriteArtifactAsync(AgentTaskDto task, Guid agentId, string content, CancellationToken ct)
    {
        var dbContext = serviceProvider.GetService<AgentDbContext>();
        if (dbContext is null) return;

        if (task.TaskType is "code_review")
        {
            dbContext.CodeReviews.Add(new CodeReviewRecord
            {
                ReviewId = Guid.NewGuid(),
                SourceTaskId = task.TaskId,
                AgentId = agentId,
                ReviewContent = content,
                Status = "completed",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(ct);
        }
        else if (task.TaskType is "system_report")
        {
            dbContext.SystemReports.Add(new SystemReportRecord
            {
                ReportId = Guid.NewGuid(),
                AgentId = agentId,
                ReportContent = content,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(ct);
        }
    }
}
