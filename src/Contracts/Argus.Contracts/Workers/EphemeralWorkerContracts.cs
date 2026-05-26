using Argus.Contracts.Assets;
using Argus.Contracts.Events;

namespace Argus.Contracts.Workers;

public sealed record WorkerProducedAsset(
    string AssetType,
    string Value,
    string? Subtype = null,
    decimal? Confidence = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<ArtifactReference>? ArtifactReferences = null);

public sealed record ArtifactReference(
    string ArtifactType,
    string Name,
    string Hash);

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
    Func<string, object, CancellationToken, Task> PublishEventAsync,
    Func<EphemeralRateLimitRequest, CancellationToken, Task<bool>> RequestRateLimitTokenAsync,
    Func<AssetDto, CancellationToken, Task<AssetDto>> StoreAssetAsync,
    Func<Guid, CancellationToken, Task<AssetDto?>> GetAssetAsync,
    Func<string, string, CancellationToken, Task> CreateRelationshipAsync);

public sealed record EphemeralWorkerResult(
    bool Success,
    IReadOnlyCollection<WorkerProducedAsset> ProducedAssets,
    IReadOnlyCollection<PublishedEvent> PublishedEvents,
    string? Error = null);

public sealed record PublishedEvent(
    string EventType,
    object Payload);

public sealed record EphemeralRateLimitRequest(
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
