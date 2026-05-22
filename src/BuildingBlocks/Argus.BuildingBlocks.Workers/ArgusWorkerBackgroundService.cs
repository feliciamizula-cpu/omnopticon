using Argus.Contracts.Assets;
using Argus.Contracts.RateLimits;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace Argus.BuildingBlocks.Workers;

public sealed class ArgusWorkerBackgroundService(
    IReconWorker worker,
    IHttpClientFactory httpClientFactory,
    IOptions<ArgusWorkerOptions> options,
    ILogger<ArgusWorkerBackgroundService> logger) : BackgroundService
{
    private readonly ArgusWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterWorkerAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatAsync(runningTasks: 0, stoppingToken);
                var task = await LeaseTaskAsync(stoppingToken);

                if (task is null)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                    continue;
                }

                await RunTaskAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop failed for {WorkerType}", worker.Capability.WorkerType);
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    private async Task RunTaskAsync(ReconTaskDto task, CancellationToken cancellationToken)
    {
        logger.LogInformation("Worker {WorkerId} leased task {TaskId} ({TaskType})", _options.WorkerId, task.TaskId, task.TaskType);

        await StartTaskAsync(task.TaskId, cancellationToken);
        await HeartbeatAsync(runningTasks: 1, cancellationToken);

        var context = new WorkerExecutionContext(
            _options.WorkerId,
            (percent, message, checkpoint) => ReportProgressAsync(task.TaskId, percent, message, checkpoint, cancellationToken),
            request => RequestRateLimitTokenAsync(request, cancellationToken));

        try
        {
            var result = await worker.ProcessAsync(task, context, cancellationToken);

            foreach (var asset in result.ProducedAssets)
            {
                await PublishAssetAsync(task, asset, cancellationToken);
            }

            await CompleteTaskAsync(task.TaskId, result, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {TaskId} failed in worker {WorkerId}", task.TaskId, _options.WorkerId);
            await FailTaskAsync(task.TaskId, ex, cancellationToken);
        }
        finally
        {
            await HeartbeatAsync(runningTasks: 0, cancellationToken);
        }
    }

    private async Task RegisterWorkerAsync(CancellationToken cancellationToken)
    {
        var request = new WorkerRegistrationRequest(_options.WorkerId, worker.Capability, typeof(IReconWorker).Assembly.GetName().Version?.ToString());
        var client = CreateClient(_options.RealtimeServiceBaseAddress);

        using var response = await client.PostAsJsonAsync("/workers/register", request, cancellationToken);
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

        using var response = await client.PostAsJsonAsync("/workers/heartbeat", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ReconTaskDto?> LeaseTaskAsync(CancellationToken cancellationToken)
    {
        var request = new LeaseReconTaskRequest(_options.WorkerId, worker.Capability.WorkerType, _options.LeaseDuration);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync("/tasks/lease", request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReconTaskDto>(cancellationToken);
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
        var request = new UpdateReconTaskProgressRequest(percent, message, checkpointJson);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/progress", request, cancellationToken);
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

        using var response = await client.PostAsJsonAsync("/rate-limits/check", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var decision = await response.Content.ReadFromJsonAsync<RateLimitDecision>(cancellationToken);
        return decision?.IsAllowed == true;
    }

    private async Task PublishAssetAsync(
        ReconTaskDto task,
        WorkerProducedAsset asset,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AssetType>(asset.AssetType, ignoreCase: true, out var assetType))
        {
            logger.LogWarning("Worker {WorkerType} produced unknown asset type {AssetType}", worker.Capability.WorkerType, asset.AssetType);
            return;
        }

        var request = new CreateAssetRequest(
            task.ProgramId,
            task.ScopeId,
            assetType,
            asset.Value,
            asset.Subtype,
            task.TaskId.ToString(),
            asset.Metadata,
            asset.Tags);

        var client = CreateClient(_options.AssetServiceBaseAddress);
        using var response = await client.PostAsJsonAsync("/assets", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task CompleteTaskAsync(
        Guid taskId,
        WorkerProcessResult result,
        CancellationToken cancellationToken)
    {
        var request = new CompleteReconTaskRequest(result.PartiallySucceeded, result.OutputSummaryJson);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/complete", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task FailTaskAsync(Guid taskId, Exception ex, CancellationToken cancellationToken)
    {
        var request = new FailReconTaskRequest(ex.GetType().Name, ex.Message, Retryable: true, CheckpointJson: null);
        var client = CreateClient(_options.TaskServiceBaseAddress);

        using var response = await client.PostAsJsonAsync($"/tasks/{taskId}/fail", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient(Uri baseAddress)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = baseAddress;
        return client;
    }
}
