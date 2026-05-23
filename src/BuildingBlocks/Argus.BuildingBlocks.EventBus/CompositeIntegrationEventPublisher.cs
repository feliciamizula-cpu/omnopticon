using Argus.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.EventBus;

public sealed class CompositeIntegrationEventPublisher(
    RealtimeIntegrationEventPublisher realtimePublisher,
    IEnumerable<RabbitMqIntegrationEventPublisher> rabbitMqPublishers,
    ILogger<CompositeIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    private readonly RabbitMqIntegrationEventPublisher? _rabbitMqPublisher = rabbitMqPublishers.FirstOrDefault();

    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        await PublishRealtimeAsync(envelope, cancellationToken);

        if (_rabbitMqPublisher is not null)
        {
            await PublishRabbitMqAsync(envelope, cancellationToken);
        }
    }

    private async Task PublishRealtimeAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            await realtimePublisher.PublishAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Realtime publisher failed unexpectedly for {EventType} {EventId}",
                envelope.EventType,
                envelope.EventId);
        }
    }

    private async Task PublishRabbitMqAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            await _rabbitMqPublisher!.PublishAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "RabbitMQ publisher failed for {EventType} {EventId}",
                envelope.EventType,
                envelope.EventId);
        }
    }
}
