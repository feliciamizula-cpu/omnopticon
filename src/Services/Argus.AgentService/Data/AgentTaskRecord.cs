namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentTaskRecord
{
    public string TaskId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "pending";
    public string? AssignedTo { get; set; }
    public string? TargetRole { get; set; }
    public string? TaskType { get; set; }
    public string Runtime { get; set; } = AgentCapabilities.RuntimeDefault;
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public string? ScheduleExpression { get; set; }   // legacy; mirrored from first schedule for now
    public string? TriggerEvent { get; set; }         // legacy; mirrored from first trigger for now
    public string? ResultOutput { get; set; }
    public string? RequeueReason { get; set; }
    public string? RecoveryContext { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? EnabledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisabledAt { get; set; }

    public AgentTaskDto ToDto(string? firstScheduleCron = null, string? firstTriggerEvent = null)
    {
        var required = System.Text.Json.JsonSerializer.Deserialize<string[]>(RequiredCapabilitiesJson) ?? [];
        return new AgentTaskDto(
            TaskId, Description, Instructions, Priority, Status, AssignedTo, TargetRole, TaskType,
            Runtime, required,
            firstScheduleCron ?? ScheduleExpression,
            firstTriggerEvent ?? TriggerEvent,
            ResultOutput,
            CreatedAt, ClaimedAt, CompletedAt, LastRunAt, NextRunAt,
            EnabledAt, DisabledAt,
            RecoveryContext, Attempts);
    }
}
