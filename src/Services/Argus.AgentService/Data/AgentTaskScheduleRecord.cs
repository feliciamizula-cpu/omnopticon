namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentTaskScheduleRecord
{
    public Guid ScheduleId { get; set; } = Guid.NewGuid();
    public string TaskId { get; set; } = string.Empty;
    public string Cron { get; set; } = string.Empty;
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentTaskScheduleDto ToDto() =>
        new(ScheduleId, TaskId, Cron, Label, Enabled, NextRunAt, LastRunAt, CreatedAt);
}
