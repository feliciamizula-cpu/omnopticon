namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentTaskRunRecord
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public string TaskId { get; set; } = string.Empty;
    public Guid? ScheduleId { get; set; }
    public Guid? TriggerId { get; set; }
    public Guid? AgentId { get; set; }
    public string TriggerSource { get; set; } = "manual";
    public string Status { get; set; } = "pending";
    public string Runtime { get; set; } = AgentCapabilities.RuntimeInPod;
    public string CapabilitiesSnapshotJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? WorkspaceRef { get; set; }

    public AgentTaskRunDto ToDto()
    {
        var caps = System.Text.Json.JsonSerializer.Deserialize<string[]>(CapabilitiesSnapshotJson) ?? [];
        return new AgentTaskRunDto(
            RunId, TaskId, ScheduleId, TriggerId, AgentId, TriggerSource, Status, Runtime,
            caps, CreatedAt, StartedAt, CompletedAt, Output, Error, WorkspaceRef);
    }
}
