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
        var context = await selectionService.SelectAsync(role, ct);
        if (context is null)
        {
            logger.LogWarning("No agent available for role '{Role}' to execute task '{TaskId}'", role, taskId);
            return null;
        }

        // Mark task in_progress
        var updatedTask = await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(
            Status: "in_progress",
            AssignedTo: context.Agent.AgentId.ToString()), ct);

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
            finalStatus = "completed";

            if (providerUsageService is not null)
                await providerUsageService.RecordInvocationCompletedAsync(
                    context.Agent.Tool, context.Agent.Model, 0, resultOutput, null, ct);

            await WriteArtifactAsync(task, context.Agent.AgentId, resultOutput, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task '{TaskId}' execution failed", taskId);
            finalStatus = "failed";
            resultOutput = ex.Message;

            if (providerUsageService is not null)
                await providerUsageService.RecordInvocationCompletedAsync(
                    context.Agent.Tool, context.Agent.Model, 1, null, ex.Message, ct);
        }

        return await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(
            Status: finalStatus,
            ResultOutput: resultOutput), ct);
    }

    private static string BuildSystemPrompt(AgentTaskDto task, string role)
    {
        return $"""
            You are an AI agent with role '{role}' in the Argus security platform.
            Complete the following task concisely and output only the result.
            Task type: {task.TaskType ?? "general"}
            """;
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
