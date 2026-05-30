namespace Argus.Contracts.Agents;

public sealed record AgentTaskScheduleDto(
    Guid ScheduleId,
    string TaskId,
    string Cron,
    string? Label,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt);

public sealed record CreateAgentTaskScheduleRequest(
    string Cron,
    string? Label = null,
    bool Enabled = true);

public sealed record UpdateAgentTaskScheduleRequest(
    string? Cron = null,
    string? Label = null,
    bool? Enabled = null);
