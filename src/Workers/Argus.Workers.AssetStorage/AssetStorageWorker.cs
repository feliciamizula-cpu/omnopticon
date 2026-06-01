using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Workers.AssetStorage;

/// <summary>
/// The single writer to asset-service. Recon workers no longer POST assets themselves — they emit
/// <see cref="AssetProduced"/>, and this worker is the sole consumer that persists them (and relates
/// them back to their source asset). asset-service then emits AssetDiscovered to drive the pipeline.
/// </summary>
public sealed class AssetStorageWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _assetServiceBaseAddress;
    private readonly ILogger<AssetStorageWorker> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public AssetStorageWorker(
        IHttpClientFactory httpClientFactory,
        IOptions<ArgusWorkerOptions> options,
        ILogger<AssetStorageWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _assetServiceBaseAddress = options.Value.AssetServiceBaseAddress;
        _logger = logger;
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        WorkerType: "AssetStorageWorker",
        SubscribedAssetTypes: ["*"],
        ProducedAssetTypes: [],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 20,
        SubscribedEventTypes: [nameof(AssetProduced)]);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.InputPayloadJson))
            return WorkerProcessResult.Empty("No payload to store.");

        var produced = JsonSerializer.Deserialize<AssetProduced>(task.InputPayloadJson, JsonOptions);
        if (produced is null)
            return WorkerProcessResult.Empty("Unparsable AssetProduced payload.");

        if (!Enum.TryParse<AssetType>(produced.AssetType, ignoreCase: true, out var assetType))
        {
            _logger.LogWarning("AssetStorageWorker: unknown asset type {AssetType}", produced.AssetType);
            return WorkerProcessResult.Empty($"Unknown asset type {produced.AssetType}.");
        }

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = _assetServiceBaseAddress;

        var createRequest = new CreateAssetRequest(
            produced.ProgramId,
            produced.ScopeId,
            assetType,
            produced.Value,
            produced.Subtype,
            produced.Confidence,
            produced.SourceTaskId,
            produced.Metadata,
            produced.Tags);

        using var response = await client.PostAsJsonAsync("/assets", createRequest, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AssetStorageWorker: failed to store {AssetType} {Value}: {Status}",
                produced.AssetType, produced.Value, response.StatusCode);
            return WorkerProcessResult.Empty($"Store failed: {response.StatusCode}");
        }

        var stored = await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions, cancellationToken);

        // Relate the stored asset back to the asset it was discovered from.
        if (stored is not null && produced.InputAssetId is { } inputId && inputId != Guid.Empty)
        {
            var relationship = new CreateAssetRelationshipRequest(
                inputId,
                stored.AssetId,
                DetermineEdgeType(produced.AssetType),
                produced.SourceTaskId);

            using var relResponse = await client.PostAsJsonAsync("/assets/relationships", relationship, JsonOptions, cancellationToken);
            if (!relResponse.IsSuccessStatusCode)
                _logger.LogDebug("AssetStorageWorker: relationship create returned {Status}", relResponse.StatusCode);
        }

        _logger.LogInformation("AssetStorageWorker stored {AssetType} {Value} (from {Source})",
            produced.AssetType, produced.Value, produced.SourceWorkerType);

        return WorkerProcessResult.Empty("stored");
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
}
