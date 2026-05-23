using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public interface IPoisonMessageStore
{
    Task RecordPoisonAsync(IntegrationEventEnvelope<JsonElement> envelope, Exception exception, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PoisonMessageRecord>> GetMessagesAsync(int take, CancellationToken cancellationToken = default);
    Task<PoisonMessageRecord?> GetMessageAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<bool> MarkReplayedAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<bool> RemoveMessageAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed record PoisonMessageRecord(
    Guid EventId,
    string EventType,
    string? SourceService,
    Guid? CorrelationId,
    Guid? CausationId,
    JsonElement Payload,
    DateTimeOffset FailedAt,
    string? ErrorMessage,
    string? ErrorType,
    int AttemptCount,
    bool IsReplayed,
    DateTimeOffset? ReplayedAt)
{
    public string PayloadJson => Payload.GetRawText();
}

public sealed class InMemoryPoisonMessageStore : IPoisonMessageStore
{
    private readonly ConcurrentDictionary<Guid, PoisonMessageRecord> _messages = new();

    public Task<IReadOnlyList<PoisonMessageRecord>> GetMessagesAsync(int take, CancellationToken cancellationToken = default)
    {
        var result = _messages.Values
            .Where(m => !m.IsReplayed)
            .OrderByDescending(m => m.FailedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToArray();
        return Task.FromResult<IReadOnlyList<PoisonMessageRecord>>(result);
    }

    public Task<PoisonMessageRecord?> GetMessageAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _messages.TryGetValue(eventId, out var record);
        return Task.FromResult(record);
    }

    public Task<bool> MarkReplayedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(eventId, out var record))
        {
            var updated = record with { IsReplayed = true, ReplayedAt = DateTimeOffset.UtcNow };
            _messages[eventId] = updated;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> RemoveMessageAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_messages.TryRemove(eventId, out _));
    }

    public Task RecordPoisonAsync(IntegrationEventEnvelope<JsonElement> envelope, Exception exception, CancellationToken cancellationToken = default)
    {
        var record = new PoisonMessageRecord(
            envelope.EventId,
            envelope.EventType,
            envelope.SourceService,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.Payload,
            DateTimeOffset.UtcNow,
            exception.Message,
            exception.GetType().Name,
            1,
            false,
            null);

        _messages[envelope.EventId] = record;
        return Task.CompletedTask;
    }
}

public sealed class EfCorePoisonMessageStore<TDbContext> : IPoisonMessageStore
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public EfCorePoisonMessageStore(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordPoisonAsync(IntegrationEventEnvelope<JsonElement> envelope, Exception exception, CancellationToken cancellationToken = default)
    {
        var record = new PoisonMessageRecord(
            envelope.EventId,
            envelope.EventType,
            envelope.SourceService,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.Payload,
            DateTimeOffset.UtcNow,
            exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message,
            exception.GetType().Name,
            1,
            false,
            null);

        _dbContext.Set<PoisonMessageRecord>().Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PoisonMessageRecord>> GetMessagesAsync(int take, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<PoisonMessageRecord>()
            .Where(r => !r.IsReplayed)
            .OrderByDescending(r => r.FailedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<PoisonMessageRecord?> GetMessageAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<PoisonMessageRecord>()
            .FirstOrDefaultAsync(r => r.EventId == eventId, cancellationToken);
    }

    public async Task<bool> MarkReplayedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.Set<PoisonMessageRecord>()
            .FirstOrDefaultAsync(r => r.EventId == eventId, cancellationToken);

        if (record is null) return false;

        record.IsReplayed = true;
        record.ReplayedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveMessageAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.Set<PoisonMessageRecord>()
            .FirstOrDefaultAsync(r => r.EventId == eventId, cancellationToken);

        if (record is null) return false;

        _dbContext.Set<PoisonMessageRecord>().Remove(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public static class PoisonMessageModelBuilderExtensions
{
    public static ModelBuilder ConfigurePoisonMessages(this ModelBuilder modelBuilder)
    {
        var poison = modelBuilder.Entity<PoisonMessageRecord>();
        poison.ToTable("poison_messages");
        poison.HasKey(r => r.EventId);
        poison.HasIndex(r => new { r.EventType, r.FailedAt });
        poison.HasIndex(r => r.IsReplayed);
        poison.Property(r => r.EventType).HasMaxLength(256);
        poison.Property(r => r.SourceService).HasMaxLength(256);
        poison.Property(r => r.PayloadJson).HasColumnType("jsonb");
        poison.Property(r => r.ErrorMessage).HasMaxLength(2048);
        poison.Property(r => r.ErrorType).HasMaxLength(256);

        return modelBuilder;
    }
}