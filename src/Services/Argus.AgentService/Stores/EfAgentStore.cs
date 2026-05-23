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

        var hasAgents = await _dbContext.Agents.AnyAsync(cancellationToken);
        if (!hasAgents)
        {
            SeedDefaultAgents();
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private void SeedDefaultAgents()
    {
        var devAgents = new[]
        {
            ("agent-1", "Agent 1", "development"),
            ("agent-2", "Agent 2", "development"),
            ("agent-3", "Agent 3", "development"),
            ("agent-4", "Agent 4", "development"),
            ("agent-5", "Agent 5", "development"),
        };

        foreach (var (id, name, role) in devAgents)
        {
            var guid = GuidFromId(id);
            _dbContext.Agents.Add(new AgentRecord
            {
                AgentId = guid,
                Name = name,
                Role = role,
                Status = "active",
                ResponsibilitiesJson = JsonSerializer.Serialize(new[] { "application implementation" }),
                Tool = "opencode",
                Model = "claude-sonnet-4-6"
            });
        }

        var devopsAgents = new[]
        {
            ("devops-1", "DevOps Agent 1"),
            ("devops-2", "DevOps Agent 2"),
        };

        foreach (var (id, name) in devopsAgents)
        {
            var guid = GuidFromId(id);
            _dbContext.Agents.Add(new AgentRecord
            {
                AgentId = guid,
                Name = name,
                Role = "devops",
                Status = "active",
                ResponsibilitiesJson = JsonSerializer.Serialize(new[] { "system health", "deployment", "coordination" }),
                Tool = "opencode",
                Model = "claude-sonnet-4-6"
            });
        }
    }

    private static Guid GuidFromId(string id)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return new Guid(hash);
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
        var taskId = (await _dbContext.AgentTasks.MaxAsync(t => (int?)int.Parse(t.TaskId) ?? 0, cancellationToken) + 1).ToString("000");

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
            record.Status = request.Status;
        if (request.AssignedTo != null)
            record.AssignedTo = request.AssignedTo;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
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
