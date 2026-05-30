namespace Argus.Contracts.Agents;

public sealed record AgentTaskTriggerDto(
    Guid TriggerId,
    string TaskId,
    string EventName,
    bool Enabled,
    DateTimeOffset CreatedAt);

public sealed record CreateAgentTaskTriggerRequest(
    string EventName,
    bool Enabled = true);

public sealed record UpdateAgentTaskTriggerRequest(
    string? EventName = null,
    bool? Enabled = null);
