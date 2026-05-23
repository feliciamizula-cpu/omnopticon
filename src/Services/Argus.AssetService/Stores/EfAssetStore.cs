using System.Text;
using System.Text.Json;
using Argus.AssetService.Data;
using Argus.AssetService.Search;
using Argus.Contracts.Assets;
using Microsoft.EntityFrameworkCore;

namespace Argus.AssetService.Stores;

public sealed class EfAssetStore(AssetDbContext dbContext, AssetSearchService searchService) : IAssetStore
{
    public async Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var assets = dbContext.Assets.AsNoTracking().AsQueryable();

        if (query.ProgramId is not null)
            assets = assets.Where(asset => asset.ProgramId == query.ProgramId);

        if (query.Type is not null)
            assets = assets.Where(asset => asset.Type == query.Type);

        if (query.Status is not null)
            assets = assets.Where(asset => asset.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.Search))
            assets = assets.Where(asset => EF.Functions.ILike(asset.Value, $"%{query.Search}%"));

        if (query.MinInterestingScore is not null)
            assets = assets.Where(asset => asset.InterestingScore >= query.MinInterestingScore);

        if (query.MinRiskScore is not null)
            assets = assets.Where(asset => asset.RiskScore >= query.MinRiskScore);

        var totalCount = await assets.CountAsync(cancellationToken);

        var sort = query.Sort?.ToLowerInvariant() ?? "lastseenat";
        var direction = query.Direction?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        assets = sort switch
        {
            "interesting_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.InterestingScore)
                : assets.OrderByDescending(asset => asset.InterestingScore),
            "risk_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.RiskScore)
                : assets.OrderByDescending(asset => asset.RiskScore),
            "firstseenat" => direction == "asc"
                ? assets.OrderBy(asset => asset.FirstSeenAt)
                : assets.OrderByDescending(asset => asset.FirstSeenAt),
            "value" => direction == "asc"
                ? assets.OrderBy(asset => asset.Value)
                : assets.OrderByDescending(asset => asset.Value),
            _ => assets.OrderByDescending(asset => asset.LastSeenAt)
        };

        var records = await assets
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return new PagedResult<AssetDto>(records.Select(asset => asset.ToDto()).ToArray(), page, pageSize, totalCount);
    }

    public Task<AssetSearchResult> SearchAsync(AssetSearchRequest request, CancellationToken cancellationToken)
        => searchService.SearchAsync(dbContext, request, cancellationToken);

    public async Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedValue = AssetNormalizer.NormalizeValue(request.Type, request.Value);
        var naturalKey = AssetRecord.BuildNaturalKey(request.ProgramId, request.Type, normalizedValue, request.ScopeId);

        var existing = await dbContext.Assets
            .FirstOrDefaultAsync(asset =>
                asset.ProgramId == request.ProgramId
                && asset.Type == request.Type
                && asset.NaturalKey == naturalKey,
                cancellationToken);

        if (existing is not null)
        {
            existing.LastSeenAt = now;
            existing.MetadataJson = JsonSerializer.Serialize(AssetSerialization.MergeMetadata(existing.Metadata, request.Metadata));
            existing.TagsJson = JsonSerializer.Serialize(AssetSerialization.MergeTags(existing.Tags, request.Tags));

            if (request.Category.HasValue)
                existing.Category = request.Category.Value;
            if (request.Subcategory.HasValue)
                existing.Subcategory = request.Subcategory.Value;
            if (request.TypeKey is not null)
                existing.TypeKey = request.TypeKey;
            if (request.ScopeStatus is not null)
                existing.ScopeStatus = Enum.Parse<ScopeStatus>(request.ScopeStatus, true);
            if (request.VerificationStatus.HasValue)
                existing.VerificationStatus = request.VerificationStatus;
            if (request.SourceWorkerType is not null)
                existing.SourceWorkerType = request.SourceWorkerType;
            if (request.SourceWorkerId is not null)
                existing.SourceWorkerId = request.SourceWorkerId;
            if (request.SourceTaskRunId.HasValue)
                existing.SourceTaskRunId = request.SourceTaskRunId;
            if (request.SourceEventId.HasValue)
                existing.SourceEventId = request.SourceEventId;
            if (request.CorrelationId.HasValue)
                existing.CorrelationId = request.CorrelationId;

            await dbContext.SaveChangesAsync(cancellationToken);
            return new AssetUpsertResult(existing.ToDto(), WasCreated: false);
        }

        var category = request.Category ?? AssetNormalizer.InferCategory(request.Type);
        var typeKey = request.TypeKey ?? AssetNormalizer.InferTypeKey(request.Type);
        var interestingScore = AssetScoringService.ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue);

        var created = new AssetRecord
        {
            AssetId = Guid.NewGuid(),
            ProgramId = request.ProgramId,
            ScopeId = request.ScopeId,
            Type = request.Type,
            Subtype = request.Subtype,
            Value = normalizedValue,
            NaturalKey = naturalKey,
            Confidence = request.Confidence ?? 1.0m,
            Status = AssetStatus.New,
            RiskScore = 0,
            InterestingScore = interestingScore,
            FirstSeenAt = now,
            LastSeenAt = now,
            DiscoveredByTaskId = request.DiscoveredByTaskId,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>()),
            TagsJson = JsonSerializer.Serialize(request.Tags ?? []),
            Category = category,
            Subcategory = request.Subcategory,
            TypeKey = typeKey,
            ScopeStatus = request.ScopeStatus is not null ? Enum.Parse<ScopeStatus>(request.ScopeStatus, true) : null,
            VerificationStatus = request.VerificationStatus,
            LifecycleStatus = AssetLifecycleStatus.Discovered,
            HighValue = false,
            SourceWorkerType = request.SourceWorkerType,
            SourceWorkerId = request.SourceWorkerId,
            SourceTaskRunId = request.SourceTaskRunId,
            SourceEventId = request.SourceEventId,
            CorrelationId = request.CorrelationId
        };

        dbContext.Assets.Add(created);

        if (request.ParentAssetIds is not null && request.ParentAssetIds.Count > 0)
        {
            foreach (var parentId in request.ParentAssetIds)
            {
                var relationship = new AssetRelationshipRecord
                {
                    RelationshipId = Guid.NewGuid(),
                    FromAssetId = parentId,
                    ToAssetId = created.AssetId,
                    EdgeType = "child_of",
                    CreatedAt = now,
                    DiscoveredByTaskId = request.DiscoveredByTaskId
                };
                dbContext.AssetRelationships.Add(relationship);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new AssetUpsertResult(created.ToDto(), WasCreated: true);
    }

    public async Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.AssetId == assetId, cancellationToken);

        return asset?.ToDto();
    }

    public Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken) =>
        dbContext.Assets.AnyAsync(asset => asset.AssetId == assetId, cancellationToken);

    public async Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        return await dbContext.AssetRelationships
            .AsNoTracking()
            .Where(edge => edge.FromAssetId == assetId || edge.ToAssetId == assetId)
            .OrderBy(edge => edge.EdgeType)
            .Select(edge => edge.ToDto())
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken)
    {
        var edgeType = request.EdgeType.Trim().ToLowerInvariant();
        var exists = await dbContext.AssetRelationships.AnyAsync(
            r => r.FromAssetId == request.FromAssetId && r.ToAssetId == request.ToAssetId && r.EdgeType == edgeType,
            cancellationToken);
        if (exists)
            throw new InvalidOperationException("Relationship already exists.");

        var relationship = new AssetRelationshipRecord
        {
            RelationshipId = Guid.NewGuid(),
            FromAssetId = request.FromAssetId,
            ToAssetId = request.ToAssetId,
            EdgeType = edgeType,
            CreatedAt = DateTimeOffset.UtcNow,
            DiscoveredByTaskId = request.DiscoveredByTaskId
        };

        dbContext.AssetRelationships.Add(relationship);
        await dbContext.SaveChangesAsync(cancellationToken);
        return relationship.ToDto();
    }

    public async Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.Status = status;
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        var existingTags = asset.Tags;
        asset.TagsJson = JsonSerializer.Serialize(AssetSerialization.MergeTags(existingTags, tags));
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        var updatedTags = asset.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
        asset.TagsJson = JsonSerializer.Serialize(updatedTags);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.Confidence = confidence;
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> UpdateAsync(Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        if (request.Type.HasValue) asset.Type = request.Type.Value;
        if (request.Subtype is not null) asset.Subtype = request.Subtype;
        if (request.Value is not null) asset.Value = request.Value;
        if (request.Confidence.HasValue) asset.Confidence = request.Confidence.Value;
        if (request.Status.HasValue) asset.Status = request.Status.Value;
        if (request.RiskScore.HasValue) asset.RiskScore = request.RiskScore.Value;
        if (request.InterestingScore.HasValue) asset.InterestingScore = request.InterestingScore.Value;
        if (request.ScopeStatusValue.HasValue) asset.ScopeStatus = request.ScopeStatusValue;
        if (request.VerificationStatusValue.HasValue) asset.VerificationStatus = request.VerificationStatusValue;
        if (request.LifecycleStatus.HasValue) asset.LifecycleStatus = request.LifecycleStatus.Value;
        if (request.HighValue.HasValue) asset.HighValue = request.HighValue.Value;

        if (request.Metadata is not null)
            asset.MetadataJson = JsonSerializer.Serialize(request.Metadata);

        if (request.Tags is not null)
            asset.TagsJson = JsonSerializer.Serialize(request.Tags);

        if (request.TagsToAdd is not null && request.TagsToAdd.Count > 0)
            asset.TagsJson = JsonSerializer.Serialize(AssetSerialization.MergeTags(asset.Tags, request.TagsToAdd));

        if (request.TagsToRemove is not null && request.TagsToRemove.Count > 0)
        {
            var tagsToRemoveSet = request.TagsToRemove.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updatedTags = asset.Tags.Where(t => !tagsToRemoveSet.Contains(t)).ToArray();
            asset.TagsJson = JsonSerializer.Serialize(updatedTags);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> VerifyAsync(Guid assetId, VerificationStatus status, string? notes, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.VerificationStatus = status;
        asset.LifecycleStatus = status == VerificationStatus.Verified
            ? AssetLifecycleStatus.Confirmed
            : AssetLifecycleStatus.Rejected;

        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> RejectAsync(Guid assetId, string? reason, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.VerificationStatus = VerificationStatus.Rejected;
        asset.LifecycleStatus = AssetLifecycleStatus.Rejected;

        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> MarkHighValueAsync(Guid assetId, bool highValue, string? reason, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.HighValue = highValue;

        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<IReadOnlyCollection<AssetDto>> GetSubgraphAsync(Guid assetId, int? maxDepth, IReadOnlyCollection<AssetType>? assetTypes, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        var sql = new StringBuilder("""
            WITH RECURSIVE reachable AS (
                SELECT to_asset_id, 1 AS depth
                FROM asset_edges
                WHERE from_asset_id = @rootId
                UNION ALL
                SELECT r.to_asset_id, reachable.depth + 1
                FROM asset_edges r
                JOIN reachable ON r.from_asset_id = reachable.to_asset_id
                WHERE @maxDepth IS NULL OR reachable.depth < @maxDepth
            )
            SELECT a.* FROM assets a
            WHERE a.asset_id IN (SELECT to_asset_id FROM reachable)
            """);

        if (assetTypes is not null && assetTypes.Count > 0)
        {
            var typeNames = assetTypes.Select(t => t.ToString()).ToArray();
            sql.Append(" AND a.type = ANY(@assetTypes)");
        }

        var records = await connection.QueryAsync<AssetRecord>(
            sql.ToString(),
            new { rootId = assetId, maxDepth, assetTypes = assetTypes?.Select(t => t.ToString()).ToArray() });

        return records.Select(r => r.ToDto()).ToArray();
    }

    public async Task<AssetLineageDto> GetLineageAsync(Guid assetId, string direction, int depth, CancellationToken cancellationToken)
    {
        var maxDepth = Math.Clamp(depth, 1, 10);
        var visited = new HashSet<Guid> { assetId };
        var nodes = new List<AssetLineageNodeDto>();
        var edges = new List<AssetLineageEdgeDto>();
        var generation = 0;

        var startAsset = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken);
        if (startAsset is null)
            throw new InvalidOperationException("Asset not found.");

        nodes.Add(new AssetLineageNodeDto(assetId, startAsset.Value, startAsset.Type, startAsset.Subtype, 0, generation));

        var queue = new Queue<(Guid AssetId, int CurrentDepth)>();
        queue.Enqueue((assetId, 0));

        var lowerDirection = direction.ToLowerInvariant();

        while (queue.TryDequeue(out var current))
        {
            if (current.CurrentDepth >= maxDepth)
                continue;

            IQueryable<AssetRelationshipRecord> relationshipQuery;

            if (lowerDirection is "parents")
                relationshipQuery = dbContext.AssetRelationships.Where(r => r.ToAssetId == current.AssetId);
            else if (lowerDirection is "children")
                relationshipQuery = dbContext.AssetRelationships.Where(r => r.FromAssetId == current.AssetId);
            else
                relationshipQuery = dbContext.AssetRelationships.Where(r => r.FromAssetId == current.AssetId || r.ToAssetId == current.AssetId);

            var relationships = await relationshipQuery
                .Where(r => r.FromAssetId == current.AssetId || r.ToAssetId == current.AssetId)
                .ToArrayAsync(cancellationToken);

            foreach (var relationship in relationships)
            {
                var neighborId = relationship.FromAssetId == current.AssetId
                    ? relationship.ToAssetId
                    : relationship.FromAssetId;

                if (!visited.Add(neighborId))
                    continue;

                var neighborAsset = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.AssetId == neighborId, cancellationToken);
                if (neighborAsset is null)
                    continue;

                edges.Add(new AssetLineageEdgeDto(
                    relationship.FromAssetId,
                    relationship.ToAssetId,
                    relationship.EdgeType,
                    current.CurrentDepth + 1));

                nodes.Add(new AssetLineageNodeDto(
                    neighborAsset.AssetId,
                    neighborAsset.Value,
                    neighborAsset.Type,
                    neighborAsset.Subtype,
                    current.CurrentDepth + 1,
                    generation + current.CurrentDepth + 1));

                queue.Enqueue((neighborId, current.CurrentDepth + 1));
            }
        }

        return new AssetLineageDto(assetId, nodes, edges);
    }

    public async Task<IReadOnlyCollection<AssetRelatedGroupDto>> GetRelatedAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken);
        if (asset is null)
            throw new InvalidOperationException("Asset not found.");

        var groups = new List<AssetRelatedGroupDto>();

        var parents = await dbContext.AssetRelationships
            .Where(r => r.ToAssetId == assetId)
            .Select(r => r.FromAssetId)
            .ToArrayAsync(cancellationToken);

        if (parents.Length > 0)
        {
            var parentAssets = await dbContext.Assets.AsNoTracking()
                .Where(a => parents.Contains(a.AssetId))
                .ToArrayAsync(cancellationToken);
            groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "parent", parentAssets.Select(a => a.ToDto()).ToList(), parentAssets.Length));
        }

        var children = await dbContext.AssetRelationships
            .Where(r => r.FromAssetId == assetId)
            .Select(r => r.ToAssetId)
            .ToArrayAsync(cancellationToken);

        if (children.Length > 0)
        {
            var childAssets = await dbContext.Assets.AsNoTracking()
                .Where(a => children.Contains(a.AssetId))
                .ToArrayAsync(cancellationToken);
            groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "child", childAssets.Select(a => a.ToDto()).ToList(), childAssets.Length));
        }

        var siblings = new List<AssetDto>();
        if (parents.Length > 0)
        {
            var siblingIds = await dbContext.AssetRelationships
                .Where(r => r.ToAssetId == assetId)
                .SelectMany(r => dbContext.AssetRelationships.Where(sr => sr.ToAssetId == r.FromAssetId && sr.FromAssetId != assetId).Select(sr => sr.FromAssetId))
                .Distinct()
                .ToArrayAsync(cancellationToken);

            if (siblingIds.Length > 0)
            {
                var siblingAssets = await dbContext.Assets.AsNoTracking()
                    .Where(a => siblingIds.Contains(a.AssetId))
                    .ToArrayAsync(cancellationToken);
                siblings.AddRange(siblingAssets.Select(a => a.ToDto()));
            }
        }

        if (siblings.Count > 0)
            groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "sibling", siblings, siblings.Count));

        var sameHostAssets = await dbContext.Assets.AsNoTracking()
            .Where(a => a.AssetId != assetId && a.ProgramId == asset.ProgramId && ExtractHost(a.Value) == ExtractHost(asset.Value))
            .Take(50)
            .ToArrayAsync(cancellationToken);

        if (sameHostAssets.Length > 0)
            groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "same_host", sameHostAssets.Select(a => a.ToDto()).ToList(), sameHostAssets.Length));

        if (!string.IsNullOrWhiteSpace(asset.SourceWorkerType))
        {
            var sameWorkerAssets = await dbContext.Assets.AsNoTracking()
                .Where(a => a.AssetId != assetId && a.SourceWorkerType == asset.SourceWorkerType && a.SourceWorkerId == asset.SourceWorkerId)
                .Take(50)
                .ToArrayAsync(cancellationToken);

            if (sameWorkerAssets.Length > 0)
                groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "same_worker", sameWorkerAssets.Select(a => a.ToDto()).ToList(), sameWorkerAssets.Length));
        }

        if (asset.Tags.Count > 0)
        {
            var tagAssets = await dbContext.Assets.AsNoTracking()
                .Where(a => a.AssetId != assetId && a.ProgramId == asset.ProgramId && a.Tags.Any(t => asset.Tags.Contains(t)))
                .Take(50)
                .ToArrayAsync(cancellationToken);

            if (tagAssets.Length > 0)
                groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "same_tag", tagAssets.Select(a => a.ToDto()).ToList(), tagAssets.Length));
        }

        return groups;
    }

    public async Task<IReadOnlyCollection<AssetObservationRecord>> GetObservationsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        return await dbContext.AssetObservations
            .AsNoTracking()
            .Where(o => o.AssetId == assetId)
            .OrderByDescending(o => o.ObservedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AssetObservationRecord> AddObservationAsync(Guid assetId, string observationType, string status, string? summary, IDictionary<string, object>? data, CancellationToken cancellationToken)
    {
        var observation = new AssetObservationRecord
        {
            ObservationId = Guid.NewGuid(),
            AssetId = assetId,
            ObservedAt = DateTimeOffset.UtcNow,
            ObservationType = observationType,
            Status = status,
            Summary = summary,
            DataJson = data is not null ? JsonSerializer.Serialize(data) : "{}"
        };

        dbContext.AssetObservations.Add(observation);

        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken);
        if (asset is not null)
        {
            asset.LastObservedAt = observation.ObservedAt;

            var previousObservation = await dbContext.AssetObservations
                .Where(o => o.AssetId == assetId && o.ObservationType == observationType)
                .OrderByDescending(o => o.ObservedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (previousObservation is not null)
            {
                var hasChanged = DetectChange(previousObservation.Status, status, previousObservation.DataJson, observation.DataJson);
                if (hasChanged)
                    asset.LifecycleStatus = AssetLifecycleStatus.Updated;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return observation;
    }

    public async Task<BulkOperationResult> BulkOperationAsync(AssetBulkActionRequest request, CancellationToken cancellationToken)
    {
        var successCount = 0;
        var failureCount = 0;
        var results = new List<AssetDto>();
        var errors = new List<string>();

        foreach (var assetId in request.AssetIds)
        {
            try
            {
                var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken);
                if (asset is null)
                {
                    errors.Add($"Asset {assetId} not found.");
                    failureCount++;
                    continue;
                }

                switch (request.Action.ToLowerInvariant())
                {
                    case "verify":
                        asset.VerificationStatus = VerificationStatus.Verified;
                        asset.LifecycleStatus = AssetLifecycleStatus.Confirmed;
                        break;
                    case "reject":
                        asset.VerificationStatus = VerificationStatus.Rejected;
                        asset.LifecycleStatus = AssetLifecycleStatus.Rejected;
                        break;
                    case "mark_high_value":
                        asset.HighValue = true;
                        break;
                    case "unmark_high_value":
                        asset.HighValue = false;
                        break;
                    case "archive":
                        asset.Status = AssetStatus.Archived;
                        break;
                }

                results.Add(asset.ToDto());
                successCount++;
            }
            catch (Exception ex)
            {
                errors.Add($"Asset {assetId}: {ex.Message}");
                failureCount++;
            }
        }

        if (results.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(results, successCount, failureCount, errors);
    }

    private static string? ExtractHost(string value)
    {
        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return uri.Host;
            if (value.Contains('@'))
                return value.Split('@').LastOrDefault();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool DetectChange(string previousStatus, string newStatus, string previousData, string newData)
    {
        if (previousStatus != newStatus)
            return true;

        try
        {
            var prevDict = JsonSerializer.Deserialize<Dictionary<string, object>>(previousData);
            var newDict = JsonSerializer.Deserialize<Dictionary<string, object>>(newData);

            if (prevDict is null || newDict is null)
                return false;

            foreach (var kvp in newDict)
            {
                if (!prevDict.ContainsKey(kvp.Key))
                    return true;
                if (!JsonSerializer.Serialize(prevDict[kvp.Key]).Equals(JsonSerializer.Serialize(kvp.Value)))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }
}

internal sealed record BulkOperationResult(
    IReadOnlyCollection<AssetDto> Results,
    int SuccessCount,
    int FailureCount,
    IReadOnlyCollection<string> Errors);