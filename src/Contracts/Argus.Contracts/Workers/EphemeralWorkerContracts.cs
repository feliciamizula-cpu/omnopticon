using Argus.Contracts.Assets;
using Argus.Contracts.Events;

namespace Argus.Contracts.Workers;

public sealed record EphemeralWorkerDescriptor(
    string WorkerType,
    IReadOnlyCollection<string> SubscribedEvents,
    IReadOnlyCollection<string> SubscribedAssetTypes,
    IReadOnlyCollection<string> ProducedAssetTypes,
    bool RequiresHttp,
    bool IsSystemWorker);

public sealed record EphemeralWorkerContext(
    string WorkerInstanceId,
    string WorkerType,
    AssetDto Asset,
    IntegrationEventEnvelope<object> TriggeringEvent,
    Func<string, object, Task> PublishEventAsync,
    Func<RateLimitRequest, Task<bool>> RequestRateLimitTokenAsync,
    Func<AssetDto, Task<AssetDto>> StoreAssetAsync,
    Func<Guid, Task<AssetDto?>> GetAssetAsync,
    Func<string, string, Task> CreateRelationshipAsync);

public sealed record EphemeralWorkerResult(
    bool Success,
    IReadOnlyCollection<WorkerProducedAsset> ProducedAssets,
    IReadOnlyCollection<PublishedEvent> PublishedEvents,
    string? Error = null);

public sealed record WorkerProducedAsset(
    AssetType Type,
    string Value,
    string? Subtype = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyCollection<string>? Tags = null);

public sealed record PublishedEvent(
    string EventType,
    object Payload);

public sealed record RateLimitRequest(
    Guid ProgramId,
    Guid? ScopeId,
    string? Host,
    string? RegisteredDomain,
    string? Ip,
    string WorkerType,
    string? ProxyId = null,
    int PermitCount = 1);

public interface IEphemeralWorker
{
    EphemeralWorkerDescriptor Descriptor { get; }

    Task<EphemeralWorkerResult> ProcessAsync(
        EphemeralWorkerContext context,
        CancellationToken cancellationToken);
}
