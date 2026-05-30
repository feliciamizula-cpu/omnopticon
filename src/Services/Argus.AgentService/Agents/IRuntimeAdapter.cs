namespace Argus.AgentService.Agents;

using Argus.Contracts.Agents;

public sealed record RuntimeExecutionRequest(
    AgentTaskRunDto Run,
    AgentTaskDto Task,
    AgentDto Agent,
    string Prompt);

public sealed record RuntimeExecutionResult(
    bool Success,
    string? Output,
    string? Error,
    string? WorkspaceRef);

public interface IRuntimeAdapter
{
    string Kind { get; }   // "in_pod" | "workspace"
    Task<RuntimeExecutionResult> ExecuteAsync(RuntimeExecutionRequest request, CancellationToken cancellationToken);
}
