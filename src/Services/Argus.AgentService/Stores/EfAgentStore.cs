namespace Argus.AgentService.Stores;

using System.Text.Json;
using Argus.AgentService.Data;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Agents;
using Microsoft.EntityFrameworkCore;

public sealed class EfAgentStore(AgentDbContext dbContext) : IAgentStore
{
    private readonly AgentDbContext _dbContext = dbContext;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _dbContext.Database.MigrateAsync(cancellationToken);
        }
        catch
        {
            // Migration might fail if outbox tables already exist, continue anyway
        }

        try
        {
            await _dbContext.Database.EnsureArgusOutboxCreatedAsync(cancellationToken);
            await _dbContext.Database.EnsureArgusInboxCreatedAsync(cancellationToken);
        }
        catch
        {
            // Tables might already exist from another service
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

        if (!hasAgents || !hasTasks)
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
        if (!string.IsNullOrWhiteSpace(request.Status))
            record.Status = request.Status;
        if (request.Responsibilities != null)
            record.ResponsibilitiesJson = JsonSerializer.Serialize(request.Responsibilities);
        if (!string.IsNullOrWhiteSpace(request.Tool))
            record.Tool = request.Tool;
        if (!string.IsNullOrWhiteSpace(request.Model))
            record.Model = request.Model;

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
            Status = "pending",
            AssignedTo = request.AssignedTo
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
        if (request.AssignedTo != null)
            record.AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? null : request.AssignedTo;

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
}
