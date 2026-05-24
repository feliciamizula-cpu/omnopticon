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
            Model = request.Model
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

        return await query
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentTaskDto?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.AgentTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        return task?.ToDto();
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

        _dbContext.AgentTasks.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<AgentTaskDto?> UpdateTaskAsync(string taskId, UpdateAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AgentTasks
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (record is null)
            return null;

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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
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
}
