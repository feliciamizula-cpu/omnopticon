namespace Argus.AgentService.Agents;

using Argus.AgentService.ProviderUsage;
using Argus.AgentService.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Selects the first available agent for a given role using Microsoft.Extensions.AI's IChatClient abstraction.
/// Selection order: SortOrder ascending within the role, skipping agents whose provider is exhausted or critical.
/// </summary>
public sealed class AgentSelectionService(
    IAgentStore store,
    ILogger<AgentSelectionService> logger,
    ProviderUsageService? providerUsageService = null)
{
    private static readonly HashSet<string> BlockedStatuses =
        new(["exhausted", "critical"], StringComparer.OrdinalIgnoreCase);

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

        // Resolve provider statuses once for the candidate tools
        var blockedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (providerUsageService is not null)
        {
            try
            {
                var summary = await providerUsageService.GetSummaryAsync(ct);
                foreach (var account in summary.Accounts)
                {
                    var status = account.LatestSnapshot?.Status;
                    if (status is not null && BlockedStatuses.Contains(status))
                    {
                        foreach (var tool in account.RelatedTools)
                            blockedTools.Add(tool);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read provider usage; proceeding without blocking check");
            }
        }

        foreach (var agent in candidates)
        {
            if (blockedTools.Contains(agent.Tool))
            {
                logger.LogInformation(
                    "Skipping agent '{Name}' — tool '{Tool}' provider is exhausted/critical",
                    agent.Name, agent.Tool);
                continue;
            }

            logger.LogInformation(
                "Selected agent '{Name}' (role={Role}, tool={Tool}, model={Model}, sortOrder={SortOrder})",
                agent.Name, agent.Role, agent.Tool, agent.Model, agent.SortOrder);

            IChatClient chatClient = new CliChatClient(agent.Tool, agent.Model);
            return new AgentExecutionContext(agent, chatClient);
        }

        logger.LogWarning("All candidates for role '{Role}' are blocked by provider limits", role);
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
