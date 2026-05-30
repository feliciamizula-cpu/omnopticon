namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentTaskTriggerRecord
{
    public Guid TriggerId { get; set; } = Guid.NewGuid();
    public string TaskId { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentTaskTriggerDto ToDto() =>
        new(TriggerId, TaskId, EventName, Enabled, CreatedAt);
}
