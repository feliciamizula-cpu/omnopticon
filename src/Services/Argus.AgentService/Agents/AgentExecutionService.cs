namespace Argus.AgentService.Agents;

using Argus.AgentService.Stores;
using Argus.Contracts.Agents;
using Microsoft.Extensions.Logging;

public sealed class AgentExecutionService(
    IAgentStore store,
    AgentSelectionService selection,
    IEnumerable<IRuntimeAdapter> adapters,
    ILogger<AgentExecutionService> logger)
{
    private readonly Dictionary<string, IRuntimeAdapter> _adaptersByKind =
        adapters.ToDictionary(a => a.Kind, StringComparer.Ordinal);

    public async Task<AgentTaskRunDto?> ExecuteAsync(
        string taskId,
        Guid? scheduleId,
        Guid? triggerId,
        string triggerSource,
        CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId, ct);
        if (task is null)
        {
            logger.LogWarning("Task '{TaskId}' not found for execution", taskId);
            return null;
        }

        var seed = new AgentTaskRunDto(
            Guid.NewGuid(), taskId, scheduleId, triggerId, null,
            triggerSource, "pending", AgentCapabilities.RuntimeInPod, Array.Empty<string>(),
            DateTimeOffset.UtcNow, null, null, null, null, null);
        var run = await store.CreateRunAsync(seed, ct);

        var role = task.TargetRole ?? "developer";
        var ctx = Guid.TryParse(task.AssignedTo, out var assignedId)
            ? await selection.SelectByIdAsync(assignedId, ct)
            : await selection.SelectAsync(role, task.RequiredCapabilities, ct);

        if (ctx is null)
        {
            await store.UpdateRunAsync(run.RunId, status: "failed",
                startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow,
                output: null,
                error: $"No agent available for role '{role}' with capabilities [{string.Join(", ", task.RequiredCapabilities)}].",
                workspaceRef: null, ct);
            return await store.GetRunAsync(run.RunId, ct);
        }

        var runtimeKind = ResolveRuntime(task, ctx.Agent);
        if (!_adaptersByKind.TryGetValue(runtimeKind, out var adapter))
        {
            await store.UpdateRunAsync(run.RunId, status: "failed",
                startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow,
                output: null, error: $"No adapter registered for runtime '{runtimeKind}'.",
                workspaceRef: null, ct);
            return await store.GetRunAsync(run.RunId, ct);
        }

        await store.UpdateRunAsync(run.RunId, status: "running",
            startedAt: DateTimeOffset.UtcNow, completedAt: null,
            output: null, error: null, workspaceRef: null, ct);

        await store.UpdateAgentAsync(ctx.Agent.AgentId,
            new UpdateAgentRequest(CurrentTaskId: taskId, WorkStatus: "working",
                LastHeartbeatAt: DateTimeOffset.UtcNow, LastError: ""), ct);

        var prompt = task.Instructions ?? task.Description;
        var request = new RuntimeExecutionRequest(run, task, ctx.Agent, prompt);
        var result = await adapter.ExecuteAsync(request, ct);

        var now = DateTimeOffset.UtcNow;
        await store.UpdateRunAsync(run.RunId,
            status: result.Success ? "completed" : "failed",
            startedAt: null, completedAt: now,
            output: result.Output, error: result.Error,
            workspaceRef: result.WorkspaceRef, ct);

        await store.UpdateAgentAsync(ctx.Agent.AgentId,
            new UpdateAgentRequest(CurrentTaskId: "",
                WorkStatus: result.Success ? "idle" : "error",
                LastHeartbeatAt: now,
                LastError: result.Success ? "" : result.Error), ct);

        // Update task's LastRunAt mirror; the schedule (if any) sets NextRunAt elsewhere.
        await store.UpdateTaskAsync(taskId,
            new UpdateAgentTaskRequest(LastRunAt: now,
                Status: result.Success ? "completed" : "failed",
                ResultOutput: result.Output ?? result.Error), ct);

        return await store.GetRunAsync(run.RunId, ct);
    }

    public static string ResolveRuntime(AgentTaskDto task, AgentDto agent)
    {
        if (!string.IsNullOrEmpty(task.Runtime) &&
            task.Runtime != AgentCapabilities.RuntimeDefault)
        {
            return task.Runtime;
        }
        if (!string.IsNullOrEmpty(agent.DefaultRuntime))
        {
            return agent.DefaultRuntime;
        }
        return AgentCapabilities.DefaultRuntimeForCapabilities(agent.Capabilities);
    }
}
