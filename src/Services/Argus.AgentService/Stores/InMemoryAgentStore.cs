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
    private readonly ConcurrentDictionary<Guid, AgentTaskScheduleRecord> _schedules = new();
    private readonly ConcurrentDictionary<Guid, AgentTaskTriggerRecord> _triggers = new();
    private readonly ConcurrentDictionary<Guid, AgentTaskRunRecord> _runs = new();
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
            Model = request.Model,
            Provider = string.IsNullOrWhiteSpace(request.Provider) ? null : request.Provider.Trim(),
            Priority = request.Priority,
            CapabilitiesJson = request.Capabilities != null ? JsonSerializer.Serialize(request.Capabilities) : "[]",
            DefaultRuntime = request.DefaultRuntime ?? AgentCapabilities.RuntimeInPod
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
        if (request.ClearProvider)
            record.Provider = null;
        else if (!string.IsNullOrWhiteSpace(request.Provider))
            record.Provider = request.Provider.Trim();
        if (!string.IsNullOrWhiteSpace(request.Priority))
            record.Priority = request.Priority;
        if (request.Capabilities != null)
            record.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities);
        if (!string.IsNullOrWhiteSpace(request.DefaultRuntime))
            record.DefaultRuntime = request.DefaultRuntime;

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

        var result = tasks.OrderByDescending(t => t.CreatedAt)
            .Select(t => EnrichTask(t))
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskDto>>(result);
    }

    public Task<AgentTaskDto?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var record))
            return Task.FromResult<AgentTaskDto?>(null);
        return Task.FromResult<AgentTaskDto?>(EnrichTask(record));
    }

    private AgentTaskDto EnrichTask(AgentTaskRecord r)
    {
        var firstSched = _schedules.Values
            .Where(s => s.TaskId == r.TaskId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Cron)
            .FirstOrDefault();
        var firstTrig = _triggers.Values
            .Where(t => t.TaskId == r.TaskId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.EventName)
            .FirstOrDefault();
        return r.ToDto(firstSched, firstTrig);
    }

    public Task<AgentTaskDto> CreateTaskAsync(CreateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var taskId = _nextTaskId.ToString("000");
        _nextTaskId++;

        var record = new AgentTaskRecord
        {
            TaskId = taskId,
            Description = request.Description,
            Instructions = request.Instructions,
            Priority = request.Priority,
            Status = request.ScheduleExpression is not null || request.TriggerEvent is not null ? "scheduled" : "pending",
            AssignedTo = request.AssignedTo,
            TargetRole = request.TargetRole,
            TaskType = request.TaskType,
            Runtime = request.Runtime,
            RequiredCapabilitiesJson = request.RequiredCapabilities != null ? JsonSerializer.Serialize(request.RequiredCapabilities) : "[]",
            ScheduleExpression = request.ScheduleExpression,
            TriggerEvent = request.TriggerEvent,
            NextRunAt = request.ScheduleExpression is not null
                ? AgentScheduleCalculator.GetNextRun(request.ScheduleExpression, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow
                : null
        };

        _tasks.TryAdd(taskId, record);

        if (request.ScheduleExpression is not null)
        {
            var schedRecord = new AgentTaskScheduleRecord
            {
                ScheduleId = Guid.NewGuid(),
                TaskId = taskId,
                Cron = request.ScheduleExpression,
                Enabled = true,
                NextRunAt = AgentScheduleCalculator.GetNextRun(request.ScheduleExpression, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow
            };
            _schedules.TryAdd(schedRecord.ScheduleId, schedRecord);
        }

        if (request.TriggerEvent is not null)
        {
            var trigRecord = new AgentTaskTriggerRecord
            {
                TriggerId = Guid.NewGuid(),
                TaskId = taskId,
                EventName = request.TriggerEvent,
                Enabled = true
            };
            _triggers.TryAdd(trigRecord.TriggerId, trigRecord);
        }

        return Task.FromResult(EnrichTask(record));
    }

    public Task<AgentTaskDto?> UpdateTaskAsync(string taskId, UpdateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var record))
            return Task.FromResult<AgentTaskDto?>(null);

        if (!string.IsNullOrWhiteSpace(request.Description))
            record.Description = request.Description;
        if (request.Instructions != null)
            record.Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions;
        if (!string.IsNullOrWhiteSpace(request.Priority))
            record.Priority = request.Priority;
        if (!string.IsNullOrWhiteSpace(request.Status))
            ApplyStatusTransition(record, request.Status);
        record.AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? null : request.AssignedTo;
        if (request.TargetRole != null)
            record.TargetRole = string.IsNullOrWhiteSpace(request.TargetRole) ? null : request.TargetRole;
        if (request.TaskType != null)
            record.TaskType = string.IsNullOrWhiteSpace(request.TaskType) ? null : request.TaskType;
        if (!string.IsNullOrWhiteSpace(request.Runtime))
            record.Runtime = request.Runtime;
        if (request.RequiredCapabilities != null)
            record.RequiredCapabilitiesJson = JsonSerializer.Serialize(request.RequiredCapabilities);
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
        if (request.EnabledAt.HasValue)
            record.EnabledAt = request.EnabledAt.Value;
        if (request.DisabledAt.HasValue)
            record.DisabledAt = request.DisabledAt.Value;

        return Task.FromResult<AgentTaskDto?>(EnrichTask(record));
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

    // ---- Schedules ----

    public Task<IReadOnlyList<AgentTaskScheduleDto>> ListSchedulesForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var result = _schedules.Values
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskScheduleDto>>(result);
    }

    public Task<IReadOnlyList<AgentTaskScheduleDto>> ListDueSchedulesAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var result = _schedules.Values
            .Where(s => s.Enabled && (s.NextRunAt == null || s.NextRunAt <= asOf))
            .OrderBy(s => s.NextRunAt)
            .Select(s => s.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskScheduleDto>>(result);
    }

    public Task<AgentTaskScheduleDto> CreateScheduleAsync(string taskId, CreateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var record = new AgentTaskScheduleRecord
        {
            ScheduleId = Guid.NewGuid(),
            TaskId = taskId,
            Cron = request.Cron,
            Label = request.Label,
            Enabled = request.Enabled,
            NextRunAt = AgentScheduleCalculator.GetNextRun(request.Cron, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow
        };
        _schedules.TryAdd(record.ScheduleId, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<AgentTaskScheduleDto?> UpdateScheduleAsync(Guid scheduleId, UpdateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default)
    {
        if (!_schedules.TryGetValue(scheduleId, out var record))
            return Task.FromResult<AgentTaskScheduleDto?>(null);

        if (!string.IsNullOrWhiteSpace(request.Cron))
        {
            record.Cron = request.Cron;
            record.NextRunAt = AgentScheduleCalculator.GetNextRun(request.Cron, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow;
        }
        if (request.Label != null)
            record.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label;
        if (request.Enabled.HasValue)
            record.Enabled = request.Enabled.Value;

        return Task.FromResult<AgentTaskScheduleDto?>(record.ToDto());
    }

    public Task<AgentTaskScheduleDto?> RecomputeScheduleNextRunAsync(Guid scheduleId, DateTimeOffset asOf, DateTimeOffset? lastRunAt, CancellationToken cancellationToken = default)
    {
        if (!_schedules.TryGetValue(scheduleId, out var record))
            return Task.FromResult<AgentTaskScheduleDto?>(null);

        record.NextRunAt = AgentScheduleCalculator.GetNextRun(record.Cron, asOf);
        if (lastRunAt.HasValue)
            record.LastRunAt = lastRunAt.Value;

        return Task.FromResult<AgentTaskScheduleDto?>(record.ToDto());
    }

    public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_schedules.TryRemove(scheduleId, out _));
    }

    // ---- Triggers ----

    public Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var result = _triggers.Values
            .Where(t => t.TaskId == taskId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskTriggerDto>>(result);
    }

    public Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersByEventAsync(string eventName, CancellationToken cancellationToken = default)
    {
        var result = _triggers.Values
            .Where(t => t.EventName.Equals(eventName, StringComparison.OrdinalIgnoreCase) && t.Enabled)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskTriggerDto>>(result);
    }

    public Task<AgentTaskTriggerDto> CreateTriggerAsync(string taskId, CreateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default)
    {
        var record = new AgentTaskTriggerRecord
        {
            TriggerId = Guid.NewGuid(),
            TaskId = taskId,
            EventName = request.EventName,
            Enabled = request.Enabled
        };
        _triggers.TryAdd(record.TriggerId, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<AgentTaskTriggerDto?> UpdateTriggerAsync(Guid triggerId, UpdateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default)
    {
        if (!_triggers.TryGetValue(triggerId, out var record))
            return Task.FromResult<AgentTaskTriggerDto?>(null);

        if (!string.IsNullOrWhiteSpace(request.EventName))
            record.EventName = request.EventName;
        if (request.Enabled.HasValue)
            record.Enabled = request.Enabled.Value;

        return Task.FromResult<AgentTaskTriggerDto?>(record.ToDto());
    }

    public Task<bool> DeleteTriggerAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_triggers.TryRemove(triggerId, out _));
    }

    // ---- Runs ----

    public Task<AgentTaskRunDto> CreateRunAsync(AgentTaskRunDto seed, CancellationToken cancellationToken = default)
    {
        var record = new AgentTaskRunRecord
        {
            RunId = seed.RunId,
            TaskId = seed.TaskId,
            ScheduleId = seed.ScheduleId,
            TriggerId = seed.TriggerId,
            AgentId = seed.AgentId,
            TriggerSource = seed.TriggerSource,
            Status = seed.Status,
            Runtime = seed.Runtime,
            CapabilitiesSnapshotJson = JsonSerializer.Serialize(seed.CapabilitiesSnapshot),
            CreatedAt = seed.CreatedAt,
            StartedAt = seed.StartedAt,
            CompletedAt = seed.CompletedAt,
            Output = seed.Output,
            Error = seed.Error,
            WorkspaceRef = seed.WorkspaceRef
        };
        _runs.TryAdd(record.RunId, record);
        return Task.FromResult(record.ToDto());
    }

    public Task<AgentTaskRunDto?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_runs.TryGetValue(runId, out var record) ? record.ToDto() : null);
    }

    public Task<AgentTaskRunDto?> UpdateRunAsync(Guid runId, string? status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? output, string? error, string? workspaceRef, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var record))
            return Task.FromResult<AgentTaskRunDto?>(null);

        if (!string.IsNullOrWhiteSpace(status))
            record.Status = status;
        if (startedAt.HasValue)
            record.StartedAt = startedAt.Value;
        if (completedAt.HasValue)
            record.CompletedAt = completedAt.Value;
        if (output != null)
            record.Output = output;
        if (error != null)
            record.Error = error;
        if (workspaceRef != null)
            record.WorkspaceRef = workspaceRef;

        return Task.FromResult<AgentTaskRunDto?>(record.ToDto());
    }

    public Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForTaskAsync(string taskId, int take = 50, CancellationToken cancellationToken = default)
    {
        var result = _runs.Values
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskRunDto>>(result);
    }

    public Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForAgentAsync(Guid agentId, int take = 50, CancellationToken cancellationToken = default)
    {
        var result = _runs.Values
            .Where(r => r.AgentId == agentId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskRunDto>>(result);
    }

    public Task<IReadOnlyList<AgentTaskRunDto>> ListPendingRunsAsync(CancellationToken cancellationToken = default)
    {
        var result = _runs.Values
            .Where(r => r.Status == "pending")
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.ToDto())
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskRunDto>>(result);
    }
}
