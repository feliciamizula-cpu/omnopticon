namespace Argus.AgentService.Agents;

using Argus.Contracts.Agents;
using Microsoft.Extensions.AI;

/// <summary>
/// The result of agent selection — the chosen agent and its ready-to-use IChatClient.
/// </summary>
public sealed record AgentExecutionContext(AgentDto Agent, IChatClient ChatClient);
