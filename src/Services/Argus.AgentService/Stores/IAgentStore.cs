namespace Argus.AgentService.Stores;

using Argus.Contracts.Agents;

public interface IAgentStore
{
    Task<IReadOnlyList<AgentDto>> ListAgentsAsync(CancellationToken cancellationToken = default);
    Task<AgentDto?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken = default);
    Task<AgentDto> CreateAgentAsync(CreateAgentRequest request, CancellationToken cancellationToken = default);
    Task<AgentDto?> UpdateAgentAsync(Guid agentId, UpdateAgentRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTaskDto>> ListTasksAsync(string? status = null, string? priority = null, CancellationToken cancellationToken = default);
    Task<AgentTaskDto?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<AgentTaskDto> CreateTaskAsync(CreateAgentTaskRequest request, CancellationToken cancellationToken = default);
    Task<AgentTaskDto?> UpdateTaskAsync(string taskId, UpdateAgentTaskRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessageDto>> GetChatHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<ChatMessageDto> SaveChatMessageAsync(string role, string content, CancellationToken cancellationToken = default);

    Task InitializeAsync(CancellationToken cancellationToken = default);
}
