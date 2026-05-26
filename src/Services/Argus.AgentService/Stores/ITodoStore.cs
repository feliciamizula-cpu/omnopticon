using Argus.Contracts.Agents;

namespace Argus.AgentService.Stores;

public interface ITodoStore
{
    Task<IReadOnlyList<TodoItemDto>> ListTodosAsync(string? status = null, string? priority = null, CancellationToken cancellationToken = default);
    Task<TodoItemDto?> GetTodoAsync(Guid todoId, CancellationToken cancellationToken = default);
    Task<TodoItemDto> CreateTodoAsync(CreateTodoRequest request, CancellationToken cancellationToken = default);
    Task<TodoItemDto?> UpdateTodoAsync(Guid todoId, UpdateTodoRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteTodoAsync(Guid todoId, CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
