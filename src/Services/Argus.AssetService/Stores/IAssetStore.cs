using Argus.AssetService.Data;
using Argus.Contracts.Assets;

namespace Argus.AssetService.Stores;

public interface IAssetStore
{
    Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken);
    Task<AssetSearchResult> SearchAsync(AssetSearchRequest request, CancellationToken cancellationToken);
    Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken);
    Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken);
    Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken);
    Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken);
    Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken);
    Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken);
    Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken);
    Task<AssetDto> UpdateAsync(Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken);
    Task<AssetDto> VerifyAsync(Guid assetId, VerificationStatus status, string? notes, CancellationToken cancellationToken);
    Task<AssetDto> RejectAsync(Guid assetId, string? reason, CancellationToken cancellationToken);
    Task<AssetDto> MarkHighValueAsync(Guid assetId, bool highValue, string? reason, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetDto>> GetSubgraphAsync(Guid assetId, int? maxDepth, IReadOnlyCollection<AssetType>? assetTypes, CancellationToken cancellationToken);
    Task<AssetLineageDto> GetLineageAsync(Guid assetId, string direction, int depth, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetRelatedGroupDto>> GetRelatedAsync(Guid assetId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetObservationRecord>> GetObservationsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<AssetObservationRecord> AddObservationAsync(Guid assetId, string observationType, string status, string? summary, IDictionary<string, object>? data, CancellationToken cancellationToken);
    Task<BulkOperationResult> BulkOperationAsync(AssetBulkActionRequest request, CancellationToken cancellationToken);
}