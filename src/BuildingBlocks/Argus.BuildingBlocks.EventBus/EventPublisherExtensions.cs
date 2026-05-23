using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public static class EventPublisherExtensions
{
    public static Task PublishAsync<T>(
        this IIntegrationEventPublisher publisher,
        T payload,
        string eventType,
        string sourceService,
        Guid? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        var envelope = IntegrationEventEnvelope<T>.Create(
            payload,
            eventType,
            sourceService,
            correlationId: correlationId,
            causationId: causationId);

        return publisher.PublishAsync(envelope, cancellationToken);
    }
}
