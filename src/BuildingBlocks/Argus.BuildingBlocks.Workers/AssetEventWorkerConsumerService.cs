using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Argus.BuildingBlocks.Workers;

/// <summary>
/// Drives an event-driven worker. Subscribes directly to asset lifecycle events on the shared
/// RabbitMQ topic exchange and forwards matching assets to the worker's processing channel.
/// No task-service involvement: workers react to events and produce more data.
///
/// Routing:
///   - AssetDiscovered / AssetConfirmed -> processed when the asset type is one this worker subscribes to.
///   - WorkerProcessRequested           -> processed only when explicitly targeted at this worker type
///                                          (the manual "Spider this asset" re-trigger).
///
/// Loop safety: asset-service only emits AssetDiscovered for newly-created assets (dupes emit
/// AssetUpdated, which we ignore), so each new in-scope asset is processed exactly once.
/// </summary>
internal sealed class AssetEventWorkerConsumerService : BackgroundService, IAsyncDisposable
{
    private readonly TaskNotificationChannel _channel;
    private readonly WorkerCapabilityDescriptor _capability;
    private readonly ArgusWorkerOptions _options;
    private readonly ILogger<AssetEventWorkerConsumerService> _logger;
    private readonly string _exchangeName;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private IConnection? _connection;
    private IChannel? _rabbitChannel;

    // Per-asset idempotency for AUTOMATIC events: a worker processes a given asset at most once,
    // so duplicate/replayed asset events can never drive an infinite reprocessing loop. Manual
    // WorkerProcessRequested re-triggers bypass this intentionally. Bounded to cap memory.
    private const int MaxProcessedTracked = 50_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _processedAssets = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _processedOrder = new();

    private static readonly string[] SubscribedEventTypes = ["AssetDiscovered", "AssetConfirmed", "WorkerProcessRequested"];

    public AssetEventWorkerConsumerService(
        TaskNotificationChannel channel,
        IReconWorker worker,
        IOptions<ArgusWorkerOptions> options,
        ILogger<AssetEventWorkerConsumerService> logger)
    {
        _channel = channel;
        _capability = worker.Capability;
        _options = options.Value;
        _logger = logger;
        _exchangeName = string.IsNullOrWhiteSpace(_options.EventExchangeName)
            ? "argus.integration.events"
            : _options.EventExchangeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connStr = _options.EventBusConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogWarning("Worker {WorkerType}: no eventbus connection string, asset event consumer not starting", _capability.WorkerType);
            return;
        }

        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(connStr) };
            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _rabbitChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await _rabbitChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 20, global: false, cancellationToken: stoppingToken);

            await _rabbitChannel.ExchangeDeclareAsync(_exchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);

            // One durable queue per worker-type + event-type. Bound by routing key == event type.
            foreach (var eventType in SubscribedEventTypes)
            {
                var queueName = $"worker.{_capability.WorkerType}.{eventType}";
                await _rabbitChannel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await _rabbitChannel.QueueBindAsync(queueName, _exchangeName, eventType, cancellationToken: stoppingToken);
            }

            var consumer = new AsyncEventingBasicConsumer(_rabbitChannel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    await HandleMessageAsync(ea, stoppingToken);
                    await _rabbitChannel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker {WorkerType}: error handling event, requeuing", _capability.WorkerType);
                    await _rabbitChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                }
            };

            foreach (var eventType in SubscribedEventTypes)
            {
                var queueName = $"worker.{_capability.WorkerType}.{eventType}";
                await _rabbitChannel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            }

            _logger.LogInformation("Worker {WorkerType}: event consumer started on exchange {Exchange}; subscribed asset types: [{Types}]",
                _capability.WorkerType, _exchangeName, string.Join(", ", _capability.SubscribedAssetTypes));

            // Loop-risk smell: a worker that consumes an asset type it also produces can self-cascade.
            // Per-asset idempotency keeps it bounded, but surface the overlap so it's reviewed per worker
            // (intended for crawlers like the spider; usually a bug elsewhere, e.g. probe Url->Url).
            var selfOverlap = _capability.SubscribedAssetTypes
                .Intersect(_capability.ProducedAssetTypes, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (selfOverlap.Length > 0)
            {
                _logger.LogWarning("Worker {WorkerType}: subscribes to asset type(s) it also produces [{Overlap}]. " +
                    "This can self-cascade; per-asset idempotency bounds it, but confirm this is intended (e.g. crawl).",
                    _capability.WorkerType, string.Join(", ", selfOverlap));
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerType}: asset event consumer failed", _capability.WorkerType);
        }
    }

    private async ValueTask HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetString(ea.Body.ToArray());
        var eventType = ea.BasicProperties.Type ?? ea.RoutingKey;

        // Deserialize the envelope, then the payload, with case-insensitive (web) options so the
        // PascalCase-on-the-wire payload binds correctly regardless of casing.
        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(json, _jsonOptions);
        if (envelope is null) return;

        Guid assetId, programId;
        string assetType, value;
        string? targetWorkerType = null;

        if (string.Equals(eventType, "WorkerProcessRequested", StringComparison.OrdinalIgnoreCase))
        {
            var p = envelope.Payload.Deserialize<WorkerProcessRequested>(_jsonOptions);
            if (p is null) return;
            (assetId, programId, assetType, value, targetWorkerType) = (p.AssetId, p.ProgramId, p.AssetType, p.Value, p.TargetWorkerType);
        }
        else
        {
            // AssetDiscovered and AssetConfirmed share the leading shape (AssetId, ProgramId, AssetType, Value).
            var p = envelope.Payload.Deserialize<AssetDiscovered>(_jsonOptions);
            if (p is null) return;
            (assetId, programId, assetType, value) = (p.AssetId, p.ProgramId, p.AssetType, p.Value);
        }

        if (string.IsNullOrEmpty(assetType) || string.IsNullOrEmpty(value))
            return;

        // Routing decision.
        if (targetWorkerType is not null)
        {
            // Targeted manual re-trigger: only the addressed worker type acts; bypasses idempotency.
            if (!string.Equals(targetWorkerType, _capability.WorkerType, StringComparison.OrdinalIgnoreCase))
                return;
        }
        else
        {
            // Automatic pipeline: act only on asset types this worker subscribes to...
            if (!_capability.SubscribedAssetTypes.Contains(assetType, StringComparer.OrdinalIgnoreCase))
                return;

            // ...and only once per asset, so duplicate/replayed events can't loop.
            if (!_processedAssets.TryAdd(assetId, 0))
                return;

            _processedOrder.Enqueue(assetId);
            while (_processedOrder.Count > MaxProcessedTracked && _processedOrder.TryDequeue(out var evicted))
                _processedAssets.TryRemove(evicted, out _);
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            url = value,
            value,
            assetId,
            assetType,
            programId
        });

        var task = new ReconTaskDto(
            TaskId: Guid.NewGuid(),
            TaskType: _capability.WorkerType,
            ProgramId: programId,
            ScopeId: null,
            InputAssetId: assetId,
            InputPayloadJson: payloadJson,
            WorkerCapability: _capability.WorkerType,
            RequiredAssetType: assetType,
            State: ReconTaskState.Requested,
            Attempt: 0,
            MaxAttempts: 1,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            StartedAt: null,
            CompletedAt: null,
            ProgressPercent: 0,
            ProgressMessage: null,
            CheckpointJson: null,
            OutputSummaryJson: null,
            ErrorCode: null,
            ErrorMessage: null,
            DedupeHash: null,
            ScopeSnapshotJson: null,
            Priority: WorkerPriority.Normal);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _channel.PublishAsync(new TaskNotification(task, cts), ct);

        _logger.LogInformation("Worker {WorkerType}: accepted {EventType} for {AssetType} asset {AssetId} ({Value})",
            _capability.WorkerType, eventType, assetType, assetId, value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_rabbitChannel is not null) await _rabbitChannel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
