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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IChannel? _channel;
    private bool _exchangeDeclared;

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel != null && _channel.IsOpen)
        {
            return _channel;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel == null || !_channel.IsOpen)
            {
                _channel?.Dispose();
                _channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
                _exchangeDeclared = false;
            }
            return _channel;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (!connection.IsOpen)
        {
            logger.LogWarning("RabbitMQ connection is closed for {EventType} {EventId}", envelope.EventType, envelope.EventId);
            throw new InvalidOperationException($"RabbitMQ connection is closed; cannot publish {envelope.EventType} {envelope.EventId}.");
        }

        var channel = await GetChannelAsync(cancellationToken);

        if (!_exchangeDeclared)
        {
            await channel.ExchangeDeclareAsync(
                exchange: _options.ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
            _exchangeDeclared = true;
        }

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

    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (!connection.IsOpen)
        {
            logger.LogWarning("RabbitMQ connection is closed for {EventType} {EventId}", envelope.EventType, envelope.EventId);
            throw new InvalidOperationException($"RabbitMQ connection is closed; cannot publish {envelope.EventType} {envelope.EventId}.");
        }

        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            if (!_exchangeDeclared)
            {
                await _channel.ExchangeDeclareAsync(
                    exchange: _options.ExchangeName,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    cancellationToken: cancellationToken);
                _exchangeDeclared = true;
            }

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

            await _channel.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: envelope.EventType,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _channelLock.Release();
        }
    }
}
