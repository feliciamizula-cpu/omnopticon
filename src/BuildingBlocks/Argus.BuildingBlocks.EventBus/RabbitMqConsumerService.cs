using Argus.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventBus;

public sealed class RabbitMqConsumerService<TDbContext> : BackgroundService, IAsyncDisposable
    where TDbContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ArgusEventBusOptions> _options;
    private readonly ILogger _logger;
    private readonly string _consumerName;
    private readonly int _maxRetries = 3;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly IPoisonMessageStore? _poisonStore;

    private const string DeadLetterExchange = "argus.events.dlx";
    private const string RetryExchange = "argus.events.retry";

    public RabbitMqConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<ArgusEventBusOptions> options,
        ILogger<RabbitMqConsumerService<TDbContext>> logger,
        IPoisonMessageStore? poisonStore = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _consumerName = $"{options.Value.SourceService}_{typeof(TDbContext).Name}";
        _poisonStore = poisonStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            var connectionString = _options.Value.RabbitMqConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogWarning("No RabbitMQ connection string configured, consumer not starting");
                return;
            }

            var factory = new ConnectionFactory();
            factory.Uri = new Uri(connectionString);

            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, cancellationToken: stoppingToken);

            await _channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
            await _channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: stoppingToken);

            // Subscribe ONLY to event types this service actually has a registered handler for.
            // Binding queues for every event type (incl. ones with no handler, e.g. rate-limit
            // notifications) fans every such event out to every service, where it is fetched, inbox-
            // checked against the DB, then discarded — wasteful at best, and under load it triggers
            // failure -> retry -> dead-letter-to-main-exchange -> re-fan-out amplification.
            string[] eventTypes;
            using (var probeScope = _scopeFactory.CreateScope())
            {
                eventTypes = EventTypeToTypes
                    .Where(kv => probeScope.ServiceProvider.GetService(kv.Value.handlerType) is not null)
                    .Select(kv => kv.Key)
                    .ToArray();
            }

            if (eventTypes.Length == 0)
            {
                _logger.LogInformation("Consumer {ConsumerName} has no registered event handlers; not binding any queues.", _consumerName);
                return;
            }

            _logger.LogInformation("Consumer {ConsumerName} subscribing to {Count} handled event type(s): [{Types}]",
                _consumerName, eventTypes.Length, string.Join(", ", eventTypes));

            foreach (var eventType in eventTypes)
            {
                var dlqName = $"dlq.{_consumerName}_{eventType}";
                await _channel.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await _channel.QueueBindAsync(dlqName, DeadLetterExchange, $"dead.{eventType}", cancellationToken: stoppingToken);
            }

            foreach (var eventType in eventTypes)
            {
                var queueName = $"{_consumerName}_{eventType}";
                var args = new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = DeadLetterExchange,
                    ["x-dead-letter-routing-key"] = $"dead.{eventType}"
                };
                await _channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, arguments: args, cancellationToken: stoppingToken);
                await _channel.QueueBindAsync(queueName, _options.Value.ExchangeName, eventType, cancellationToken: stoppingToken);

                var retryQueueName = $"retry.{_consumerName}_{eventType}";
                var retryArgs = new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = _options.Value.ExchangeName,
                    ["x-dead-letter-routing-key"] = eventType
                };
                await _channel.QueueDeclareAsync(retryQueueName, durable: true, exclusive: false, autoDelete: false, arguments: retryArgs, cancellationToken: stoppingToken);
                await _channel.QueueBindAsync(retryQueueName, RetryExchange, eventType, cancellationToken: stoppingToken);
            }

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                var retryCount = GetRetryCount(ea.BasicProperties);
                var isRedelivered = ea.Redelivered;

                if (retryCount > _maxRetries)
                {
                    _logger.LogWarning("Message {DeliveryTag} exceeded retry limit (retryCount={RetryCount}), moving to DLQ", ea.DeliveryTag, retryCount);
                    await MoveToDlqAsync(ea, null, retryCount, stoppingToken);
                    return;
                }

                if (isRedelivered)
                {
                    _logger.LogInformation("Processing redelivered message {DeliveryTag} with retryCount={RetryCount}", ea.DeliveryTag, retryCount);
                }

                try
                {
                    var body = ea.Body.ToArray();
                    var json = Encoding.UTF8.GetString(body);
                    var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(json);

                    if (envelope is not null)
                    {
                        if (_poisonStore is not null)
                        {
                            var existing = await _poisonStore.GetMessageAsync(envelope.EventId, stoppingToken);
                            if (existing is not null)
                            {
                                _logger.LogWarning("Message {EventId} is already in poison store, moving to DLQ", envelope.EventId);
                                await MoveToDlqAsync(ea, null, retryCount, stoppingToken);
                                return;
                            }
                        }

                        await ProcessMessageAsync(envelope, stoppingToken);
                    }

                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message {DeliveryTag} (retry {RetryCount})", ea.DeliveryTag, retryCount);
                    await HandleFailedMessageAsync(ea, ex, stoppingToken);
                }
            };

            foreach (var eventType in eventTypes)
            {
                var queueName = $"{_consumerName}_{eventType}";
                await _channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            }
            _logger.LogInformation("RabbitMQ consumer {ConsumerName} started, listening on {QueueCount} queues", _consumerName, eventTypes.Length);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RabbitMQ consumer failed");
        }
    }

    private async Task MoveToDlqAsync(BasicDeliverEventArgs ea, Exception? ex, int attemptCount, CancellationToken cancellationToken)
    {
        try
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(json);
            if (envelope is not null)
            {
                if (_poisonStore is not null) { await _poisonStore.RecordPoisonAsync(envelope, ex ?? new Exception("Poison message detected on redelivery"), attemptCount, cancellationToken); }
            }
        }
        catch (Exception poisonEx)
        {
            _logger.LogError(poisonEx, "Failed to record poison message {DeliveryTag}", ea.DeliveryTag);
        }
        await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken);
    }

    private async Task HandleFailedMessageAsync(BasicDeliverEventArgs ea, Exception ex, CancellationToken cancellationToken)
    {
        var retryCount = GetRetryCount(ea.BasicProperties);
        if (retryCount >= _maxRetries)
        {
            _logger.LogWarning("Message {DeliveryTag} exceeded max retries, moving to DLQ", ea.DeliveryTag);
            await MoveToDlqAsync(ea, ex, retryCount, cancellationToken);
        }
        else
        {
            var delayMs = GetRetryDelayMs(retryCount + 1);
            _logger.LogInformation("Scheduling message {DeliveryTag} for retry #{RetryCount} after {DelayMs}ms", ea.DeliveryTag, retryCount + 1, delayMs);
            await ScheduleRetryAsync(ea, delayMs, cancellationToken);
            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
        }
    }

    private async Task ScheduleRetryAsync(BasicDeliverEventArgs ea, int delayMs, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = ea.BasicProperties.MessageId ?? Guid.NewGuid().ToString(),
            CorrelationId = ea.BasicProperties.CorrelationId ?? "",
            Type = ea.BasicProperties.Type ?? "",
            Expiration = delayMs.ToString()
        };

        if (ea.BasicProperties.Headers is null)
        {
            properties.Headers = new Dictionary<string, object?>();
        }
        else
        {
            properties.Headers = new Dictionary<string, object?>(ea.BasicProperties.Headers);
        }

        var retryCount = GetRetryCount(ea.BasicProperties);
        properties.Headers["x-retry-count"] = retryCount + 1;

        var body = ea.Body.ToArray();
        var eventType = ea.BasicProperties.Type ?? "unknown";

        await _channel!.BasicPublishAsync(RetryExchange, eventType, false, properties, body, cancellationToken);
    }

    private static int GetRetryDelayMs(int retryAttempt)
    {
        var delaySeconds = Math.Min(60, Math.Pow(2, Math.Clamp(retryAttempt - 1, 0, 6)));
        return (int)(delaySeconds * 1000);
    }

    private async Task ProcessMessageAsync(IntegrationEventEnvelope<JsonElement> envelope, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var alreadyProcessed = await dbContext.Set<InboxMessageRecord>()
            .AnyAsync(x => x.EventId == envelope.EventId && x.ConsumerName == _consumerName, cancellationToken);

        if (alreadyProcessed)
        {
            _logger.LogDebug("Event {EventId} already processed by {ConsumerName}", envelope.EventId, _consumerName);
            return;
        }

        var resolvedTypes = ResolveHandlerType(envelope.EventType);
        if (resolvedTypes is null)
        {
            _logger.LogWarning("No handler type found for event type {EventType}", envelope.EventType);
            return;
        }
        var handlerType = resolvedTypes.Value.handlerType;
        var payloadType = resolvedTypes.Value.payloadType;

        var handler = scope.ServiceProvider.GetService(handlerType);
        if (handler is null)
        {
            _logger.LogWarning("No handler found for event type {EventType}", envelope.EventType);
            return;
        }

        var typedEnvelopeType = typeof(IntegrationEventEnvelope<>).MakeGenericType(payloadType);
        var typedEnvelopeJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var typedEnvelope = JsonSerializer.Deserialize(typedEnvelopeJson, typedEnvelopeType, JsonOptions);
        if (typedEnvelope is null)
        {
            _logger.LogWarning("Failed to deserialize typed envelope for event {EventType}", envelope.EventType);
            return;
        }

        if (!s_dispatchDict.TryGetValue(new(handlerType, envelope.EventType), out var invokeMethod))
        {
            _logger.LogWarning("No dispatch method for {EventType}", envelope.EventType);
            return;
        }

        var task = (Task?)invokeMethod.Invoke(handler, [typedEnvelope, cancellationToken]);
        if (task is not null)
        {
            await task;
        }

        dbContext.Set<InboxMessageRecord>().Add(new InboxMessageRecord
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            ConsumerName = _consumerName,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
            Error = null,
            AttemptCount = 1
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Processed event {EventId} of type {EventType}", envelope.EventId, envelope.EventType);
    }

    private static readonly Dictionary<string, (Type handlerType, Type payloadType)> EventTypeToTypes = new()
    {
        [nameof(ProgramCreated)] = (typeof(IIntegrationEventConsumer<ProgramCreated>), typeof(ProgramCreated)),
        [nameof(ScopeCreated)] = (typeof(IIntegrationEventConsumer<ScopeCreated>), typeof(ScopeCreated)),
        [nameof(ProgramScopeChanged)] = (typeof(IIntegrationEventConsumer<ProgramScopeChanged>), typeof(ProgramScopeChanged)),

        [nameof(AssetDiscovered)] = (typeof(IIntegrationEventConsumer<AssetDiscovered>), typeof(AssetDiscovered)),
        [nameof(AssetConfirmed)] = (typeof(IIntegrationEventConsumer<AssetConfirmed>), typeof(AssetConfirmed)),
        [nameof(AssetUpdated)] = (typeof(IIntegrationEventConsumer<AssetUpdated>), typeof(AssetUpdated)),
        [nameof(AssetPropertyChanged)] = (typeof(IIntegrationEventConsumer<AssetPropertyChanged>), typeof(AssetPropertyChanged)),
        [nameof(AssetRelationshipDiscovered)] = (typeof(IIntegrationEventConsumer<AssetRelationshipDiscovered>), typeof(AssetRelationshipDiscovered)),
        [nameof(FindingCandidateCreated)] = (typeof(IIntegrationEventConsumer<FindingCandidateCreated>), typeof(FindingCandidateCreated)),

        [nameof(TaskRequested)] = (typeof(IIntegrationEventConsumer<TaskRequested>), typeof(TaskRequested)),
        [nameof(TaskLeased)] = (typeof(IIntegrationEventConsumer<TaskLeased>), typeof(TaskLeased)),
        [nameof(TaskStarted)] = (typeof(IIntegrationEventConsumer<TaskStarted>), typeof(TaskStarted)),
        [nameof(TaskProgressed)] = (typeof(IIntegrationEventConsumer<TaskProgressed>), typeof(TaskProgressed)),
        [nameof(TaskCompleted)] = (typeof(IIntegrationEventConsumer<TaskCompleted>), typeof(TaskCompleted)),
        [nameof(TaskFailed)] = (typeof(IIntegrationEventConsumer<TaskFailed>), typeof(TaskFailed)),

        [nameof(WorkerHeartbeat)] = (typeof(IIntegrationEventConsumer<WorkerHeartbeat>), typeof(WorkerHeartbeat)),
        [nameof(RateLimitTokenGranted)] = (typeof(IIntegrationEventConsumer<RateLimitTokenGranted>), typeof(RateLimitTokenGranted)),
        [nameof(RateLimitDelayed)] = (typeof(IIntegrationEventConsumer<RateLimitDelayed>), typeof(RateLimitDelayed)),
        [nameof(RateLimitBackpressureSignaled)] = (typeof(IIntegrationEventConsumer<RateLimitBackpressureSignaled>), typeof(RateLimitBackpressureSignaled)),

        [nameof(ProxyAdded)] = (typeof(IIntegrationEventConsumer<ProxyAdded>), typeof(ProxyAdded)),
        [nameof(ProxyRemoved)] = (typeof(IIntegrationEventConsumer<ProxyRemoved>), typeof(ProxyRemoved)),
        [nameof(ProxyStatusChanged)] = (typeof(IIntegrationEventConsumer<ProxyStatusChanged>), typeof(ProxyStatusChanged)),
        [nameof(ProxyRateLimitExceeded)] = (typeof(IIntegrationEventConsumer<ProxyRateLimitExceeded>), typeof(ProxyRateLimitExceeded)),

        [nameof(ArtifactCreated)] = (typeof(IIntegrationEventConsumer<ArtifactCreated>), typeof(ArtifactCreated)),
        [nameof(EvidenceAdded)] = (typeof(IIntegrationEventConsumer<EvidenceAdded>), typeof(EvidenceAdded)),
        [nameof(FindingCreated)] = (typeof(IIntegrationEventConsumer<FindingCreated>), typeof(FindingCreated)),
        [nameof(FindingUpdated)] = (typeof(IIntegrationEventConsumer<FindingUpdated>), typeof(FindingUpdated)),
        [nameof(FindingTriaged)] = (typeof(IIntegrationEventConsumer<FindingTriaged>), typeof(FindingTriaged)),
    };

    private static readonly Dictionary<(Type, string), MethodInfo> s_dispatchDict = BuildDispatchDict();

    private static Dictionary<(Type, string), MethodInfo> BuildDispatchDict()
    {
        var dict = new Dictionary<(Type, string), MethodInfo>();
        var iface = typeof(IIntegrationEventConsumer<>);
        foreach (var (eventTypeName, (_, payloadType)) in EventTypeToTypes)
        {
            var handlerType = iface.MakeGenericType(payloadType);
            var method = handlerType.GetMethod(nameof(IIntegrationEventConsumer<object>.HandleAsync))!;
            dict[(handlerType, eventTypeName)] = method;
        }
        return dict;
    }

    private static (Type handlerType, Type payloadType)? ResolveHandlerType(string eventType)
    {
        return EventTypeToTypes.TryGetValue(eventType, out var types) ? types : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int GetRetryCount(IReadOnlyBasicProperties? properties)
    {
        if (properties?.Headers is null)
        {
            return 0;
        }

        if (!properties.Headers.TryGetValue("x-retry-count", out var countObj) || countObj is null)
        {
            return 0;
        }

        return countObj switch
        {
            int value => value,
            long value when value <= int.MaxValue && value >= int.MinValue => (int)value,
            long value when value > int.MaxValue => int.MaxValue,
            long value when value < int.MinValue => 0,
            short value => value,
            byte value => value,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var value) => value,
            ReadOnlyMemory<byte> bytes when int.TryParse(Encoding.UTF8.GetString(bytes.Span), out var value) => value,
            string text when int.TryParse(text, out var value) => value,
            _ => 0
        };
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_channel is not null) await _channel.CloseAsync();
        if (_connection is not null) await _connection.CloseAsync();
    }
}

internal sealed class InboxMessageRecord
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ConsumerName { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
}

public static class InboxModelBuilderExtensions
{
    public static void ConfigureInbox(this ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<InboxMessageRecord>();
        inbox.ToTable("inbox_messages");
        inbox.HasKey(x => new { x.EventId, x.ConsumerName });
        inbox.HasIndex(x => new { x.EventType, x.ProcessedAt });
        inbox.Property(x => x.EventType).HasMaxLength(256);
        inbox.Property(x => x.ConsumerName).HasMaxLength(256);
    }
}