namespace Argus.Web;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

/// <summary>
/// Bridges the event bus to the browser: subscribes to asset lifecycle events and broadcasts each as
/// a SignalR <see cref="AssetDelta"/> so the Operations grid updates the instant a worker discovers an
/// asset — no polling/refresh. Each web instance uses its own exclusive queue so it always receives a
/// copy and can push to its own connected browsers (correct for multiple web replicas).
/// </summary>
public sealed class AssetDeltaBroadcastService(
    IConfiguration configuration,
    DevelopmentRealtimeNotifier notifier,
    ILogger<AssetDeltaBroadcastService> logger) : BackgroundService, IAsyncDisposable
{
    private const string Exchange = "argus.integration.events";
    private static readonly string[] EventTypes = ["AssetDiscovered", "AssetConfirmed", "AssetUpdated"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connStr = configuration.GetConnectionString("eventbus") ?? configuration["ConnectionStrings__eventbus"];
        if (string.IsNullOrWhiteSpace(connStr))
        {
            logger.LogWarning("AssetDeltaBroadcastService: no eventbus connection string; live asset updates disabled.");
            return;
        }

        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(connStr) };
            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);

            // Exclusive, auto-delete, server-named queue: this web instance gets its own copy of events.
            var queue = await _channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true, cancellationToken: stoppingToken);
            foreach (var et in EventTypes)
                await _channel.QueueBindAsync(queue.QueueName, Exchange, et, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try { await HandleAsync(ea, stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "AssetDeltaBroadcastService: failed to handle event"); }
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
            };
            await _channel.BasicConsumeAsync(queue.QueueName, autoAck: false, consumer, stoppingToken);

            logger.LogInformation("AssetDeltaBroadcastService: live asset updates active (queue {Queue}).", queue.QueueName);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "AssetDeltaBroadcastService failed");
        }
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var eventType = ea.BasicProperties.Type ?? ea.RoutingKey;
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.ToArray()));
        if (!doc.RootElement.TryGetProperty("payload", out var p) &&
            !doc.RootElement.TryGetProperty("Payload", out p))
            return;

        string? Get(string camel, string pascal) =>
            p.TryGetProperty(camel, out var v) || p.TryGetProperty(pascal, out v) ? v.GetString() : null;

        var assetId = Guid.TryParse(Get("assetId", "AssetId"), out var aid) ? aid : Guid.Empty;
        var programId = Guid.TryParse(Get("programId", "ProgramId"), out var pid) ? pid : Guid.Empty;
        var assetType = Get("assetType", "AssetType") ?? "";
        var value = Get("value", "Value") ?? "";
        if (assetId == Guid.Empty || string.IsNullOrEmpty(value)) return;

        var action = eventType.Equals("AssetDiscovered", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        var delta = new AssetDelta(action, assetId, assetType, value, null, null, null, programId);
        await notifier.NotifyAssetDeltaAsync(delta, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
