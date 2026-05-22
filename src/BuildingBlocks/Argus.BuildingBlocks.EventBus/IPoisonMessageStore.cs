using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public interface IPoisonMessageStore
{
    void RecordPoison(IntegrationEventEnvelope<JsonElement> envelope, Exception exception);
    IReadOnlyCollection<PoisonMessageRecord> GetMessages(int take);
    PoisonMessageRecord? GetMessage(Guid eventId);
    bool MarkReplayed(Guid eventId);
    bool RemoveMessage(Guid eventId);
    void UpdateRetryCount(Guid eventId, int attemptCount);
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