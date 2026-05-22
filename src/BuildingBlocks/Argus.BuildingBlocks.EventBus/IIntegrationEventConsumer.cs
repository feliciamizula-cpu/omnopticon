using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public interface IIntegrationEventConsumer<T>
    where T : notnull
{
    Task HandleAsync(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default);
}
