using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventDrivenWorkers;

public sealed class EphemeralWorkerDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EphemeralWorkerRegistry _registry;
    private readonly ILogger<EphemeralWorkerDispatcher> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public EphemeralWorkerDispatcher(
        IServiceScopeFactory scopeFactory,
        EphemeralWorkerRegistry registry,
        ILogger<EphemeralWorkerDispatcher> logger,
        int maxConcurrency = 50)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task DispatchAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            await ProcessEventAsync(envelope, cancellationToken);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task ProcessEventAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var eventType = envelope.EventType;
        var assetType = ExtractAssetType(envelope);

        if (string.IsNullOrEmpty(assetType))
        {
            _logger.LogDebug("Event {EventType} has no asset type, skipping worker dispatch", eventType);
            return;
        }

        var workerTypes = _registry.GetWorkerTypesForEvent(eventType, assetType);

        if (workerTypes.Count == 0)
        {
            _logger.LogDebug("No workers subscribed to {EventType}:{AssetType}", eventType, assetType);
            return;
        }

        _logger.LogInformation(
            "Dispatching {EventType}:{AssetType} to {WorkerCount} worker(s)",
            eventType, assetType, workerTypes.Count);

        foreach (var workerType in workerTypes)
        {
            try
            {
                await InvokeWorkerAsync(workerType, envelope, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerType} failed processing {EventType}:{AssetType}",
                    workerType.Name, eventType, assetType);
            }
        }
    }

    private async Task InvokeWorkerAsync(
        Type workerType,
        IntegrationEventEnvelope<object> envelope,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var worker = (IEphemeralWorker)scope.ServiceProvider.GetRequiredService(workerType);
        var instanceId = $"{worker.Descriptor.WorkerType}-{Guid.NewGuid():N}";

        _logger.LogInformation("Ephemeral worker {InstanceId} started for {EventType}",
            instanceId, envelope.EventType);

        var asset = await ExtractAssetFromEventAsync(scope.ServiceProvider, envelope, cancellationToken);
        if (asset == null)
        {
            _logger.LogWarning("Failed to extract asset from event {EventId}", envelope.EventId);
            return;
        }

        var context = BuildWorkerContext(scope.ServiceProvider, instanceId, worker.Descriptor.WorkerType, asset, envelope);

        var result = await worker.ProcessAsync(context, cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation("Ephemeral worker {InstanceId} completed successfully, produced {AssetCount} assets",
                instanceId, result.ProducedAssets.Count);
        }
        else
        {
            _logger.LogWarning("Ephemeral worker {InstanceId} failed: {Error}", instanceId, result.Error);
        }
    }

    private static async Task<AssetDto?> ExtractAssetFromEventAsync(
        IServiceProvider serviceProvider,
        IntegrationEventEnvelope<object> envelope,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();

        var assetServiceAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE") ?? "http://asset-service";
        client.BaseAddress = new Uri(assetServiceAddress);

        var assetId = ExtractAssetId(envelope);
        if (!assetId.HasValue)
        {
            return null;
        }

        try
        {
            var response = await client.GetAsync($"/assets/{assetId.Value}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<AssetDto>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - worker may not need full asset
        }

        return null;
    }

    private static EphemeralWorkerContext BuildWorkerContext(
        IServiceProvider serviceProvider,
        string instanceId,
        string workerType,
        AssetDto asset,
        IntegrationEventEnvelope<object> triggeringEvent)
    {
        var publisher = serviceProvider.GetRequiredService<IIntegrationEventPublisher>();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        return new EphemeralWorkerContext(
            WorkerInstanceId: instanceId,
            WorkerType: workerType,
            Asset: asset,
            TriggeringEvent: triggeringEvent,
            PublishEventAsync: async (eventType, payload) =>
            {
                var envelopeMethod = typeof(IntegrationEventEnvelope<object>)
                    .GetMethod("Create")!
                    .MakeGenericMethod(payload.GetType());

                var envelope = envelopeMethod.Invoke(null, new[] { payload, eventType, "Argus.WorkerDispatcher", triggeringEvent.CorrelationId, triggeringEvent.EventId });

                var publishMethod = publisher.GetType().GetMethods()
                    .First(m => m.Name == "PublishAsync" && m.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(payload.GetType());

                await (Task)publishMethod.Invoke(publisher, new[] { envelope, CancellationToken.None })!;
            },
            RequestRateLimitTokenAsync: async (request) =>
            {
                var client = httpClientFactory.CreateClient();
                var rateLimitServiceAddress = Environment.GetEnvironmentVariable("ARGUS_RATE_LIMIT_SERVICE") ?? "http://rate-limit-service";
                client.BaseAddress = new Uri(rateLimitServiceAddress);

                var payload = new RateLimitCheckRequest(
                    request.ProgramId,
                    request.ScopeId,
                    request.Host,
                    request.RegisteredDomain,
                    request.Ip,
                    request.WorkerType,
                    request.ProxyId,
                    request.PermitCount);

                var response = await client.PostAsJsonAsync("/rate-limits/check", payload, JsonOptions);
                if (response.IsSuccessStatusCode)
                {
                    var decision = await response.Content.ReadFromJsonAsync<RateLimitDecision>(JsonOptions);
                    return decision?.IsAllowed == true;
                }
                return false;
            },
            StoreAssetAsync: async (assetToStore) =>
            {
                var client = httpClientFactory.CreateClient();
                var assetServiceAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE") ?? "http://asset-service";
                client.BaseAddress = new Uri(assetServiceAddress);

                var createRequest = new CreateAssetRequest(
                    assetToStore.ProgramId,
                    assetToStore.ScopeId,
                    assetToStore.Type,
                    assetToStore.Value,
                    assetToStore.Subtype,
                    instanceId,
                    assetToStore.Metadata,
                    assetToStore.Tags);

                var response = await client.PostAsJsonAsync("/assets", createRequest, JsonOptions);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions);
                }
                throw new InvalidOperationException($"Failed to store asset: {response.StatusCode}");
            },
            GetAssetAsync: async (assetId) =>
            {
                var client = httpClientFactory.CreateClient();
                var assetServiceAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE") ?? "http://asset-service";
                client.BaseAddress = new Uri(assetServiceAddress);

                var response = await client.GetAsync($"/assets/{assetId}", CancellationToken.None);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions);
                }
                return null;
            },
            CreateRelationshipAsync: async (fromAssetId, toAssetId) =>
            {
                var client = httpClientFactory.CreateClient();
                var assetServiceAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE") ?? "http://asset-service";
                client.BaseAddress = new Uri(assetServiceAddress);

                var request = new CreateAssetRelationshipRequest(
                    Guid.Parse(fromAssetId),
                    Guid.Parse(toAssetId),
                    "produces",
                    instanceId);

                var response = await client.PostAsJsonAsync("/assets/relationships", request, JsonOptions);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to create relationship: {response.StatusCode}");
                }
            });
    }

    private static Guid? ExtractAssetId(IntegrationEventEnvelope<object> envelope)
    {
        return envelope switch
        {
            IntegrationEventEnvelope<AssetDiscovered> assetDiscovered => assetDiscovered.Payload.AssetId,
            IntegrationEventEnvelope<AssetCreated> assetCreated => assetCreated.Payload.AssetId,
            IntegrationEventEnvelope<AssetConfirmed> assetConfirmed => assetConfirmed.Payload.AssetId,
            IntegrationEventEnvelope<AssetUpdated> assetUpdated => assetUpdated.Payload.AssetId,
            IntegrationEventEnvelope<AssetPropertyChanged> assetPropertyChanged => assetPropertyChanged.Payload.AssetId,
            _ => null
        };
    }

    private static string? ExtractAssetType(IntegrationEventEnvelope<object> envelope)
    {
        return envelope switch
        {
            IntegrationEventEnvelope<AssetDiscovered> assetDiscovered => assetDiscovered.Payload.AssetType,
            IntegrationEventEnvelope<AssetCreated> assetCreated => assetCreated.Payload.AssetType,
            IntegrationEventEnvelope<AssetConfirmed> assetConfirmed => assetConfirmed.Payload.AssetType,
            IntegrationEventEnvelope<AssetUpdated> assetUpdated => assetUpdated.Payload.AssetType,
            IntegrationEventEnvelope<AssetPropertyChanged> assetPropertyChanged => assetPropertyChanged.Payload.AssetType,
            _ => null
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ephemeral worker dispatcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
