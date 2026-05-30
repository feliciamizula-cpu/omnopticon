namespace Argus.AgentService.Agents;

using Argus.Contracts.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

public sealed class InPodRuntimeAdapter(
    Func<string, string, IChatClient> chatClientFactory,
    ILogger<InPodRuntimeAdapter> logger) : IRuntimeAdapter
{
    public string Kind => AgentCapabilities.RuntimeInPod;

    public async Task<RuntimeExecutionResult> ExecuteAsync(
        RuntimeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var chat = chatClientFactory(request.Agent.Tool, request.Agent.Model);
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildSystemPrompt(request)),
                new(ChatRole.User, request.Prompt)
            };
            var completion = await chat.GetResponseAsync(messages, cancellationToken: cancellationToken);
            return new RuntimeExecutionResult(true, completion.Text, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "InPodRuntimeAdapter failed for task '{TaskId}'", request.Task.TaskId);
            return new RuntimeExecutionResult(false, null, ex.Message, null);
        }
    }

    private static string BuildSystemPrompt(RuntimeExecutionRequest r) =>
        $$"""
        You are an agent with role '{{r.Agent.Role}}' in the Argus platform.
        Capabilities granted: {{string.Join(", ", r.Agent.Capabilities)}}.
        Task type: {{r.Task.TaskType ?? "general"}}.
        Complete the task and output the result concisely.
        To create follow-up work, emit ACTION lines in the format:
        ACTION:CREATE_TASK priority="..." targetRole="..." taskType="..." description="..."
        """;
}
