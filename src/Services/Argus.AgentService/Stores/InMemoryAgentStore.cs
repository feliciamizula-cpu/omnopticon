namespace Argus.AgentService.Stores;

using System.Collections.Concurrent;
using System.Text.Json;
using Argus.AgentService.Agents;
using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage;
using Argus.Contracts.Agents;

public sealed class InMemoryAgentStore : IAgentStore
{
    private readonly ConcurrentDictionary<Guid, AgentRecord> _agents = new();
    private readonly ConcurrentDictionary<string, AgentTaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<Guid, ChatMessageRecord> _chatMessages = new();
    private readonly ConcurrentDictionary<Guid, CodeReviewRecord> _codeReviews = new();
    private readonly ConcurrentDictionary<Guid, SystemReportRecord> _systemReports = new();
    private int _nextTaskId = 1;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SeedDefaultAgents();
        return Task.CompletedTask;
    }

    internal readonly ConcurrentDictionary<Guid, ProviderAccountRecord> ProviderAccounts = new();
    internal readonly ConcurrentDictionary<Guid, ProviderUsageSnapshotRecord> ProviderUsageSnapshots = new();

    private void SeedDefaultAgents()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var agent in AgentDevelopmentSeedData.CreateAgents(now))
        {
            _agents.TryAdd(agent.AgentId, agent);
        }

        foreach (var task in AgentDevelopmentSeedData.CreateTasks(now))
        {
            _tasks.TryAdd(task.TaskId, task);
        }

        foreach (var account in ProviderUsageSeedData.CreateAccounts(now))
        {
            ProviderAccounts.TryAdd(account.AccountId, account);
        }

        _nextTaskId = AgentDevelopmentSeedData.NextNumericTaskId(_tasks.Keys);
    }

    public Task<IReadOnlyList<AgentDto>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var agents = _agents.Values.Select(a => a.ToDto()).ToList();
        return Task.FromResult<IReadOnlyList<AgentDto>>(agents);
    }

    public Task<AgentDto?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_agents.TryGetValue(agentId, out var record) ? record.ToDto() : null);
    }

    public Task<AgentDto> CreateAgentAsync(CreateAgentRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var record = new AgentRecord
        {
            AgentId = id,
            Name = request.Name,
            Role = request.Role,
            RoleDescription = request.RoleDescription,
            SortOrder = request.SortOrder,
            Status = "active",
            ResponsibilitiesJson = JsonSerializer.Serialize(request.Responsibilities),
            Tool = request.Tool,
            Model = request.Model
        };

        _agents.TryAdd(id, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<AgentDto?> UpdateAgentAsync(Guid agentId, UpdateAgentRequest request, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var record))
            return Task.FromResult<AgentDto?>(null);

        if (!string.IsNullOrWhiteSpace(request.Name))
            record.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Role))
            record.Role = request.Role;
        if (request.RoleDescription != null)
            record.RoleDescription = string.IsNullOrWhiteSpace(request.RoleDescription) ? null : request.RoleDescription;
        if (request.SortOrder.HasValue)
            record.SortOrder = request.SortOrder.Value;
        if (!string.IsNullOrWhiteSpace(request.Status))
            record.Status = request.Status;
        if (request.Responsibilities != null)
            record.ResponsibilitiesJson = JsonSerializer.Serialize(request.Responsibilities);
        if (!string.IsNullOrWhiteSpace(request.Tool))
            record.Tool = request.Tool;
        if (!string.IsNullOrWhiteSpace(request.Model))
            record.Model = request.Model;
        if (request.CurrentTaskId is not null)
            record.CurrentTaskId = string.IsNullOrWhiteSpace(request.CurrentTaskId) ? null : request.CurrentTaskId;
        if (!string.IsNullOrWhiteSpace(request.WorkStatus))
            record.WorkStatus = request.WorkStatus;
        if (request.LastHeartbeatAt.HasValue)
            record.LastHeartbeatAt = request.LastHeartbeatAt.Value;
        if (request.LastError is not null)
            record.LastError = string.IsNullOrWhiteSpace(request.LastError) ? null : request.LastError;

        record.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<AgentDto?>(record.ToDto());
    }

    public Task<bool> DeleteAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_agents.TryRemove(agentId, out _));
    }

    public Task<IReadOnlyList<AgentTaskDto>> ListTasksAsync(string? status = null, string? priority = null, CancellationToken cancellationToken = default)
    {
        var tasks = _tasks.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(status))
            tasks = tasks.Where(t => t.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(priority))
            tasks = tasks.Where(t => t.Priority.Equals(priority, StringComparison.OrdinalIgnoreCase));

        var result = tasks.OrderByDescending(t => t.CreatedAt).Select(t => t.ToDto()).ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskDto>>(result);
    }

    public Task<AgentTaskDto?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_tasks.TryGetValue(taskId, out var record) ? record.ToDto() : null);
    }

    public Task<AgentTaskDto> CreateTaskAsync(CreateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var taskId = _nextTaskId.ToString("000");
        _nextTaskId++;

        var record = new AgentTaskRecord
        {
            TaskId = taskId,
            Description = request.Description,
            Priority = request.Priority,
            Status = request.ScheduleExpression is not null || request.TriggerEvent is not null ? "scheduled" : "pending",
            AssignedTo = request.AssignedTo,
            TargetRole = request.TargetRole,
            TaskType = request.TaskType,
            ScheduleExpression = request.ScheduleExpression,
            TriggerEvent = request.TriggerEvent,
            NextRunAt = request.ScheduleExpression is not null
                ? AgentScheduleCalculator.GetNextRun(request.ScheduleExpression, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow
                : null
        };

        _tasks.TryAdd(taskId, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<AgentTaskDto?> UpdateTaskAsync(string taskId, UpdateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var record))
            return Task.FromResult<AgentTaskDto?>(null);

        if (!string.IsNullOrWhiteSpace(request.Description))
            record.Description = request.Description;
        if (!string.IsNullOrWhiteSpace(request.Priority))
            record.Priority = request.Priority;
        if (!string.IsNullOrWhiteSpace(request.Status))
            ApplyStatusTransition(record, request.Status);
        record.AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? null : request.AssignedTo;
        if (request.TargetRole != null)
            record.TargetRole = string.IsNullOrWhiteSpace(request.TargetRole) ? null : request.TargetRole;
        if (request.TaskType != null)
            record.TaskType = string.IsNullOrWhiteSpace(request.TaskType) ? null : request.TaskType;
        if (request.ScheduleExpression != null)
            record.ScheduleExpression = string.IsNullOrWhiteSpace(request.ScheduleExpression) ? null : request.ScheduleExpression;
        if (request.TriggerEvent != null)
            record.TriggerEvent = string.IsNullOrWhiteSpace(request.TriggerEvent) ? null : request.TriggerEvent;
        if (request.ResultOutput != null)
            record.ResultOutput = request.ResultOutput;
        if (request.LastRunAt.HasValue)
            record.LastRunAt = request.LastRunAt.Value;
        if (request.NextRunAt.HasValue)
            record.NextRunAt = request.NextRunAt.Value;

        return Task.FromResult<AgentTaskDto?>(record.ToDto());
    }


    private static void ApplyStatusTransition(AgentTaskRecord record, string status)
    {
        var normalizedStatus = status.Trim().ToLowerInvariant();
        record.Status = normalizedStatus;

        var now = DateTimeOffset.UtcNow;
        switch (normalizedStatus)
        {
            case "pending":
            case "scheduled":
                record.ClaimedAt = null;
                record.CompletedAt = null;
                break;
            case "claimed":
            case "in_progress":
            case "blocked":
                record.ClaimedAt ??= now;
                record.CompletedAt = null;
                break;
            case "completed":
                record.ClaimedAt ??= now;
                record.CompletedAt = now;
                break;
            case "failed":
                record.ClaimedAt ??= now;
                record.CompletedAt = now;
                record.Attempts += 1;
                break;
        }
    }

    public Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_tasks.TryRemove(taskId, out _));
    }

    public Task<IReadOnlyList<ChatMessageDto>> GetChatHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var history = _chatMessages.Values
            .OrderBy(m => m.CreatedAt)
            .TakeLast(limit)
            .Select(m => m.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<ChatMessageDto>>(history);
    }

    public Task<ChatMessageDto> SaveChatMessageAsync(string role, string content, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var record = new ChatMessageRecord
        {
            MessageId = id,
            Role = role,
            Content = content
        };

        _chatMessages.TryAdd(id, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<IReadOnlyList<CodeReviewDto>> ListCodeReviewsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        var result = _codeReviews.Values
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<CodeReviewDto>>(result);
    }

    public Task<IReadOnlyList<SystemReportDto>> ListSystemReportsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        var result = _systemReports.Values
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<SystemReportDto>>(result);
    }
}
