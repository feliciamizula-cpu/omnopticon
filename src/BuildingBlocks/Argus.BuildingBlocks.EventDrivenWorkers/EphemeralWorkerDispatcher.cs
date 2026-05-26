using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventDrivenWorkers;

public sealed class EphemeralWorkerDispatcher : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EphemeralWorkerRegistry _registry;
    private readonly EphemeralWorkerOptions _options;
    private readonly ILogger<EphemeralWorkerDispatcher> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly JsonSerializerOptions _jsonOptions;

    public EphemeralWorkerDispatcher(
        IServiceScopeFactory scopeFactory,
        EphemeralWorkerRegistry registry,
        IOptions<EphemeralWorkerOptions> options,
        ILogger<EphemeralWorkerDispatcher> logger,
        int maxConcurrency = 50)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
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

    private async Task InvokeWorkerAsync<T>(
        Type workerType,
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken)
        where T : notnull
    {
        using var scope = _scopeFactory.CreateScope();
        var worker = (IEphemeralWorker)scope.ServiceProvider.GetRequiredService(workerType);
        var instanceId = $"{worker.Descriptor.WorkerType}-{Guid.NewGuid():N}";

        _logger.LogInformation("Ephemeral worker {InstanceId} started for {EventType}",
            instanceId, envelope.EventType);

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var asset = await ExtractAssetFromEventAsync(httpClientFactory, envelope, cancellationToken);
        if (asset == null)
        {
            _logger.LogWarning("Failed to extract asset from event {EventId}", envelope.EventId);
            return;
        }

        var context = BuildWorkerContext(scope.ServiceProvider, httpClientFactory, instanceId, worker.Descriptor.WorkerType, asset, envelope, cancellationToken);

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

    private async Task<AssetDto?> ExtractAssetFromEventAsync<T>(
        IHttpClientFactory httpClientFactory,
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var client = httpClientFactory.CreateClient("ephemeral-worker");
        client.BaseAddress = _options.AssetServiceBaseAddress;

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
                return JsonSerializer.Deserialize<AssetDto>(json, _jsonOptions);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to extract asset from event {EventId}", envelope.EventId);
        }

        return null;
    }

    private EphemeralWorkerContext BuildWorkerContext<T>(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        string instanceId,
        string workerType,
        AssetDto asset,
        IntegrationEventEnvelope<T> triggeringEvent,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var publisher = serviceProvider.GetService(typeof(object)); // Placeholder - publisher may not be registered

        var envelopeObj = new IntegrationEventEnvelope<object>
        {
            EventId = triggeringEvent.EventId,
            EventType = triggeringEvent.EventType,
            Payload = triggeringEvent.Payload!,
            SourceService = triggeringEvent.SourceService,
            CorrelationId = triggeringEvent.CorrelationId,
            CausationId = triggeringEvent.CausationId,
            OccurredAt = triggeringEvent.OccurredAt,
            SchemaVersion = triggeringEvent.SchemaVersion
        };

        return new EphemeralWorkerContext(
            WorkerInstanceId: instanceId,
            WorkerType: workerType,
            Asset: asset,
            TriggeringEvent: envelopeObj,
            PublishEventAsync: async (eventType, payload, ct) =>
            {
                if (publisher == null) return;
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ct);
                using var _ = linkedCts;

                var envelopeType = typeof(IntegrationEventEnvelope<>).MakeGenericType(payload.GetType());
                var createMethod = envelopeType.GetMethod("Create")!;
                var envelope = createMethod.Invoke(null, new object[] { payload, eventType, "Argus.WorkerDispatcher", triggeringEvent.CorrelationId, triggeringEvent.EventId });

                if (publisher == null) return;
                var publishMethod = publisher.GetType().GetMethods()
                    .First(m => m.Name == "PublishAsync" && m.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(payload.GetType());

                await (Task)publishMethod.Invoke(publisher, new object[] { envelope!, linkedCts.Token })!;
            },
            RequestRateLimitTokenAsync: async (request, ct) =>
            {
                var client = httpClientFactory.CreateClient("ephemeral-worker");
                client.BaseAddress = _options.RateLimitServiceBaseAddress;

                var payload = new
                {
                    request.ProgramId,
                    request.ScopeId,
                    request.Host,
                    request.RegisteredDomain,
                    request.Ip,
                    request.WorkerType,
                    request.ProxyId,
                    request.PermitCount
                };

                var response = await client.PostAsJsonAsync("/rate-limits/check", payload, _jsonOptions, ct);
                if (response.IsSuccessStatusCode)
                {
                    var decision = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions, ct);
                    return decision.TryGetProperty("isAllowed", out var allowed) && allowed.GetBoolean();
                }
                return false;
            },
            StoreAssetAsync: async (assetToStore, ct) =>
            {
                var client = httpClientFactory.CreateClient("ephemeral-worker");
                client.BaseAddress = _options.AssetServiceBaseAddress;

                var createRequest = new CreateAssetRequest(
                    assetToStore.ProgramId,
                    assetToStore.ScopeId,
                    assetToStore.Type,
                    assetToStore.Value,
                    assetToStore.Subtype,
                    assetToStore.Confidence,
                    instanceId,
                    assetToStore.Metadata,
                    assetToStore.Tags);

                var response = await client.PostAsJsonAsync("/assets", createRequest, _jsonOptions, ct);
                if (response.IsSuccessStatusCode)
                {
                    var stored = await response.Content.ReadFromJsonAsync<AssetDto>(_jsonOptions, ct);
                    return stored!;
                }
                throw new InvalidOperationException($"Failed to store asset: {response.StatusCode}");
            },
            GetAssetAsync: async (assetId, ct) =>
            {
                var client = httpClientFactory.CreateClient("ephemeral-worker");
                client.BaseAddress = _options.AssetServiceBaseAddress;

                var response = await client.GetAsync($"/assets/{assetId}", ct);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<AssetDto>(_jsonOptions, ct);
                }
                return null;
            },
            CreateRelationshipAsync: async (fromAssetId, toAssetId, ct) =>
            {
                var client = httpClientFactory.CreateClient("ephemeral-worker");
                client.BaseAddress = _options.AssetServiceBaseAddress;

                var request = new CreateAssetRelationshipRequest(
                    Guid.Parse(fromAssetId),
                    Guid.Parse(toAssetId),
                    "produces",
                    instanceId);

                var response = await client.PostAsJsonAsync("/assets/relationships", request, _jsonOptions, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to create relationship: {response.StatusCode}");
                }
            });
    }

    private static Guid? ExtractAssetId<T>(IntegrationEventEnvelope<T> envelope)
        where T : notnull
    {
        var payload = envelope.Payload;
        if (payload == null) return null;

        var assetIdProp = payload.GetType().GetProperty("AssetId");
        return assetIdProp?.GetValue(payload) as Guid?;
    }

    private static string? ExtractAssetType<T>(IntegrationEventEnvelope<T> envelope)
        where T : notnull
    {
        var payload = envelope.Payload;
        if (payload == null) return null;

        var assetTypeProp = payload.GetType().GetProperty("AssetType");
        return assetTypeProp?.GetValue(payload) as string;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ephemeral worker dispatcher started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ephemeral worker dispatcher stopped");
        return Task.CompletedTask;
    }
}
