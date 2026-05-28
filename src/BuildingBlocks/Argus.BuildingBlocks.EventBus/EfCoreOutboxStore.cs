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
                      WHERE ""SourceService"" = {0}
                        AND ""ProcessedAt"" IS NULL
                        AND (""NextAttemptAt"" IS NULL OR ""NextAttemptAt"" <= {1})
                        AND (""LockedUntil"" IS NULL OR ""LockedUntil"" <= {1})
                      ORDER BY ""OccurredAt""
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
                      WHERE [SourceService] = {0}
                        AND [ProcessedAt] IS NULL
                        AND ([NextAttemptAt] IS NULL OR [NextAttemptAt] <= {1})
                        AND ([LockedUntil] IS NULL OR [LockedUntil] <= {1})
                      ORDER BY [OccurredAt]",
                    sourceService,
                    now,
                    limit)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            messages = await _dbContext.Set<OutboxMessage>()
                .FromSqlRaw(
                    @"SELECT * FROM outbox_messages
                      WHERE ""SourceService"" = {0}
                        AND ""ProcessedAt"" IS NULL
                        AND (""NextAttemptAt"" IS NULL OR ""NextAttemptAt"" <= {1})
                        AND (""LockedUntil"" IS NULL OR ""LockedUntil"" <= {1})
                      ORDER BY ""OccurredAt""
                      LIMIT {2}
                      FOR UPDATE SKIP LOCKED",
                    sourceService,
                    now,
                    limit)
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
