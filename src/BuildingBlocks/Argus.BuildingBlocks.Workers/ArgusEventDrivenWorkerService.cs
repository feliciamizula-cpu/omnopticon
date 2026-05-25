using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Programs;
using Argus.Contracts.RateLimits;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.Workers;

public sealed class ArgusEventDrivenWorkerService : BackgroundService
{
    private readonly IReconWorker _worker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArgusWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly ArgusMetrics _metrics;
    private readonly ChannelReader<TaskNotification> _channelReader;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<Guid, string?> _taskCheckpoints = new();

    public ArgusEventDrivenWorkerService(
        IReconWorker worker,
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Options.IOptions<ArgusWorkerOptions> options,
        ILogger<ArgusEventDrivenWorkerService> logger,
        ArgusMetrics metrics,
        ChannelReader<TaskNotification> channelReader)
    {
        _worker = worker;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _channelReader = channelReader;
        _concurrencyLimiter = new SemaphoreSlim(worker.Capability.MaxConcurrency, worker.Capability.MaxConcurrency);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Event-driven worker {WorkerType} started with max concurrency {MaxConcurrency}",
            _worker.Capability.WorkerType, _worker.Capability.MaxConcurrency);

        await RegisterWorkerAsync(stoppingToken);

        try
        {
            await foreach (var notification in _channelReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _concurrencyLimiter.WaitAsync(stoppingToken);
                    _ = ProcessTaskWithReleaseAsync(notification, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Event-driven worker {WorkerType} ({WorkerId}) stopping, initiating graceful drain (timeout: {DrainTimeout})",
            _worker.Capability.WorkerType, _options.WorkerId, _options.DrainTimeout);

        var baseStopTask = base.StopAsync(cancellationToken);

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(_options.DrainTimeout);

        var acquiredDrainSlots = 0;

        try
        {
            for (var i = 0; i < _worker.Capability.MaxConcurrency; i++)
            {
                await _concurrencyLimiter.WaitAsync(drainCts.Token);
                acquiredDrainSlots++;
            }

            _logger.LogInformation("Event-driven worker {WorkerType} ({WorkerId}) drain complete, all in-flight tasks finished",
                _worker.Capability.WorkerType, _options.WorkerId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Event-driven worker {WorkerType} ({WorkerId}) drain timeout expired ({DrainTimeout}), forcing shutdown of remaining tasks",
                _worker.Capability.WorkerType, _options.WorkerId, _options.DrainTimeout);

            if (_options.SaveCheckpointOnShutdown)
            {
                await FailRemainingTasksWithCheckpointAsync(cancellationToken);
            }
        }
        finally
        {
            if (acquiredDrainSlots > 0)
            {
                _concurrencyLimiter.Release(acquiredDrainSlots);
            }
        }

        try
        {
            await baseStopTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FailRemainingTasksWithCheckpointAsync(CancellationToken cancellationToken)
    {
        foreach (var (taskId, checkpointJson) in _taskCheckpoints.ToArray())
        {
            try
            {
                _logger.LogInformation("Failing task {TaskId} with checkpoint due to worker shutdown", taskId);
                var shutdownEx = new OperationCanceledException("Worker shutting down");
                await FailTaskAsync(taskId, shutdownEx, cancellationToken, checkpointJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record shutdown checkpoint for task {TaskId}", taskId);
            }
        }
    }

    private async Task ProcessTaskWithReleaseAsync(TaskNotification notification, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessTaskAsync(notification, stoppingToken);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<ScopeSnapshot?> FetchAndValidateSnapshotAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.SnapshotSecretKey))
        {
            _logger.LogWarning("SnapshotSecretKey not configured, cannot fetch signed snapshot");
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.ScopeServiceBaseAddress;
        using var response = await client.GetAsync($"/programs/{programId:N}/snapshot", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("No snapshot found for program {ProgramId}", programId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var snapshotJson = await response.Content.ReadAsStringAsync(cancellationToken);

        var snapshot = SnapshotSigner.DeserializeSnapshot(snapshotJson);
        if (snapshot is null)
        {
            _logger.LogError("Failed to deserialize scope snapshot for program {ProgramId}", programId);
            return null;
        }

        if (!SnapshotSigner.VerifySignature(snapshot, _options.SnapshotSecretKey))
        {
            _logger.LogError("Scope snapshot signature verification failed for program {ProgramId}", programId);
            return null;
        }

        _logger.LogDebug("Scope snapshot verified for program {ProgramId}", programId);
        return snapshot;
    }

    private async Task ProcessTaskAsync(TaskNotification notification, CancellationToken cancellationToken)
    {
        var task = notification.Task;
        var stopwatch = Stopwatch.StartNew();
        _taskCheckpoints[task.TaskId] = task.CheckpointJson;

        _logger.LogInformation("Event-driven worker {WorkerId} processing task {TaskId} ({TaskType})",
            _options.WorkerId, task.TaskId, task.TaskType);

        if (!string.IsNullOrEmpty(_options.SnapshotSecretKey))
        {
            var snapshot = await FetchAndValidateSnapshotAsync(task.ProgramId, cancellationToken);
            if (snapshot is null)
            {
                _logger.LogError("Task {TaskId} failed to fetch or validate scope snapshot", task.TaskId);
                await FailTaskAsync(task.TaskId, new InvalidOperationException("Scope snapshot fetch/validation failed"), cancellationToken);
                return;
            }
            _logger.LogDebug("Task {TaskId} fetched and validated scope snapshot {SnapshotId}", task.TaskId, snapshot.SnapshotId);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = StartHeartbeatTimerAsync(heartbeatCts.Token);

        var context = new WorkerExecutionContext(
            _options.WorkerId,
            (percent, message, checkpoint) => ReportProgressAsync(task.TaskId, percent, message, checkpoint, heartbeatCts.Token),
            request => RequestRateLimitTokenAsync(request, heartbeatCts.Token),
            signal => SignalBackpressureAsync(signal, heartbeatCts.Token));

        try
        {
            await StartTaskAsync(task.TaskId, heartbeatCts.Token);

            var result = await _worker.ProcessAsync(task, context, heartbeatCts.Token);
            stopwatch.Stop();

            if (result.RetryAfter.HasValue && result.RetryAfter.Value > TimeSpan.Zero)
            {
                _logger.LogInformation("Task {TaskId} received rate limit response, waiting {RetryAfter}s",
                    task.TaskId, result.RetryAfter.Value.TotalSeconds);
                await Task.Delay(result.RetryAfter.Value, heartbeatCts.Token);
            }

            foreach (var asset in result.ProducedAssets)
            {
                if (await IsProducedAssetInScopeAsync(task, asset, heartbeatCts.Token))
                {
                    var createdAsset = await PublishAssetAsync(task, asset, heartbeatCts.Token);
                    if (createdAsset is not null)
                    {
                        await CreateRelationshipAsync(task, createdAsset, asset.AssetType, heartbeatCts.Token);
                        _metrics.RecordAssetProduced(task.ProgramId, _worker.Capability.WorkerType, asset.AssetType);
                    }
                }
            }

            _metrics.RecordTaskProcessed(task.ProgramId, _worker.Capability.WorkerType, result.PartiallySucceeded);
            _metrics.RecordTaskDuration(task.ProgramId, _worker.Capability.WorkerType, stopwatch.Elapsed.TotalMilliseconds);

            await CompleteTaskAsync(task.TaskId, result, heartbeatCts.Token);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordTaskFailed(task.ProgramId, _worker.Capability.WorkerType, ex.GetType().Name);
            _metrics.RecordTaskDuration(task.ProgramId, _worker.Capability.WorkerType, stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogError(ex, "Task {TaskId} failed in event-driven worker {WorkerId}", task.TaskId, _options.WorkerId);
            await FailTaskAsync(task.TaskId, ex, heartbeatCts.Token);
        }
        finally
        {
            _taskCheckpoints.TryRemove(task.TaskId, out _);
            heartbeatCts.Cancel();
            await heartbeatTask;
            notification.CompletionCts.Cancel();
        }
    }

    private async Task RegisterWorkerAsync(CancellationToken cancellationToken)
    {
        var request = new WorkerRegistrationRequest(_options.WorkerId, _worker.Capability, typeof(IReconWorker).Assembly.GetName().Version?.ToString());
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.RealtimeServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/workers/register", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task StartHeartbeatTimerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);
                await HeartbeatAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Heartbeat failed for event-driven worker {WorkerType} ({WorkerId})", _worker.Capability.WorkerType, _options.WorkerId);
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var request = new WorkerHeartbeatRequest(
            _options.WorkerId,
            _worker.Capability.WorkerType,
            _worker.Capability.MaxConcurrency - _concurrencyLimiter.CurrentCount,
            _worker.Capability.MaxConcurrency,
            DateTimeOffset.UtcNow);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.RealtimeServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/workers/heartbeat", request, _jsonOptions, cancellationToken);
    }

    private async Task StartTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.TaskServiceBaseAddress;
        using var response = await client.PostAsync($"/tasks/{taskId}/start?workerId={Uri.EscapeDataString(_options.WorkerId)}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ReportProgressAsync(Guid taskId, int percent, string message, string? checkpointJson, CancellationToken cancellationToken)
    {
        if (checkpointJson is not null)
        {
            _taskCheckpoints[taskId] = checkpointJson;
        }

        var request = new UpdateReconTaskProgressRequest(percent, message, checkpointJson);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.TaskServiceBaseAddress;

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/progress", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> RequestRateLimitTokenAsync(RateLimitRequest request, CancellationToken cancellationToken)
    {
        var payload = new RateLimitCheckRequest(
            request.ProgramId,
            request.ScopeId,
            request.Host,
            request.RegisteredDomain,
            request.Ip,
            request.WorkerType,
            request.ProxyId,
            request.PermitCount);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.RateLimitServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/rate-limits/check", payload, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var decision = await response.Content.ReadFromJsonAsync<RateLimitDecision>(_jsonOptions, cancellationToken);
        return decision?.IsAllowed == true;
    }

    private async Task SignalBackpressureAsync(RateLimitBackpressureSignal signal, CancellationToken cancellationToken)
    {
        var payload = new RateLimitBackpressureRequest(signal.Host, signal.BucketKey, signal.RetryAfter, signal.ObservedStatusCode);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.RateLimitServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/rate-limits/backpressure", payload, _jsonOptions, cancellationToken);
    }

    private async Task<AssetDto?> PublishAssetAsync(ReconTaskDto task, WorkerProducedAsset asset, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AssetType>(asset.AssetType, ignoreCase: true, out var assetType))
        {
            _logger.LogWarning("Worker produced unknown asset type {AssetType}", asset.AssetType);
            return null;
        }

        var request = new CreateAssetRequest(
            task.ProgramId,
            task.ScopeId,
            assetType,
            asset.Value,
            asset.Subtype,
            asset.Confidence,
            task.TaskId.ToString(),
            asset.Metadata,
            asset.Tags);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.AssetServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/assets", request, _jsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to publish asset {AssetType} {Value}: {StatusCode}", asset.AssetType, asset.Value, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AssetDto>(_jsonOptions, cancellationToken);
    }

    private async Task CreateRelationshipAsync(ReconTaskDto task, AssetDto createdAsset, string assetType, CancellationToken cancellationToken)
    {
        if (!task.InputAssetId.HasValue) return;

        var edgeType = DetermineEdgeType(assetType);
        var request = new CreateAssetRelationshipRequest(
            task.InputAssetId.Value,
            createdAsset.AssetId,
            edgeType,
            task.TaskId.ToString());

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.AssetServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/assets/relationships", request, _jsonOptions, cancellationToken);
    }

    private static string DetermineEdgeType(string assetType) =>
        assetType.ToLowerInvariant() switch
        {
            "subdomain" => "resolves_to",
            "ip" => "resolves_to",
            "url" => "discovered_at",
            "htmlpage" => "contains",
            "javascriptfile" => "loads",
            "apiendpoint" => "exposes",
            "technology" => "uses",
            "dnsrecord" => "recorded_as",
            _ => "produces"
        };

    private async Task<bool> IsProducedAssetInScopeAsync(ReconTaskDto task, WorkerProducedAsset asset, CancellationToken cancellationToken)
    {
        if (!_options.ScopeValidationRequired) return true;

        var target = ExtractScopeTarget(asset);
        if (string.IsNullOrWhiteSpace(target)) return false;

        var request = new ScopeValidationRequest(task.ProgramId, target, asset.AssetType);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.ScopeServiceBaseAddress;

        using var response = await client.PostAsJsonAsync("/scope-validation/check", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ScopeValidationResult>(_jsonOptions, cancellationToken);
        return result?.IsAllowed == true;
    }

    private static string? ExtractScopeTarget(WorkerProducedAsset asset)
    {
        if (string.Equals(asset.AssetType, "Observation", StringComparison.OrdinalIgnoreCase)
            && asset.Metadata?.TryGetValue("url", out var observationUrl) == true)
        {
            return Uri.TryCreate(observationUrl.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], UriKind.Absolute, out var uri)
                ? uri.Host
                : observationUrl;
        }

        if (string.Equals(asset.AssetType, "Url", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, "ApiEndpoint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, "JavaScriptFile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, "HtmlPage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, "JsonDocument", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, "HttpResponse", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(asset.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], UriKind.Absolute, out var uri)
                ? uri.Host : asset.Value;
        }
        if (string.Equals(asset.AssetType, "DnsRecord", StringComparison.OrdinalIgnoreCase)
            && asset.Metadata?.TryGetValue("host", out var host) == true)
        {
            return host;
        }
        return asset.Value;
    }

    private async Task CompleteTaskAsync(Guid taskId, WorkerProcessResult result, CancellationToken cancellationToken)
    {
        var request = new CompleteReconTaskRequest(result.PartiallySucceeded, result.OutputSummaryJson);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.TaskServiceBaseAddress;

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/complete", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task FailTaskAsync(Guid taskId, Exception ex, CancellationToken cancellationToken, string? checkpointJson = null)
    {
        var checkpoint = checkpointJson ?? (_taskCheckpoints.TryGetValue(taskId, out var saved) ? saved : null);
        var request = new FailReconTaskRequest(ex.GetType().Name, ex.Message, Retryable: true, CheckpointJson: checkpoint);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _options.TaskServiceBaseAddress;

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/fail", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
