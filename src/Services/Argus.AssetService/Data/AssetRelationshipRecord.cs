using Argus.Contracts.Assets;

namespace Argus.AssetService.Data;

public sealed class AssetRelationshipRecord
{
    public Guid RelationshipId { get; set; }
    public Guid FromAssetId { get; set; }
    public Guid ToAssetId { get; set; }
    public string EdgeType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? DiscoveredByTaskId { get; set; }
    public string? SourceWorkerType { get; set; }
    public string? SourceWorkerId { get; set; }
    public Guid? SourceTaskRunId { get; set; }
    public Guid? SourceEventId { get; set; }

    public AssetRelationshipDto ToDto() =>
        new(RelationshipId, FromAssetId, ToAssetId, EdgeType, CreatedAt, DiscoveredByTaskId);
}