using Microsoft.EntityFrameworkCore;

namespace Argus.BuildingBlocks.EventBus;

public sealed class EfCoreOutboxStore<TDbContext>(TDbContext dbContext) : IOutboxStore
    where TDbContext : DbContext
{
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        dbContext.Set<OutboxMessage>().Add(message);
    }

    public async Task<IReadOnlyCollection<OutboxMessage>> ClaimPendingAsync(
        string sourceService,
        string lockOwner,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var messages = await dbContext.Set<OutboxMessage>()
            .Where(message =>
                message.SourceService == sourceService
                && message.ProcessedAt == null
                && (message.NextAttemptAt == null || message.NextAttemptAt <= now)
                && (message.LockedUntil == null || message.LockedUntil <= now))
            .OrderBy(message => message.OccurredAt)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToArrayAsync(cancellationToken);

        foreach (var message in messages)
        {
            message.LockOwner = lockOwner;
            message.LockedUntil = now.Add(lockDuration);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return messages;
    }

    public async Task MarkProcessedAsync(Guid outboxMessageId, CancellationToken cancellationToken)
    {
        var message = await dbContext.Set<OutboxMessage>()
            .FirstOrDefaultAsync(message => message.OutboxMessageId == outboxMessageId, cancellationToken);

        if (message is null)
        {
            return;
        }

        message.ProcessedAt = DateTimeOffset.UtcNow;
        message.LockOwner = null;
        message.LockedUntil = null;
        message.Error = null;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid outboxMessageId,
        string error,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        var message = await dbContext.Set<OutboxMessage>()
            .FirstOrDefaultAsync(message => message.OutboxMessageId == outboxMessageId, cancellationToken);

        if (message is null)
        {
            return;
        }

        message.AttemptCount++;
        message.Error = error.Length > 2048 ? error[..2048] : error;
        message.NextAttemptAt = DateTimeOffset.UtcNow.Add(retryAfter);
        message.LockOwner = null;
        message.LockedUntil = null;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
