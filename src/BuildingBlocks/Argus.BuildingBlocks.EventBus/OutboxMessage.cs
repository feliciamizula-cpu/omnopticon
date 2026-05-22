namespace Argus.BuildingBlocks.EventBus;

public sealed record OutboxMessage(
    Guid OutboxMessageId,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ProcessedAt,
    string? Error);

public sealed record InboxMessage(
    Guid EventId,
    string EventType,
    string Consumer,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    string? Error);
