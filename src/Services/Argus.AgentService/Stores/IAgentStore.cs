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

    Task<IReadOnlyList<CodeReviewDto>> ListCodeReviewsAsync(int take = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemReportDto>> ListSystemReportsAsync(int take = 100, CancellationToken cancellationToken = default);

    // Schedules
    Task<IReadOnlyList<AgentTaskScheduleDto>> ListSchedulesForTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTaskScheduleDto>> ListDueSchedulesAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default);
    Task<AgentTaskScheduleDto> CreateScheduleAsync(string taskId, CreateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default);
    Task<AgentTaskScheduleDto?> UpdateScheduleAsync(Guid scheduleId, UpdateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default);
    Task<AgentTaskScheduleDto?> RecomputeScheduleNextRunAsync(Guid scheduleId, DateTimeOffset asOf, DateTimeOffset? lastRunAt, CancellationToken cancellationToken = default);
    Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default);

    // Triggers
    Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersForTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersByEventAsync(string eventName, CancellationToken cancellationToken = default);
    Task<AgentTaskTriggerDto> CreateTriggerAsync(string taskId, CreateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default);
    Task<AgentTaskTriggerDto?> UpdateTriggerAsync(Guid triggerId, UpdateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteTriggerAsync(Guid triggerId, CancellationToken cancellationToken = default);

    // Runs
    Task<AgentTaskRunDto> CreateRunAsync(AgentTaskRunDto seed, CancellationToken cancellationToken = default);
    Task<AgentTaskRunDto?> UpdateRunAsync(Guid runId, string? status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? output, string? error, string? workspaceRef, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForTaskAsync(string taskId, int take = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForAgentAsync(Guid agentId, int take = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTaskRunDto>> ListPendingRunsAsync(CancellationToken cancellationToken = default);

    Task InitializeAsync(CancellationToken cancellationToken = default);
}
