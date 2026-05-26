namespace Argus.Contracts.Agents;

public sealed record TodoItemDto(
    Guid TodoId,
    string Name,
    string Instructions,
    string Priority,
    string? WorkerType,
    DateTimeOffset? Deadline,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record CreateTodoRequest(
    string Name,
    string Instructions,
    string Priority = "normal",
    string? WorkerType = null,
    DateTimeOffset? Deadline = null);

public sealed record UpdateTodoRequest(
    string? Name = null,
    string? Instructions = null,
    string? Priority = null,
    string? WorkerType = null,
    DateTimeOffset? Deadline = null,
    string? Status = null);
