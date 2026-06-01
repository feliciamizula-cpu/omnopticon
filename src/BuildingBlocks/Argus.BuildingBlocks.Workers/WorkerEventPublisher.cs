using Argus.Contracts.Events;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text.Json;

namespace Argus.BuildingBlocks.Workers;

/// <summary>
/// Minimal RabbitMQ publisher used by recon workers to emit integration events (e.g. AssetProduced)
/// to the shared topic exchange. Recon workers no longer write to asset-service directly — they emit
/// and the storage worker is the sole writer.
/// </summary>
internal sealed class WorkerEventPublisher : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _exchangeName;
    private readonly string _sourceService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _exchangeDeclared;

    public WorkerEventPublisher(string connectionString, string exchangeName, string sourceService, ILogger logger)
    {
        _connectionString = connectionString;
        _exchangeName = exchangeName;
        _sourceService = sourceService;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T payload, string eventType, CancellationToken cancellationToken)
        where T : notnull
    {
        var channel = await EnsureChannelAsync(cancellationToken);

        var envelope = IntegrationEventEnvelope<T>.Create(payload, eventType, _sourceService);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CorrelationId.ToString(),
            Type = eventType
        };

        await channel.BasicPublishAsync(
            exchange: _exchangeName,
            routingKey: eventType,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken);
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            if (!_exchangeDeclared)
            {
                await _channel.ExchangeDeclareAsync(_exchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
                _exchangeDeclared = true;
            }

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
