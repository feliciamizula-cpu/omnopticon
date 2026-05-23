namespace Argus.AgentService.Stores;

using System.Collections.Concurrent;
using System.Text.Json;
using Argus.AgentService.Data;
using Argus.Contracts.Agents;

public sealed class InMemoryAgentStore : IAgentStore
{
    private readonly ConcurrentDictionary<Guid, AgentRecord> _agents = new();
    private readonly ConcurrentDictionary<string, AgentTaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<Guid, ChatMessageRecord> _chatMessages = new();
    private int _nextTaskId = 1;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SeedDefaultAgents();
        return Task.CompletedTask;
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
            _agents.TryAdd(guid, new AgentRecord
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
            _agents.TryAdd(guid, new AgentRecord
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
        if (!string.IsNullOrWhiteSpace(request.Status))
            record.Status = request.Status;
        if (request.Responsibilities != null)
            record.ResponsibilitiesJson = JsonSerializer.Serialize(request.Responsibilities);
        if (!string.IsNullOrWhiteSpace(request.Tool))
            record.Tool = request.Tool;
        if (!string.IsNullOrWhiteSpace(request.Model))
            record.Model = request.Model;

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
            Status = "pending",
            AssignedTo = request.AssignedTo
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
            record.Status = request.Status;
        if (request.AssignedTo != null)
            record.AssignedTo = request.AssignedTo;

        return Task.FromResult<AgentTaskDto?>(record.ToDto());
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
}
