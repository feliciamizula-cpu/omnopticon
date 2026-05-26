using Argus.Contracts.Assets;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Logging;

namespace Argus.Workers.AssetStorage;

public sealed class AssetStorageWorker : IEphemeralWorker
{
    private readonly ILogger<AssetStorageWorker> _logger;

    public AssetStorageWorker(ILogger<AssetStorageWorker> logger)
    {
        _logger = logger;
    }

    public EphemeralWorkerDescriptor Descriptor { get; } = new(
        WorkerType: "AssetStorageWorker",
        SubscribedEvents: ["AssetConfirmed"],
        SubscribedAssetTypes: ["*"],
        ProducedAssetTypes: [],
        RequiresHttp: false,
        IsSystemWorker: true);

    public async Task<EphemeralWorkerResult> ProcessAsync(
        EphemeralWorkerContext context,
        CancellationToken cancellationToken)
    {
        var asset = context.Asset;

        _logger.LogInformation("AssetStorageWorker storing {AssetType}:{Value} for program {ProgramId}",
            asset.Type, asset.Value, asset.ProgramId);

        try
        {
            var storedAsset = await context.StoreAssetAsync(asset, cancellationToken);

            _logger.LogInformation("AssetStorageWorker stored asset {AssetId}", storedAsset.AssetId);

            return new EphemeralWorkerResult(
                Success: true,
                ProducedAssets: [],
                PublishedEvents: []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssetStorageWorker failed to store {AssetType}:{Value}",
                asset.Type, asset.Value);

            return new EphemeralWorkerResult(
                Success: false,
                ProducedAssets: [],
                PublishedEvents: [],
                Error: "Failed to store asset");
        }
    }
}
