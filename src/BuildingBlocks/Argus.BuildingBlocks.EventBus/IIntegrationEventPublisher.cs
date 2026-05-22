using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public interface IIntegrationEventPublisher
{
    Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull;
}
