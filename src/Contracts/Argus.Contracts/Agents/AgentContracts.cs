namespace Argus.Contracts.Agents;

public sealed record AgentDto(
    Guid AgentId,
    string Name,
    string Role,
    string? RoleDescription,
    int SortOrder,
    string Status,
    string[] Responsibilities,
    string? CurrentTaskId,
    string WorkStatus,
    DateTimeOffset? LastHeartbeatAt,
    string? LastError,
    string Tool,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAgentRequest(
    string Name,
    string Role,
    string[] Responsibilities,
    string Tool,
    string Model,
    string? RoleDescription = null,
    int SortOrder = 0);

public sealed record UpdateAgentRequest(
    string? Name = null,
    string? Role = null,
    string? RoleDescription = null,
    int? SortOrder = null,
    string? Status = null,
    string[]? Responsibilities = null,
    string? Tool = null,
    string? Model = null);

public sealed record AgentTaskDto(
    string TaskId,
    string Description,
    string Priority,
    string Status,
    string? AssignedTo,
    string? TargetRole,
    string? TaskType,
    string? ScheduleExpression,
    string? TriggerEvent,
    string? ResultOutput,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    string? RecoveryContext,
    int Attempts);

public sealed record CreateAgentTaskRequest(
    string Description,
    string Priority,
    string? AssignedTo = null,
    string? TargetRole = null,
    string? TaskType = null,
    string? ScheduleExpression = null,
    string? TriggerEvent = null);

public sealed record UpdateAgentTaskRequest(
    string? Description = null,
    string? Priority = null,
    string? Status = null,
    string? AssignedTo = null,
    string? TargetRole = null,
    string? TaskType = null,
    string? ScheduleExpression = null,
    string? TriggerEvent = null,
    string? ResultOutput = null);

public sealed record CodeReviewDto(
    Guid ReviewId,
    string? SourceTaskId,
    Guid? AgentId,
    string ReviewContent,
    string? CommitRef,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record CreateCodeReviewRequest(
    string ReviewContent,
    string? SourceTaskId = null,
    Guid? AgentId = null,
    string? CommitRef = null);

public sealed record SystemReportDto(
    Guid ReportId,
    Guid? AgentId,
    string ReportContent,
    DateTimeOffset CreatedAt);

public sealed record CreateSystemReportRequest(
    string ReportContent,
    Guid? AgentId = null);

public sealed record ChatMessageDto(
    Guid MessageId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record SendChatRequest(
    string Message,
    string Tool,
    string Model);

public sealed record ChatResponse(
    string Reply,
    ChatMessageDto[] History,
    AgentAction[] Actions);

public sealed record AgentAction(
    string ActionType,
    string Description);
