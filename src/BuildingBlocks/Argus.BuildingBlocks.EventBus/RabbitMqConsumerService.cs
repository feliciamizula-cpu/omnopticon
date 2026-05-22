using Argus.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventBus;

public sealed class RabbitMqConsumerService<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ArgusEventBusOptions> _options;
    private readonly ILogger _logger;
    private readonly string _consumerName;
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<ArgusEventBusOptions> options,
        ILogger<RabbitMqConsumerService<TDbContext>> logger)
    {
        _scopeFactory = scopeFactory;
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

            var eventTypes = new[] {
                "AssetDiscovered", "AssetUpdated", "AssetRelationshipDiscovered",
                "TaskRequested", "TaskLeased", "TaskStarted", "TaskProgressed", "TaskCompleted", "TaskFailed",
                "ProgramCreated", "ScopeCreated", "RateLimitTokenGranted", "RateLimitDelayed",
                "WorkerHeartbeat", "ProgramScopeChanged"
            };

            foreach (var eventType in eventTypes)
            {
                var queueName = $"{_consumerName}_{eventType}";
                await _channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await _channel.QueueBindAsync(queueName, "argus.events", eventType, cancellationToken: stoppingToken);
            }

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
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
                    _logger.LogError(ex, "Error processing message {DeliveryTag}", ea.DeliveryTag);
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
                }
            };

            await _channel.BasicConsumeAsync(queue: $"{_consumerName}_AssetDiscovered", autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            _logger.LogInformation("RabbitMQ consumer {ConsumerName} started", _consumerName);

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

        var handlerType = typeof(IIntegrationEventConsumer<>).MakeGenericType(envelope.Payload.GetType());
        var handler = scope.ServiceProvider.GetService(handlerType);

        if (handler is null)
        {
            _logger.LogWarning("No handler found for event type {EventType}", envelope.EventType);
            return;
        }

        var handleMethod = handlerType.GetMethod(nameof(IIntegrationEventConsumer<object>.HandleAsync));
        var task = (Task?)handleMethod?.Invoke(handler, new object[] { envelope, cancellationToken });

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

    public override void Dispose()
    {
        _channel?.CloseAsync().Wait();
        _connection?.CloseAsync().Wait();
        base.Dispose();
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