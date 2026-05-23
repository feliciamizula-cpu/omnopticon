using System.Threading.Channels;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.Workers;

public sealed class TaskNotificationChannel
{
    private readonly Channel<TaskNotification> _channel;

    public TaskNotificationChannel()
    {
        _channel = Channel.CreateUnbounded<TaskNotification>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask PublishAsync(TaskNotification notification, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(notification, cancellationToken);
    }

    public ChannelReader<TaskNotification> Reader => _channel.Reader;
}

public sealed record TaskNotification(
    ReconTaskDto Task,
    CancellationTokenSource CompletionCts);

public sealed class TaskRequestedHandler : IIntegrationEventConsumer<TaskRequested>
{
    private readonly TaskNotificationChannel _channel;
    private readonly ILogger<TaskRequestedHandler> _logger;

    public string WorkerCapability { get; }

    public TaskRequestedHandler(
        TaskNotificationChannel channel,
        ILogger<TaskRequestedHandler> logger,
        string workerCapability)
    {
        _channel = channel;
        _logger = logger;
        WorkerCapability = workerCapability;
    }

    public async Task HandleAsync(IntegrationEventEnvelope<TaskRequested> envelope, CancellationToken cancellationToken = default)
    {
        var taskDto = new ReconTaskDto(
            TaskId: envelope.Payload.TaskId,
            TaskType: envelope.Payload.TaskType,
            ProgramId: envelope.Payload.ProgramId,
            ScopeId: null,
            InputAssetId: envelope.Payload.InputAssetId,
            InputPayloadJson: null,
            WorkerCapability: envelope.Payload.TaskType,
            RequiredAssetType: null,
            State: ReconTaskState.Requested,
            Attempt: 0,
            MaxAttempts: 3,
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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _channel.PublishAsync(new TaskNotification(taskDto, cts), cancellationToken);
        _logger.LogDebug("Published task {TaskId} to notification channel for capability {Capability}",
            envelope.Payload.TaskId, WorkerCapability);
    }
}
