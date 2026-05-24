using System.Collections.Concurrent;
using Argus.Contracts.Agents;

namespace Argus.AgentService.Stores;

public sealed class InMemoryTodoStore : ITodoStore
{
    private readonly ConcurrentDictionary<Guid, TodoItemDto> _todos = new();
    private int _nextId = 1; // for string IDs if needed, but using Guid

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // No seed data for now
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TodoItemDto>> ListTodosAsync(string? status = null, string? priority = null, CancellationToken cancellationToken = default)
    {
        var todos = _todos.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            todos = todos.Where(t => string.Equals(t.Status, status, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(priority))
        {
            todos = todos.Where(t => string.Equals(t.Priority, priority, StringComparison.OrdinalIgnoreCase));
        }
        return Task.FromResult<IReadOnlyList<TodoItemDto>>(todos.OrderBy(t => t.CreatedAt).ToList());
    }

    public Task<TodoItemDto?> GetTodoAsync(Guid todoId, CancellationToken cancellationToken = default)
    {
        _todos.TryGetValue(todoId, out var item);
        return Task.FromResult<TodoItemDto?>(item);
    }

    public Task<TodoItemDto> CreateTodoAsync(CreateTodoRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var todo = new TodoItemDto(
            TodoId: id,
            Name: request.Name,
            Instructions: request.Instructions,
            Priority: request.Priority,
            WorkerType: request.WorkerType,
            Deadline: request.Deadline,
            Status: "pending",
            CreatedAt: now,
            CompletedAt: null);
        _todos.TryAdd(id, todo);
        return Task.FromResult(todo);
    }

    public Task<TodoItemDto?> UpdateTodoAsync(Guid todoId, UpdateTodoRequest request, CancellationToken cancellationToken = default)
    {
        if (!_todos.TryGetValue(todoId, out var existing))
            return Task.FromResult<TodoItemDto?>(null);
        var updated = existing with
        {
            Name = request.Name ?? existing.Name,
            Instructions = request.Instructions ?? existing.Instructions,
            Priority = request.Priority ?? existing.Priority,
            WorkerType = request.WorkerType ?? existing.WorkerType,
            Deadline = request.Deadline ?? existing.Deadline,
            Status = request.Status ?? existing.Status,
            CompletedAt = (request.Status != null && request.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)) ? DateTimeOffset.UtcNow : existing.CompletedAt
        };
        _todos[todoId] = updated;
        return Task.FromResult<TodoItemDto?>(updated);
    }

    public Task<bool> DeleteTodoAsync(Guid todoId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_todos.TryRemove(todoId, out _));
    }
}
