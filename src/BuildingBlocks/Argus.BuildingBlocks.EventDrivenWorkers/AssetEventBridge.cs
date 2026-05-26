using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.BuildingBlocks.EventBus;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.EventDrivenWorkers;

public sealed class AssetEventBridge(
    EphemeralWorkerDispatcher dispatcher,
    ILogger<AssetEventBridge> logger) :
    IIntegrationEventConsumer<AssetDiscovered>,
    IIntegrationEventConsumer<AssetCreated>,
    IIntegrationEventConsumer<AssetConfirmed>,
    IIntegrationEventConsumer<AssetUpdated>,
    IIntegrationEventConsumer<AssetPropertyChanged>
{
    public async Task HandleAsync(IntegrationEventEnvelope<AssetDiscovered> envelope, CancellationToken cancellationToken = default)
    {
        await DispatchAsync(envelope, cancellationToken);
    }

    public async Task HandleAsync(IntegrationEventEnvelope<AssetCreated> envelope, CancellationToken cancellationToken = default)
    {
        await DispatchAsync(envelope, cancellationToken);
    }

    public async Task HandleAsync(IntegrationEventEnvelope<AssetConfirmed> envelope, CancellationToken cancellationToken = default)
    {
        await DispatchAsync(envelope, cancellationToken);
    }

    public async Task HandleAsync(IntegrationEventEnvelope<AssetUpdated> envelope, CancellationToken cancellationToken = default)
    {
        await DispatchAsync(envelope, cancellationToken);
    }

    public async Task HandleAsync(IntegrationEventEnvelope<AssetPropertyChanged> envelope, CancellationToken cancellationToken = default)
    {
        await DispatchAsync(envelope, cancellationToken);
    }

    private async Task DispatchAsync<T>(IntegrationEventEnvelope<T> envelope, CancellationToken cancellationToken)
        where T : notnull
    {
        logger.LogDebug("Bridging event {EventId} of type {EventType} to ephemeral workers",
            envelope.EventId, envelope.EventType);

        await dispatcher.DispatchAsync(envelope, cancellationToken);
    }
}
