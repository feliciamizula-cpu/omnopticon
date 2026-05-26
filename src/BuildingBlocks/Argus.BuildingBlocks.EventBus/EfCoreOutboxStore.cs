using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Argus.BuildingBlocks.EventBus;

public sealed class EfCoreOutboxStore<TDbContext> : IOutboxStore
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly string _databaseProvider;

    public EfCoreOutboxStore(TDbContext dbContext)
    {
        _dbContext = dbContext;
        _databaseProvider = dbContext.Database.ProviderName ?? string.Empty;
    }

    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        _dbContext.Set<OutboxMessage>().Add(message);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<OutboxMessage>> ClaimPendingAsync(
        string sourceService,
        string lockOwner,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockUntil = now.Add(lockDuration);
        var limit = Math.Clamp(batchSize, 1, 500);

        IReadOnlyCollection<OutboxMessage> messages;

        if (_databaseProvider.Contains("Npgsql"))
        {
            messages = await _dbContext.Set<OutboxMessage>()
                .FromSqlRaw(
                    @"SELECT * FROM outbox_messages
                      WHERE source_service = {0}
                        AND processed_at IS NULL
                        AND (next_attempt_at IS NULL OR next_attempt_at <= {1})
                        AND (locked_until IS NULL OR locked_until <= {1})
                      ORDER BY occurred_at
                      LIMIT {2}
                      FOR UPDATE SKIP LOCKED",
                    sourceService,
                    now,
                    limit)
                .ToArrayAsync(cancellationToken);
        }
        else if (_databaseProvider.Contains("SqlServer"))
        {
            messages = await _dbContext.Set<OutboxMessage>()
                .FromSqlRaw(
                    @"SELECT TOP ({2}) * FROM outbox_messages WITH (ROWLOCK, READPAST)
                      WHERE source_service = {0}
                        AND processed_at IS NULL
                        AND (next_attempt_at IS NULL OR next_attempt_at <= {1})
                        AND (locked_until IS NULL OR locked_until <= {1})
                      ORDER BY occurred_at",
                    sourceService,
                    now,
                    limit)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            messages = await _dbContext.Set<OutboxMessage>()
                .Where(m =>
                    m.SourceService == sourceService
                    && m.ProcessedAt == null
                    && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                    && (m.LockedUntil == null || m.LockedUntil <= now))
                .OrderBy(m => m.OccurredAt)
                .Take(limit)
                .ToArrayAsync(cancellationToken);
        }

        foreach (var message in messages)
        {
            message.LockOwner = lockOwner;
            message.LockedUntil = lockUntil;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return messages;
    }

    public async Task MarkProcessedAsync(Guid outboxMessageId, CancellationToken cancellationToken)
    {
        var message = await _dbContext.Set<OutboxMessage>()
            .FirstOrDefaultAsync(message => message.OutboxMessageId == outboxMessageId, cancellationToken);

        if (message is null)
        {
            return;
        }

        message.ProcessedAt = DateTimeOffset.UtcNow;
        message.LockOwner = null;
        message.LockedUntil = null;
        message.Error = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid outboxMessageId,
        string error,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        var message = await _dbContext.Set<OutboxMessage>()
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

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
