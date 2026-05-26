using Argus.Contracts.Artifacts;
using Argus.Contracts.Assets;
using Argus.Contracts.Programs;
using Argus.Contracts.RateLimits;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Argus.BuildingBlocks.Workers;

public sealed class ArgusWorkerBackgroundService(
    IReconWorker worker,
    IHttpClientFactory httpClientFactory,
    IOptions<ArgusWorkerOptions> options,
    ILogger<ArgusWorkerBackgroundService> logger,
    ArgusMetrics metrics) : BackgroundService
{
    private readonly ArgusWorkerOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _concurrencyLimiter = new(worker.Capability.MaxConcurrency, worker.Capability.MaxConcurrency);
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly ConcurrentDictionary<Guid, string?> _taskCheckpoints = new();
    private int _runningTaskCount;
    private HttpClient? _artifactStore;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterWorkerAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _concurrencyLimiter.WaitAsync(stoppingToken);
                var task = RunTaskWithReleaseAsync(stoppingToken);
                var taskKey = Guid.NewGuid();
                _runningTasks.TryAdd(taskKey, task);
                _ = task.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        logger.LogError(t.Exception, "Worker task failed for {WorkerType}", worker.Capability.WorkerType);
                    }
                    _runningTasks.TryRemove(taskKey, out _);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop failed for {WorkerType}", worker.Capability.WorkerType);
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }

        await Task.WhenAll(_runningTasks.Values);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Worker {WorkerType} ({WorkerId}) stopping, initiating graceful drain (timeout: {DrainTimeout})",
            worker.Capability.WorkerType, _options.WorkerId, _options.DrainTimeout);

        var baseStopTask = base.StopAsync(cancellationToken);

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(_options.DrainTimeout);

        var acquiredDrainSlots = 0;
        try
        {
            for (var i = 0; i < worker.Capability.MaxConcurrency; i++)
            {
                await _concurrencyLimiter.WaitAsync(drainCts.Token);
                acquiredDrainSlots++;
            }

            logger.LogInformation("Worker {WorkerType} ({WorkerId}) drain complete, all in-flight tasks finished",
                worker.Capability.WorkerType, _options.WorkerId);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Worker {WorkerType} ({WorkerId}) drain timeout expired ({DrainTimeout}), forcing shutdown of remaining tasks",
                worker.Capability.WorkerType, _options.WorkerId, _options.DrainTimeout);

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
                logger.LogInformation("Failing task {TaskId} with checkpoint due to worker shutdown", taskId);
                var shutdownEx = new OperationCanceledException("Worker shutting down");
                await FailTaskAsync(taskId, shutdownEx, cancellationToken, checkpointJson);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record shutdown checkpoint for task {TaskId}", taskId);
            }
        }
    }

    private async Task RunTaskWithReleaseAsync(CancellationToken stoppingToken)
    {
        var taskWasStarted = false;

        try
        {
            await HeartbeatAsync(Volatile.Read(ref _runningTaskCount), stoppingToken);
            var task = await LeaseTaskAsync(stoppingToken);

            if (task is null)
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
                return;
            }

            Interlocked.Increment(ref _runningTaskCount);
            taskWasStarted = true;

            await RunTaskAsync(task, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker {WorkerType} ({WorkerId}) failed before a task could be completed", worker.Capability.WorkerType, _options.WorkerId);
            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            if (taskWasStarted)
            {
                var runningTasks = Math.Max(0, Interlocked.Decrement(ref _runningTaskCount));

                try
                {
                    await HeartbeatAsync(runningTasks, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
            }

            _concurrencyLimiter.Release();
        }
    }

    private async Task RunTaskAsync(ReconTaskDto task, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _taskCheckpoints[task.TaskId] = task.CheckpointJson;
        logger.LogInformation("Worker {WorkerId} leased task {TaskId} ({TaskType})", _options.WorkerId, task.TaskId, task.TaskType);

        if (!string.IsNullOrEmpty(_options.SnapshotSecretKey))
        {
            var snapshot = await FetchAndValidateSnapshotAsync(task.ProgramId, cancellationToken);
            if (snapshot is null)
            {
                logger.LogError("Task {TaskId} failed to fetch or validate scope snapshot", task.TaskId);
                await FailTaskAsync(task.TaskId, new InvalidOperationException("Scope snapshot fetch/validation failed"), cancellationToken);
                return;
            }
            logger.LogDebug("Task {TaskId} fetched and validated scope snapshot {SnapshotId}", task.TaskId, snapshot.SnapshotId);
        }

        await StartTaskAsync(task.TaskId, cancellationToken);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = StartHeartbeatTimerAsync(heartbeatCts.Token);

        var context = new WorkerExecutionContext(
            _options.WorkerId,
            (percent, message, checkpoint) => ReportProgressAsync(task.TaskId, percent, message, checkpoint, heartbeatCts.Token),
            request => RequestRateLimitTokenAsync(request, heartbeatCts.Token),
            signal => SignalBackpressureAsync(signal, heartbeatCts.Token));

        try
        {
            var result = await worker.ProcessAsync(task, context, heartbeatCts.Token);
            stopwatch.Stop();

            if (result.RetryAfter.HasValue && result.RetryAfter.Value > TimeSpan.Zero)
            {
                logger.LogInformation("Task {TaskId} received rate limit response, waiting {RetryAfter}s before completing", task.TaskId, result.RetryAfter.Value.TotalSeconds);
                await Task.Delay(result.RetryAfter.Value, heartbeatCts.Token);
            }

            foreach (var artifact in result.ProducedArtifacts)
            {
                await PublishArtifactAsync(task, artifact, heartbeatCts.Token);
            }

            foreach (var asset in result.ProducedAssets)
            {
                if (await IsProducedAssetInScopeAsync(task, asset, heartbeatCts.Token))
                {
                    var createdAsset = await PublishAssetAsync(task, asset, heartbeatCts.Token);
                    if (createdAsset is not null)
                    {
                        await CreateRelationshipAsync(task, createdAsset, asset.AssetType, heartbeatCts.Token);
                        metrics.RecordAssetProduced(task.ProgramId, worker.Capability.WorkerType, asset.AssetType);
                    }
                }
            }

            metrics.RecordTaskProcessed(task.ProgramId, worker.Capability.WorkerType, result.PartiallySucceeded);
            metrics.RecordTaskDuration(task.ProgramId, worker.Capability.WorkerType, stopwatch.Elapsed.TotalMilliseconds);

            await CompleteTaskAsync(task.TaskId, result, heartbeatCts.Token);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            metrics.RecordTaskFailed(task.ProgramId, worker.Capability.WorkerType, ex.GetType().Name);
            metrics.RecordTaskDuration(task.ProgramId, worker.Capability.WorkerType, stopwatch.Elapsed.TotalMilliseconds);
            logger.LogError(ex, "Task {TaskId} failed in worker {WorkerId}", task.TaskId, _options.WorkerId);
            await FailTaskAsync(task.TaskId, ex, heartbeatCts.Token);
        }
        finally
        {
            _taskCheckpoints.TryRemove(task.TaskId, out _);
            heartbeatCts.Cancel();
            await heartbeatTask;

            try
            {
                await HeartbeatAsync(Volatile.Read(ref _runningTaskCount), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final heartbeat failed for worker {WorkerType} ({WorkerId})", worker.Capability.WorkerType, _options.WorkerId);
            }
        }
    }

    private async Task StartHeartbeatTimerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);
                await HeartbeatAsync(Volatile.Read(ref _runningTaskCount), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Heartbeat failed for worker {WorkerType} ({WorkerId})", worker.Capability.WorkerType, _options.WorkerId);
            }
        }
    }

    private async Task RegisterWorkerAsync(CancellationToken cancellationToken)
    {
        var request = new WorkerRegistrationRequest(_options.WorkerId, worker.Capability, typeof(IReconWorker).Assembly.GetName().Version?.ToString());
        var client = CreateClient(_options.RealtimeServiceBaseAddress);

        using var response = await client.PostAsJsonAsync("/workers/register", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task HeartbeatAsync(int runningTasks, CancellationToken cancellationToken)
    {
        var request = new WorkerHeartbeatRequest(
            _options.WorkerId,
            worker.Capability.WorkerType,
            runningTasks,
            worker.Capability.MaxConcurrency,
            DateTimeOffset.UtcNow);

        var client = CreateClient(_options.RealtimeServiceBaseAddress);

        using var response = await client.PostAsJsonAsync("/workers/heartbeat", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ReconTaskDto?> LeaseTaskAsync(CancellationToken cancellationToken)
    {
        var request = new LeaseReconTaskRequest(_options.WorkerId, worker.Capability.WorkerType, worker.Capability.SubscribedAssetTypes, _options.LeaseDuration);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync("/tasks/lease", request, JsonOptions, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReconTaskDto>(JsonOptions, cancellationToken);
    }

    private async Task StartTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var client = CreateClient(_options.TaskServiceBaseAddress);
        using var response = await client.PostAsync($"/tasks/{taskId}/start?workerId={Uri.EscapeDataString(_options.WorkerId)}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ReportProgressAsync(
        Guid taskId,
        int percent,
        string message,
        string? checkpointJson,
        CancellationToken cancellationToken)
    {
        if (checkpointJson is not null)
        {
            _taskCheckpoints[taskId] = checkpointJson;
        }

        var request = new UpdateReconTaskProgressRequest(percent, message, checkpointJson);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/progress", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> RequestRateLimitTokenAsync(RateLimitRequest request, CancellationToken cancellationToken)
    {
        var client = CreateClient(_options.RateLimitServiceBaseAddress);
        var payload = new RateLimitCheckRequest(
            request.ProgramId,
            request.ScopeId,
            request.Host,
            request.RegisteredDomain,
            request.Ip,
            request.WorkerType,
            request.ProxyId,
            request.PermitCount);

        using var response = await client.PostAsJsonAsync("/rate-limits/check", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var decision = await response.Content.ReadFromJsonAsync<RateLimitDecision>(JsonOptions, cancellationToken);

        if (decision?.IsAllowed != true)
        {
            metrics.RecordRateLimitHit(request.ProgramId, worker.Capability.WorkerType, request.Host ?? "unknown");
        }

        return decision?.IsAllowed == true;
    }

    private async Task SignalBackpressureAsync(RateLimitBackpressureSignal signal, CancellationToken cancellationToken)
    {
        var client = CreateClient(_options.RateLimitServiceBaseAddress);
        var payload = new RateLimitBackpressureRequest(signal.Host, signal.BucketKey, signal.RetryAfter, signal.ObservedStatusCode);

        using var response = await client.PostAsJsonAsync("/rate-limits/backpressure", payload, JsonOptions, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Signaled backpressure for host {Host}: {StatusCode}, retry after {RetryAfter}s",
                signal.Host, signal.ObservedStatusCode, signal.RetryAfter.TotalSeconds);
        }
        else
        {
            logger.LogWarning("Failed to signal backpressure for host {Host}: {StatusCode}", signal.Host, response.StatusCode);
        }
    }

    private async Task<AssetDto?> PublishAssetAsync(
        ReconTaskDto task,
        WorkerProducedAsset asset,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AssetType>(asset.AssetType, ignoreCase: true, out var assetType))
        {
            logger.LogWarning("Worker {WorkerType} produced unknown asset type {AssetType}", worker.Capability.WorkerType, asset.AssetType);
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

        var client = CreateClient(_options.AssetServiceBaseAddress);
        using var response = await client.PostAsJsonAsync("/assets", request, JsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to publish asset {AssetType} {Value}: {StatusCode}", asset.AssetType, asset.Value, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions, cancellationToken);
    }

    private async Task CreateRelationshipAsync(
        ReconTaskDto task,
        AssetDto createdAsset,
        string assetType,
        CancellationToken cancellationToken)
    {
        if (!task.InputAssetId.HasValue)
        {
            return;
        }

        var edgeType = DetermineEdgeType(assetType);

        var request = new CreateAssetRelationshipRequest(
            task.InputAssetId.Value,
            createdAsset.AssetId,
            edgeType,
            task.TaskId.ToString());

        var client = CreateClient(_options.AssetServiceBaseAddress);
        using var response = await client.PostAsJsonAsync("/assets/relationships", request, JsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to create relationship from {InputAssetId} to {AssetId}: {StatusCode}",
                task.InputAssetId, createdAsset.AssetId, response.StatusCode);
        }
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

    private async Task<bool> IsProducedAssetInScopeAsync(
        ReconTaskDto task,
        WorkerProducedAsset asset,
        CancellationToken cancellationToken)
    {
        if (!_options.ScopeValidationRequired)
        {
            return true;
        }

        var target = ScopeValidationTarget.Extract(asset);

        if (string.IsNullOrWhiteSpace(target))
        {
            logger.LogWarning(
                "Worker {WorkerType} skipped produced asset without scope-validatable target: {AssetType} {Value}",
                worker.Capability.WorkerType,
                asset.AssetType,
                asset.Value);
            return false;
        }

        var client = CreateClient(_options.ScopeServiceBaseAddress);
        var request = new ScopeValidationRequest(task.ProgramId, target, asset.AssetType);

        using var response = await client.PostAsJsonAsync("/scope-validation/check", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ScopeValidationResult>(JsonOptions, cancellationToken);

        if (result?.IsAllowed == true)
        {
            return true;
        }

        logger.LogInformation(
            "Worker {WorkerType} skipped out-of-scope produced asset {AssetType} {Value}: {Reason}",
            worker.Capability.WorkerType,
            asset.AssetType,
            asset.Value,
            result?.Reason.ToString() ?? "scope validation failed");

        return false;
    }

    private async Task CompleteTaskAsync(
        Guid taskId,
        WorkerProcessResult result,
        CancellationToken cancellationToken)
    {
        var request = new CompleteReconTaskRequest(result.PartiallySucceeded, result.OutputSummaryJson);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/complete", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task FailTaskAsync(Guid taskId, Exception ex, CancellationToken cancellationToken, string? checkpointJson = null)
    {
        var checkpoint = checkpointJson ?? (_taskCheckpoints.TryGetValue(taskId, out var saved) ? saved : null);
        var request = new FailReconTaskRequest(ex.GetType().Name, ex.Message, true, checkpoint);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/fail", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient(Uri baseAddress)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = baseAddress;
        return client;
    }

    private async Task<ScopeSnapshot?> FetchAndValidateSnapshotAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.SnapshotSecretKey))
        {
            logger.LogWarning("SnapshotSecretKey not configured, cannot fetch signed snapshot");
            return null;
        }

        var client = CreateClient(_options.ScopeServiceBaseAddress);
        using var response = await client.GetAsync($"/programs/{programId:N}/snapshot", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("No snapshot found for program {ProgramId}", programId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var snapshotJson = await response.Content.ReadAsStringAsync(cancellationToken);

        var snapshot = SnapshotSigner.DeserializeSnapshot(snapshotJson);
        if (snapshot is null)
        {
            logger.LogError("Failed to deserialize scope snapshot for program {ProgramId}", programId);
            return null;
        }

        if (!SnapshotSigner.VerifySignature(snapshot, _options.SnapshotSecretKey))
        {
            logger.LogError("Scope snapshot signature verification failed for program {ProgramId}", programId);
            return null;
        }

        logger.LogDebug("Scope snapshot verified for program {ProgramId}", programId);
        return snapshot;
    }

    private async Task PublishArtifactAsync(ReconTaskDto task, WorkerProducedArtifact artifact, CancellationToken cancellationToken)
    {
        _artifactStore ??= httpClientFactory.CreateClient("artifact");
        _artifactStore.BaseAddress = _options.ArtifactServiceBaseAddress;

        var artifactHash = artifact.ComputeHash();
        var key = $"{task.ProgramId:N}/{task.TaskId:N}/{artifactHash}-{SanitizeArtifactName(artifact.Name)}";
        var request = new CreateArtifactRequest(
            TargetId: task.InputAssetId ?? task.ScopeId ?? task.ProgramId,
            ProgramId: task.ProgramId,
            AssetId: task.InputAssetId,
            TaskRunId: task.TaskId,
            WorkerType: worker.Capability.WorkerType,
            ArtifactType: artifact.ArtifactType,
            ContentType: artifact.ContentType,
            StorageProvider: "worker-inline",
            StorageKey: key,
            SizeBytes: artifact.Data.LongLength,
            Sha256: artifactHash,
            PreviewText: BuildArtifactPreview(artifact));

        try
        {
            using var response = await _artifactStore.PostAsJsonAsync("/artifacts", request, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug("Published artifact metadata {Name} for task {TaskId}", artifact.Name, task.TaskId);
            }
            else
            {
                logger.LogWarning("Failed to publish artifact metadata {Name} for task {TaskId}: {StatusCode}",
                    artifact.Name,
                    task.TaskId,
                    response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish artifact metadata {Name} for task {TaskId}", artifact.Name, task.TaskId);
        }
    }

    private static string SanitizeArtifactName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }

    private static string? BuildArtifactPreview(WorkerProducedArtifact artifact)
    {
        if (artifact.Data.Length == 0)
        {
            return string.Empty;
        }

        var isTextLike = artifact.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || artifact.ContentType.Contains("text", StringComparison.OrdinalIgnoreCase)
            || artifact.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || artifact.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

        if (!isTextLike)
        {
            return null;
        }

        const int maxPreviewBytes = 64 * 1024;
        var previewBytes = artifact.Data.Length > maxPreviewBytes
            ? artifact.Data.AsSpan(0, maxPreviewBytes).ToArray()
            : artifact.Data;

        return System.Text.Encoding.UTF8.GetString(previewBytes);
    }

}

internal sealed record TaskRunState(
    CancellationTokenSource CancellationSource,
    string? CheckpointJson);

internal static class ScopeValidationTarget
{
    public static string? Extract(WorkerProducedAsset asset)
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
                ? uri.Host
                : asset.Value;
        }

        if (string.Equals(asset.AssetType, "DnsRecord", StringComparison.OrdinalIgnoreCase)
            && asset.Metadata?.TryGetValue("host", out var host) == true)
        {
            return host;
        }

        return asset.Value;
    }
}

internal class WorkerTimeoutException : Exception
{
    public bool IsRetryable { get; }

    public WorkerTimeoutException(string message, bool isRetryable = true) 
        : base(message)
    {
        IsRetryable = isRetryable;
    }
}
