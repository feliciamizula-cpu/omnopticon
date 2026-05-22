namespace Argus.BuildingBlocks.EventBus;

public interface IOutboxStore
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<OutboxMessage>> ClaimPendingAsync(
        string sourceService,
        string lockOwner,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken);

    Task MarkProcessedAsync(Guid outboxMessageId, CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid outboxMessageId,
        string error,
        TimeSpan retryAfter,
        CancellationToken cancellationToken);
}
