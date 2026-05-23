namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentTaskRecord
{
    public string TaskId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "pending";
    public string? AssignedTo { get; set; }
    public string? RequeueReason { get; set; }
    public string? RecoveryContext { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public AgentTaskDto ToDto() =>
        new(TaskId, Description, Priority, Status, AssignedTo,
            CreatedAt, ClaimedAt, CompletedAt, RecoveryContext, Attempts);
}
