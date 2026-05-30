namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class AgentRecord
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? RoleDescription { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; } = "active";
    public string ResponsibilitiesJson { get; set; } = "[]";
    public string? CurrentTaskId { get; set; }
    public string WorkStatus { get; set; } = "idle";
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastError { get; set; }
    public string? Context { get; set; }
    public string Tool { get; set; } = "opencode";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string? Provider { get; set; }
    public string Priority { get; set; } = "standard";
    public string CapabilitiesJson { get; set; } = "[]";
    public string DefaultRuntime { get; set; } = AgentCapabilities.RuntimeInPod;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentDto ToDto()
    {
        var responsibilities = System.Text.Json.JsonSerializer.Deserialize<string[]>(ResponsibilitiesJson) ?? [];
        var capabilities = System.Text.Json.JsonSerializer.Deserialize<string[]>(CapabilitiesJson) ?? [];
        return new AgentDto(
            AgentId, Name, Role, RoleDescription, SortOrder, Status, responsibilities, CurrentTaskId, WorkStatus,
            LastHeartbeatAt, LastError, Tool, Model, Provider, Priority,
            capabilities, DefaultRuntime,
            CreatedAt, UpdatedAt);
    }
}
