using Argus.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.EventBus;

public sealed class DurableIntegrationEventPublisher(
    IOutboxStore outboxStore,
    ILogger<DurableIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        var message = OutboxMessageSerializer.FromEnvelope(envelope);
        await outboxStore.EnqueueAsync(message, cancellationToken);

        logger.LogDebug(
            "Queued integration event {EventType} {EventId} in outbox message {OutboxMessageId}",
            envelope.EventType,
            envelope.EventId,
            message.OutboxMessageId);
    }
}
