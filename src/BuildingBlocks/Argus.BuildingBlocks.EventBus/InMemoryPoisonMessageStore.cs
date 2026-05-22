using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public sealed class InMemoryPoisonMessageStore : IPoisonMessageStore
{
    private readonly ConcurrentDictionary<Guid, PoisonMessageRecord> _messages = new();

    public IReadOnlyCollection<PoisonMessageRecord> GetMessages(int take) =>
        _messages.Values
            .OrderByDescending(m => m.FailedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToArray();

    public PoisonMessageRecord? GetMessage(Guid eventId) =>
        _messages.TryGetValue(eventId, out var record) ? record : null;

    public bool MarkReplayed(Guid eventId)
    {
        if (_messages.TryGetValue(eventId, out var record))
        {
            _messages[eventId] = record with { IsReplayed = true, ReplayedAt = DateTimeOffset.UtcNow };
            return true;
        }
        return false;
    }

    public bool RemoveMessage(Guid eventId) =>
        _messages.TryRemove(eventId, out _);

    public void RecordPoison(IntegrationEventEnvelope<JsonElement> envelope, Exception exception)
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
    }

    public void UpdateRetryCount(Guid eventId, int attemptCount)
    {
        if (_messages.TryGetValue(eventId, out var record))
        {
            _messages[eventId] = record with { AttemptCount = attemptCount };
        }
    }
}