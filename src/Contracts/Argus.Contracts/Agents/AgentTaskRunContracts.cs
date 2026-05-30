namespace Argus.Contracts.Agents;

public sealed record AgentTaskRunDto(
    Guid RunId,
    string TaskId,
    Guid? ScheduleId,
    Guid? TriggerId,
    Guid? AgentId,
    string TriggerSource,           // "schedule" | "trigger" | "manual" | "internal"
    string Status,                  // "pending" | "running" | "completed" | "failed"
    string Runtime,                 // "in_pod" | "workspace"
    string[] CapabilitiesSnapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Output,
    string? Error,
    string? WorkspaceRef);
