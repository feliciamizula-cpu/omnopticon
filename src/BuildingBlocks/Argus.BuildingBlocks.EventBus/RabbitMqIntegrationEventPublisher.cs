using Argus.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventBus;

public sealed class RabbitMqIntegrationEventPublisher(
    IConnection connection,
    IOptions<ArgusEventBusOptions> options,
    ILogger<RabbitMqIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    private readonly ArgusEventBusOptions _options = options.Value;

    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (!connection.IsOpen)
        {
            logger.LogWarning("RabbitMQ connection is closed; skipped {EventType} {EventId}", envelope.EventType, envelope.EventId);
            return;
        }

        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CorrelationId.ToString(),
            Type = envelope.EventType,
            Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: envelope.EventType,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
