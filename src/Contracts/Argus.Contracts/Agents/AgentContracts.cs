namespace Argus.Contracts.Agents;

public sealed record AgentDto(
    Guid AgentId,
    string Name,
    string Role,
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
    string Model);

public sealed record UpdateAgentRequest(
    string? Name = null,
    string? Role = null,
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
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    string? RecoveryContext,
    int Attempts);

public sealed record CreateAgentTaskRequest(
    string Description,
    string Priority,
    string? AssignedTo = null);

public sealed record UpdateAgentTaskRequest(
    string? Description = null,
    string? Priority = null,
    string? Status = null,
    string? AssignedTo = null);

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
