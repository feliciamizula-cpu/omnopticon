namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class ChatMessageRecord
{
    public Guid MessageId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ChatMessageDto ToDto() =>
        new(MessageId, Role, Content, CreatedAt);
}
