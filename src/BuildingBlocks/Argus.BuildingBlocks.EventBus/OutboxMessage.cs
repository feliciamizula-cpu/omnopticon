namespace Argus.BuildingBlocks.EventBus;

public sealed class OutboxMessage
{
    public Guid OutboxMessageId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SourceService { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockOwner { get; set; }
    public int AttemptCount { get; set; }
    public string? Error { get; set; }
}

public sealed record InboxMessage(
    Guid EventId,
    string EventType,
    string Consumer,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    string? Error);
