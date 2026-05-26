namespace Argus.AgentService.Agents;

using Argus.AgentService.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Selects the first available agent for a given role using Microsoft.Extensions.AI's IChatClient abstraction.
/// Selection order: SortOrder ascending within the role.
/// </summary>
public sealed class AgentSelectionService(
    IAgentStore store,
    ILogger<AgentSelectionService> logger)
{
    public async Task<AgentExecutionContext?> SelectAsync(
        string role,
        CancellationToken ct = default)
    {
        var agents = await store.ListAgentsAsync(ct);
        var candidates = agents
            .Where(a => a.Role.Equals(role, StringComparison.OrdinalIgnoreCase)
                     && a.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.SortOrder)
            .ToList();

        if (candidates.Count == 0)
        {
            logger.LogWarning("No active agents found for role '{Role}'", role);
            return null;
        }

        foreach (var agent in candidates)
        {
            logger.LogInformation(
                "Selected agent '{Name}' (role={Role}, tool={Tool}, model={Model}, sortOrder={SortOrder})",
                agent.Name, agent.Role, agent.Tool, agent.Model, agent.SortOrder);

            IChatClient chatClient = new CliChatClient(agent.Tool, agent.Model);
            return new AgentExecutionContext(agent, chatClient);
        }

        logger.LogWarning("No runnable candidates found for role '{Role}'", role);
        return null;
    }

    /// <summary>
    /// Returns an IChatClient for a specific agent by ID, bypassing role/provider selection.
    /// Used for direct agent invocation (e.g., manual task assignment).
    /// </summary>
    public async Task<AgentExecutionContext?> SelectByIdAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await store.GetAgentAsync(agentId, ct);
        if (agent is null)
            return null;

        IChatClient chatClient = new CliChatClient(agent.Tool, agent.Model);
        return new AgentExecutionContext(agent, chatClient);
    }
}
