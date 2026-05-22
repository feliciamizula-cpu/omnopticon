using Argus.Contracts.Events;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventBus;

public static class OutboxMessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static OutboxMessage FromEnvelope<T>(IntegrationEventEnvelope<T> envelope)
        where T : notnull =>
        new()
        {
            OutboxMessageId = Guid.NewGuid(),
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            SourceService = envelope.SourceService,
            EnvelopeJson = JsonSerializer.Serialize(envelope, SerializerOptions),
            OccurredAt = envelope.OccurredAt,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

    public static IntegrationEventEnvelope<JsonElement> ToJsonEnvelope(OutboxMessage message) =>
        JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(message.EnvelopeJson, SerializerOptions)
        ?? throw new InvalidOperationException($"Outbox message {message.OutboxMessageId} does not contain a valid integration event envelope.");
}
