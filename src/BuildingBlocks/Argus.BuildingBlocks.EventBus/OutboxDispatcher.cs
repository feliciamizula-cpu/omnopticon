using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.BuildingBlocks.EventBus;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    RealtimeIntegrationEventPublisher realtimePublisher,
    IEnumerable<RabbitMqIntegrationEventPublisher> rabbitMqPublishers,
    IOptions<ArgusEventBusOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(30);
    private readonly string _lockOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly RabbitMqIntegrationEventPublisher? _rabbitMqPublisher = rabbitMqPublishers.SingleOrDefault();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatcher failed unexpectedly.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var messages = await outboxStore.ClaimPendingAsync(
            options.Value.SourceService,
            _lockOwner,
            batchSize: 50,
            LockDuration,
            cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                var envelope = OutboxMessageSerializer.ToJsonEnvelope(message);
                await realtimePublisher.PublishAsync(envelope, cancellationToken);

                if (_rabbitMqPublisher is not null)
                {
                    await _rabbitMqPublisher.PublishAsync(envelope, cancellationToken);
                }

                await outboxStore.MarkProcessedAsync(message.OutboxMessageId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var retryAfter = ComputeRetryAfter(message.AttemptCount);
                await outboxStore.MarkFailedAsync(message.OutboxMessageId, ex.Message, retryAfter, cancellationToken);

                logger.LogWarning(
                    ex,
                    "Outbox message {OutboxMessageId} for {EventType} failed; retrying after {RetryAfter}.",
                    message.OutboxMessageId,
                    message.EventType,
                    retryAfter);
            }
        }
    }

    private static TimeSpan ComputeRetryAfter(int attemptCount)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Clamp(attemptCount, 0, 6)));
        return TimeSpan.FromSeconds(seconds);
    }
}
