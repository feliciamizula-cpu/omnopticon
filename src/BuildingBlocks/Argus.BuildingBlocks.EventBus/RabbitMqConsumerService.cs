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
    private readonly IPoisonMessageStore _poisonStore;
    private readonly IOptions<ArgusEventBusOptions> _options;
    private readonly ILogger _logger;
    private readonly string _consumerName;
    private readonly int _maxRetries = 3;
    private IConnection? _connection;
    private IChannel? _channel;

    private const string DeadLetterExchange = "argus.events.dlx";

    public RabbitMqConsumerService(
        IServiceScopeFactory scopeFactory,
        IPoisonMessageStore poisonStore,
        IOptions<ArgusEventBusOptions> options,
        ILogger<RabbitMqConsumerService<TDbContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _poisonStore = poisonStore;
        _options = options;
        _logger = logger;
        _consumerName = $"{options.Value.SourceService}_{typeof(TDbContext).Name}";
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

            var eventTypes = new[] {
                "AssetDiscovered", "AssetUpdated", "AssetRelationshipDiscovered",
                "TaskRequested", "TaskLeased", "TaskStarted", "TaskProgressed", "TaskCompleted", "TaskFailed",
                "ProgramCreated", "ScopeCreated", "RateLimitTokenGranted", "RateLimitDelayed",
                "WorkerHeartbeat", "ProgramScopeChanged"
            };

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
            }

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                var retryCount = GetRetryCount(ea.BasicProperties);
                try
                {
                    var body = ea.Body.ToArray();
                    var json = Encoding.UTF8.GetString(body);
                    var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(json);

                    if (envelope is not null)
                    {
                        await ProcessMessageAsync(envelope, stoppingToken);
                    }

                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message {DeliveryTag} (retry {RetryCount})", ea.DeliveryTag, retryCount);
                    if (retryCount >= _maxRetries)
                    {
                        _logger.LogWarning("Message {DeliveryTag} exceeded max retries, moving to DLQ", ea.DeliveryTag);
                        try
                        {
                            var body = ea.Body.ToArray();
                            var json = Encoding.UTF8.GetString(body);
                            var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<JsonElement>>(json);
                            if (envelope is not null)
                            {
                                _poisonStore.RecordPoison(envelope, ex);
                            }
                        }
                        catch { }
                        await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                    }
                    else
                    {
                        await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, stoppingToken);
                    }
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

        var payloadJson = envelope.Payload.GetRawText();
        var deserializedPayload = JsonSerializer.Deserialize(payloadJson, payloadType, JsonOptions);
        if (deserializedPayload is null)
        {
            _logger.LogWarning("Failed to deserialize payload for event {EventType}", envelope.EventType);
            return;
        }

        var invokeMethod = s_dispatchDict[new (handlerType, envelope.EventType)];
        if (invokeMethod is null)
        {
            _logger.LogWarning("No dispatch method for {EventType}", envelope.EventType);
            return;
        }

        var task = (Task?)invokeMethod.Invoke(handler, new[] { envelope, deserializedPayload, cancellationToken });
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
        ["ProgramCreated"] = (typeof(IIntegrationEventConsumer<ProgramCreated>), typeof(ProgramCreated)),
        ["ScopeCreated"] = (typeof(IIntegrationEventConsumer<ScopeCreated>), typeof(ScopeCreated)),
        ["AssetDiscovered"] = (typeof(IIntegrationEventConsumer<AssetDiscovered>), typeof(AssetDiscovered)),
        ["AssetUpdated"] = (typeof(IIntegrationEventConsumer<AssetUpdated>), typeof(AssetUpdated)),
        ["AssetRelationshipDiscovered"] = (typeof(IIntegrationEventConsumer<AssetRelationshipDiscovered>), typeof(AssetRelationshipDiscovered)),
        ["TaskRequested"] = (typeof(IIntegrationEventConsumer<TaskRequested>), typeof(TaskRequested)),
        ["TaskLeased"] = (typeof(IIntegrationEventConsumer<TaskLeased>), typeof(TaskLeased)),
        ["TaskStarted"] = (typeof(IIntegrationEventConsumer<TaskStarted>), typeof(TaskStarted)),
        ["TaskProgressed"] = (typeof(IIntegrationEventConsumer<TaskProgressed>), typeof(TaskProgressed)),
        ["TaskCompleted"] = (typeof(IIntegrationEventConsumer<TaskCompleted>), typeof(TaskCompleted)),
        ["TaskFailed"] = (typeof(IIntegrationEventConsumer<TaskFailed>), typeof(TaskFailed)),
        ["WorkerHeartbeat"] = (typeof(IIntegrationEventConsumer<WorkerHeartbeat>), typeof(WorkerHeartbeat)),
        ["RateLimitTokenGranted"] = (typeof(IIntegrationEventConsumer<RateLimitTokenGranted>), typeof(RateLimitTokenGranted)),
        ["RateLimitDelayed"] = (typeof(IIntegrationEventConsumer<RateLimitDelayed>), typeof(RateLimitDelayed)),
    };

    private static readonly Dictionary<(Type, string), MethodInfo> s_dispatchDict = BuildDispatchDict();

    private static Dictionary<(Type, string), MethodInfo> BuildDispatchDict()
    {
        var dict = new Dictionary<(Type, string), MethodInfo>();
        var iface = typeof(IIntegrationEventConsumer<>);
        var eventTypes = new[] {
            typeof(ProgramCreated), typeof(ScopeCreated), typeof(AssetDiscovered), typeof(AssetUpdated),
            typeof(AssetRelationshipDiscovered), typeof(TaskRequested), typeof(TaskLeased), typeof(TaskStarted),
            typeof(TaskProgressed), typeof(TaskCompleted), typeof(TaskFailed), typeof(WorkerHeartbeat),
            typeof(RateLimitTokenGranted), typeof(RateLimitDelayed)
        };
        foreach (var t in eventTypes)
        {
            var handlerType = iface.MakeGenericType(t);
            var method = handlerType.GetMethod(nameof(IIntegrationEventConsumer<object>.HandleAsync))!;
            dict[(handlerType, t.Name)] = method;
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
        if (properties?.Headers is null) return 0;
        if (properties.Headers.TryGetValue("x-death", out var deathObj) && deathObj is IList<object> deaths)
        {
            var count = 0;
            foreach (var death in deaths)
            {
                if (death is IDictionary<string, object> deathInfo &&
                    deathInfo.TryGetValue("count", out var countObj))
                {
                    count = Convert.ToInt32(countObj);
                }
            }
            return count;
        }
        return 0;
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