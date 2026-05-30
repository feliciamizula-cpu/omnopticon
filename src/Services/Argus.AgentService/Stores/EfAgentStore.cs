namespace Argus.AgentService.Stores;

using System.Text.Json;
using Argus.AgentService.Agents;
using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Agents;
using Microsoft.EntityFrameworkCore;

public sealed class EfAgentStore(AgentDbContext dbContext) : IAgentStore
{
    private readonly AgentDbContext _dbContext = dbContext;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await _dbContext.Database.MigrateAsync(cancellationToken);
                await _dbContext.Database.EnsureArgusOutboxCreatedAsync(cancellationToken);
                await _dbContext.Database.EnsureArgusInboxCreatedAsync(cancellationToken);
                break;
            }
            catch
            {
                await Task.Delay(2000, cancellationToken);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var hasAgents = await _dbContext.Agents.AnyAsync(cancellationToken);
        if (!hasAgents)
        {
            _dbContext.Agents.AddRange(AgentDevelopmentSeedData.CreateAgents(now));
        }

        var hasTasks = await _dbContext.AgentTasks.AnyAsync(cancellationToken);
        if (!hasTasks)
        {
            _dbContext.AgentTasks.AddRange(AgentDevelopmentSeedData.CreateTasks(now));
        }

        var hasProviderAccounts = await _dbContext.ProviderAccounts.AnyAsync(cancellationToken);
        if (!hasProviderAccounts)
        {
            _dbContext.ProviderAccounts.AddRange(ProviderUsageSeedData.CreateAccounts(now));
        }

        if (!hasAgents || !hasTasks || !hasProviderAccounts)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }


    public async Task<IReadOnlyList<AgentDto>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Agents
            .AsNoTracking()
            .Select(a => a.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentDto?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _dbContext.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
        return agent?.ToDto();
    }

    public async Task<AgentDto> CreateAgentAsync(CreateAgentRequest request, CancellationToken cancellationToken = default)
    {
        var record = new AgentRecord
        {
            AgentId = Guid.NewGuid(),
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

        _dbContext.Agents.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentDto?> UpdateAgentAsync(Guid agentId, UpdateAgentRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.Agents
            .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
        if (record is null)
            return null;

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
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<bool> DeleteAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.Agents
            .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
        if (record is null)
            return false;

        _dbContext.Agents.Remove(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AgentTaskDto>> ListTasksAsync(string? status = null, string? priority = null, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.AgentTasks.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(priority))
            query = query.Where(t => t.Priority == priority);

        var records = await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = new List<AgentTaskDto>(records.Count);
        foreach (var r in records)
            result.Add(await EnrichAsync(r, cancellationToken));
        return result;
    }

    public async Task<AgentTaskDto?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.AgentTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (task is null) return null;
        return await EnrichAsync(task, cancellationToken);
    }

    private async Task<AgentTaskDto> EnrichAsync(AgentTaskRecord r, CancellationToken ct)
    {
        var firstSched = await _dbContext.AgentTaskSchedules
            .Where(s => s.TaskId == r.TaskId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Cron)
            .FirstOrDefaultAsync(ct);
        var firstTrig = await _dbContext.AgentTaskTriggers
            .Where(t => t.TaskId == r.TaskId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.EventName)
            .FirstOrDefaultAsync(ct);
        return r.ToDto(firstSched, firstTrig);
    }

    public async Task<AgentTaskDto> CreateTaskAsync(CreateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var taskIds = await _dbContext.AgentTasks
            .AsNoTracking()
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);
        var taskId = AgentDevelopmentSeedData.NextNumericTaskId(taskIds).ToString("000");

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

        _dbContext.AgentTasks.Add(record);

        if (request.ScheduleExpression is not null)
        {
            var scheduleRecord = new AgentTaskScheduleRecord
            {
                ScheduleId = Guid.NewGuid(),
                TaskId = taskId,
                Cron = request.ScheduleExpression,
                Enabled = true,
                NextRunAt = AgentScheduleCalculator.GetNextRun(request.ScheduleExpression, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow
            };
            _dbContext.AgentTaskSchedules.Add(scheduleRecord);
        }

        if (request.TriggerEvent is not null)
        {
            var triggerRecord = new AgentTaskTriggerRecord
            {
                TriggerId = Guid.NewGuid(),
                TaskId = taskId,
                EventName = request.TriggerEvent,
                Enabled = true
            };
            _dbContext.AgentTaskTriggers.Add(triggerRecord);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await EnrichAsync(record, cancellationToken);
    }

    public async Task<AgentTaskDto?> UpdateTaskAsync(string taskId, UpdateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTasks
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (record is null)
            return null;

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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await EnrichAsync(record, cancellationToken);
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

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTasks
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (record is null)
            return false;

        _dbContext.AgentTasks.Remove(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetChatHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ChatMessages
            .AsNoTracking()
            .OrderBy(m => m.CreatedAt)
            .TakeLast(limit)
            .Select(m => m.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatMessageDto> SaveChatMessageAsync(string role, string content, CancellationToken cancellationToken = default)
    {
        var record = new ChatMessageRecord
        {
            MessageId = Guid.NewGuid(),
            Role = role,
            Content = content
        };

        _dbContext.ChatMessages.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<IReadOnlyList<CodeReviewDto>> ListCodeReviewsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CodeReviews
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SystemReportDto>> ListSystemReportsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        return await _dbContext.SystemReports
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToListAsync(cancellationToken);
    }

    // ---- Schedules ----

    public async Task<IReadOnlyList<AgentTaskScheduleDto>> ListSchedulesForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskSchedules
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTaskScheduleDto>> ListDueSchedulesAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskSchedules
            .AsNoTracking()
            .Where(s => s.Enabled && (s.NextRunAt == null || s.NextRunAt <= asOf))
            .OrderBy(s => s.NextRunAt)
            .Select(s => s.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentTaskScheduleDto> CreateScheduleAsync(string taskId, CreateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default)
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
        _dbContext.AgentTaskSchedules.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentTaskScheduleDto?> UpdateScheduleAsync(Guid scheduleId, UpdateAgentTaskScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskSchedules
            .FirstOrDefaultAsync(s => s.ScheduleId == scheduleId, cancellationToken);
        if (record is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Cron))
        {
            record.Cron = request.Cron;
            record.NextRunAt = AgentScheduleCalculator.GetNextRun(request.Cron, DateTimeOffset.UtcNow.AddSeconds(-1)) ?? DateTimeOffset.UtcNow;
        }
        if (request.Label != null)
            record.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label;
        if (request.Enabled.HasValue)
            record.Enabled = request.Enabled.Value;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentTaskScheduleDto?> RecomputeScheduleNextRunAsync(Guid scheduleId, DateTimeOffset asOf, DateTimeOffset? lastRunAt, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskSchedules
            .FirstOrDefaultAsync(s => s.ScheduleId == scheduleId, cancellationToken);
        if (record is null) return null;

        record.NextRunAt = AgentScheduleCalculator.GetNextRun(record.Cron, asOf);
        if (lastRunAt.HasValue)
            record.LastRunAt = lastRunAt.Value;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskSchedules
            .FirstOrDefaultAsync(s => s.ScheduleId == scheduleId, cancellationToken);
        if (record is null) return false;
        _dbContext.AgentTaskSchedules.Remove(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ---- Triggers ----

    public async Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskTriggers
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTaskTriggerDto>> ListTriggersByEventAsync(string eventName, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskTriggers
            .AsNoTracking()
            .Where(t => t.EventName == eventName && t.Enabled)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentTaskTriggerDto> CreateTriggerAsync(string taskId, CreateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default)
    {
        var record = new AgentTaskTriggerRecord
        {
            TriggerId = Guid.NewGuid(),
            TaskId = taskId,
            EventName = request.EventName,
            Enabled = request.Enabled
        };
        _dbContext.AgentTaskTriggers.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentTaskTriggerDto?> UpdateTriggerAsync(Guid triggerId, UpdateAgentTaskTriggerRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskTriggers
            .FirstOrDefaultAsync(t => t.TriggerId == triggerId, cancellationToken);
        if (record is null) return null;

        if (!string.IsNullOrWhiteSpace(request.EventName))
            record.EventName = request.EventName;
        if (request.Enabled.HasValue)
            record.Enabled = request.Enabled.Value;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<bool> DeleteTriggerAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskTriggers
            .FirstOrDefaultAsync(t => t.TriggerId == triggerId, cancellationToken);
        if (record is null) return false;
        _dbContext.AgentTaskTriggers.Remove(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ---- Runs ----

    public async Task<AgentTaskRunDto> CreateRunAsync(AgentTaskRunDto seed, CancellationToken cancellationToken = default)
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
        _dbContext.AgentTaskRuns.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentTaskRunDto?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskRuns
            .FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken);
        return record?.ToDto();
    }

    public async Task<AgentTaskRunDto?> UpdateRunAsync(Guid runId, string? status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? output, string? error, string? workspaceRef, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTaskRuns
            .FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken);
        if (record is null) return null;

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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForTaskAsync(string taskId, int take = 50, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskRuns
            .AsNoTracking()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTaskRunDto>> ListRunsForAgentAsync(Guid agentId, int take = 50, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskRuns
            .AsNoTracking()
            .Where(r => r.AgentId == agentId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => r.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTaskRunDto>> ListPendingRunsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentTaskRuns
            .AsNoTracking()
            .Where(r => r.Status == "pending")
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.ToDto())
            .ToListAsync(cancellationToken);
    }
}
