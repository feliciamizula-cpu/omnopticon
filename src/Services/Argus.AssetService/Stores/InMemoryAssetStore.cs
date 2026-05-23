using System.Collections.Concurrent;
using System.Text.Json;
using Argus.AssetService.Data;
using Argus.AssetService.Normalization;
using Argus.AssetService.Scoring;
using Argus.Contracts.Assets;
using BulkOp = Argus.AssetService.Stores.EfAssetStore.BulkOperationResult;

namespace Argus.AssetService.Stores;

public sealed class InMemoryAssetStore : IAssetStore
{
    private readonly ConcurrentDictionary<Guid, AssetDto> _assets = new();
    private readonly ConcurrentDictionary<string, Guid> _naturalKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, AssetRelationshipDto> _relationships = new();

    public Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var assets = _assets.Values.AsEnumerable();

        if (query.ProgramId is not null)
            assets = assets.Where(asset => asset.ProgramId == query.ProgramId);

        if (query.Type is not null)
            assets = assets.Where(asset => asset.Type == query.Type);

        if (query.Status is not null)
            assets = assets.Where(asset => asset.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.Search))
            assets = assets.Where(asset => asset.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.Tag))
            assets = assets.Where(asset => asset.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase));

        if (query.MinInterestingScore is not null)
            assets = assets.Where(asset => asset.InterestingScore >= query.MinInterestingScore);

        if (query.MinRiskScore is not null)
            assets = assets.Where(asset => asset.RiskScore >= query.MinRiskScore);

        if (query.MinStalenessScore is not null)
            assets = assets.Where(asset => asset.StalenessScore >= query.MinStalenessScore);

        if (query.MaxStalenessScore is not null)
            assets = assets.Where(asset => asset.StalenessScore <= query.MaxStalenessScore);

        var sort = query.Sort?.ToLowerInvariant() ?? "lastseenat";
        var direction = query.Direction?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        assets = sort switch
        {
            "staleness_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.StalenessScore)
                : assets.OrderByDescending(asset => asset.StalenessScore),
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
                ? assets.OrderBy(asset => asset.Value, StringComparer.OrdinalIgnoreCase)
                : assets.OrderByDescending(asset => asset.Value, StringComparer.OrdinalIgnoreCase),
            _ => assets.OrderByDescending(asset => asset.LastSeenAt)
        };

        var ordered = assets.ToArray();

        var result = new PagedResult<AssetDto>(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            page,
            pageSize,
            ordered.Length);

        return Task.FromResult(result);
    }

    public Task<AssetSearchResult> SearchAsync(AssetSearchRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var assets = _assets.Values.AsEnumerable();

        if (request.ProgramId.HasValue)
            assets = assets.Where(a => a.ProgramId == request.ProgramId);

        if (request.ScopeId.HasValue)
            assets = assets.Where(a => a.ScopeId == request.ScopeId);

        if (request.Types?.Count > 0)
            assets = assets.Where(a => request.Types.Contains(a.Type));

        if (request.Statuses?.Count > 0)
            assets = assets.Where(a => request.Statuses.Contains(a.Status));

        if (request.HighValueOnly == true)
            assets = assets.Where(a => a.HighValue);

        if (!string.IsNullOrWhiteSpace(request.Search))
            assets = assets.Where(a => a.Value.Contains(request.Search, StringComparison.OrdinalIgnoreCase));

        if (request.MinConfidence.HasValue)
            assets = assets.Where(a => a.Confidence >= request.MinConfidence);

        if (request.MaxConfidence.HasValue)
            assets = assets.Where(a => a.Confidence <= request.MaxConfidence);

        if (request.MinRiskScore.HasValue)
            assets = assets.Where(a => a.RiskScore >= request.MinRiskScore);

        if (request.MaxRiskScore.HasValue)
            assets = assets.Where(a => a.RiskScore <= request.MaxRiskScore);

        if (request.MinInterestingScore.HasValue)
            assets = assets.Where(a => a.InterestingScore >= request.MinInterestingScore);

        if (request.MaxInterestingScore.HasValue)
            assets = assets.Where(a => a.InterestingScore <= request.MaxInterestingScore);

        if (request.Tags?.Count > 0)
            assets = assets.Where(a => a.Tags.Any(t => request.Tags.Contains(t)));

        var sortBy = request.SortBy?.ToLowerInvariant() ?? "lastseenat";
        var sortDir = request.SortDirection?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        assets = sortBy switch
        {
            "confidence" => sortDir == "asc"
                ? assets.OrderBy(a => a.Confidence)
                : assets.OrderByDescending(a => a.Confidence),
            "risk_score" => sortDir == "asc"
                ? assets.OrderBy(a => a.RiskScore)
                : assets.OrderByDescending(a => a.RiskScore),
            "interesting_score" => sortDir == "asc"
                ? assets.OrderBy(a => a.InterestingScore)
                : assets.OrderByDescending(a => a.InterestingScore),
            "firstseenat" => sortDir == "asc"
                ? assets.OrderBy(a => a.FirstSeenAt)
                : assets.OrderByDescending(a => a.FirstSeenAt),
            "value" => sortDir == "asc"
                ? assets.OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase)
                : assets.OrderByDescending(a => a.Value, StringComparer.OrdinalIgnoreCase),
            _ => assets.OrderByDescending(a => a.LastSeenAt)
        };

        var ordered = assets.ToArray();
        var totalCount = ordered.Length;
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        return Task.FromResult(new AssetSearchResult(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            page,
            pageSize,
            totalCount,
            totalPages));
    }

    public Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedValue = AssetNormalizer.NormalizeValue(request.Type, request.Value);
        var naturalKey = AssetRecord.BuildNaturalKey(request.ProgramId, request.Type, normalizedValue, request.ScopeId);

        if (_naturalKeys.TryGetValue(naturalKey, out var existingId) && _assets.TryGetValue(existingId, out var existing))
        {
            var updated = existing with
            {
                LastSeenAt = now,
                StalenessScore = 0,
                Metadata = AssetSerialization.MergeMetadata(existing.Metadata, request.Metadata),
                Tags = AssetSerialization.MergeTags(existing.Tags, request.Tags)
            };

            _assets[existing.AssetId] = updated;
            return Task.FromResult(new AssetUpsertResult(updated, WasCreated: false));
        }

        var asset = new AssetDto(
            Guid.NewGuid(),
            request.ProgramId,
            request.ScopeId,
            request.Type,
            request.Subtype,
            normalizedValue,
            naturalKey,
            Confidence: request.Confidence ?? 1.0m,
            Status: AssetStatus.New,
            RiskScore: 0,
            InterestingScore: AssetScoringService.ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue),
            FirstSeenAt: now,
            LastSeenAt: now,
            LastScannedAt: null,
            StalenessScore: 0,
            DiscoveredByTaskId: request.DiscoveredByTaskId,
            Metadata: request.Metadata ?? new Dictionary<string, string>(),
            Tags: request.Tags ?? []);

        _assets[asset.AssetId] = asset;
        _naturalKeys[naturalKey] = asset.AssetId;

        return Task.FromResult(new AssetUpsertResult(asset, WasCreated: true));
    }

    public Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        _assets.TryGetValue(assetId, out var asset);
        return Task.FromResult(asset);
    }

    public Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken) =>
        Task.FromResult(_assets.ContainsKey(assetId));

    public Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<AssetRelationshipDto> relationships = _relationships.Values
            .Where(edge => edge.FromAssetId == assetId || edge.ToAssetId == assetId)
            .OrderBy(edge => edge.EdgeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(relationships);
    }

    public Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken)
    {
        var edgeType = request.EdgeType.Trim().ToLowerInvariant();
        var exists = _relationships.Values.Any(r =>
            r.FromAssetId == request.FromAssetId && r.ToAssetId == request.ToAssetId && r.EdgeType == edgeType);
        if (exists)
            throw new InvalidOperationException("Relationship already exists.");

        var relationship = new AssetRelationshipDto(
            Guid.NewGuid(),
            request.FromAssetId,
            request.ToAssetId,
            edgeType,
            DateTimeOffset.UtcNow,
            request.DiscoveredByTaskId);

        _relationships[relationship.RelationshipId] = relationship;

        return Task.FromResult(relationship);
    }

    public Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with { Status = status };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with { Tags = AssetSerialization.MergeTags(asset.Tags, tags) };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with
        {
            Tags = asset.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with { Confidence = confidence };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> UpdateAsync(Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with
        {
            Type = request.Type ?? asset.Type,
            Subtype = request.Subtype ?? asset.Subtype,
            Value = request.Value ?? asset.Value,
            Confidence = request.Confidence ?? asset.Confidence,
            Status = request.Status ?? asset.Status,
            RiskScore = request.RiskScore ?? asset.RiskScore,
            InterestingScore = request.InterestingScore ?? asset.InterestingScore,
            Tags = request.Tags ?? asset.Tags
        };

        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> VerifyAsync(Guid assetId, VerificationStatus status, string? notes, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with
        {
            VerificationStatus = status,
            LifecycleStatus = status == VerificationStatus.Verified
                ? AssetLifecycleStatus.Confirmed
                : AssetLifecycleStatus.Rejected
        };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> RejectAsync(Guid assetId, string? reason, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with
        {
            VerificationStatus = VerificationStatus.Rejected,
            LifecycleStatus = AssetLifecycleStatus.Rejected
        };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> MarkHighValueAsync(Guid assetId, bool highValue, string? reason, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var updated = asset with { HighValue = highValue };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyCollection<AssetDto>> GetSubgraphAsync(Guid assetId, int? maxDepth, IReadOnlyCollection<AssetType>? assetTypes, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<(Guid AssetId, int Depth)>();
        queue.Enqueue((assetId, 0));

        var resultIds = new HashSet<Guid>();

        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current.AssetId))
                continue;

            if (maxDepth.HasValue && current.Depth >= maxDepth.Value)
                continue;

            var neighbors = _relationships.Values
                .Where(r => r.FromAssetId == current.AssetId)
                .Select(r => r.ToAssetId)
                .Where(id => !visited.Contains(id));

            foreach (var neighbor in neighbors)
            {
                resultIds.Add(neighbor);
                queue.Enqueue((neighbor, current.Depth + 1));
            }
        }

        var assets = _assets.Values.Where(a => resultIds.Contains(a.AssetId));

        if (assetTypes is not null && assetTypes.Count > 0)
        {
            var typeSet = assetTypes.ToHashSet();
            assets = assets.Where(a => typeSet.Contains(a.Type));
        }

        return Task.FromResult<IReadOnlyCollection<AssetDto>>(assets.ToArray());
    }

    public Task<AssetLineageDto> GetLineageAsync(Guid assetId, string direction, int depth, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var startAsset))
            throw new InvalidOperationException("Asset not found.");

        var maxDepth = Math.Clamp(depth, 1, 10);
        var visited = new HashSet<Guid> { assetId };
        var nodes = new List<AssetLineageNodeDto>();
        var edges = new List<AssetLineageEdgeDto>();

        nodes.Add(new AssetLineageNodeDto(assetId, startAsset.Value, startAsset.Type, startAsset.Subtype, 0, 0));

        var queue = new Queue<(Guid AssetId, int CurrentDepth)>();
        queue.Enqueue((assetId, 0));

        var lowerDirection = direction.ToLowerInvariant();

        while (queue.TryDequeue(out var current))
        {
            if (current.CurrentDepth >= maxDepth)
                continue;

            var relationships = _relationships.Values
                .Where(r => r.FromAssetId == current.AssetId || r.ToAssetId == current.AssetId)
                .ToArray();

            foreach (var relationship in relationships)
            {
                var neighborId = relationship.FromAssetId == current.AssetId
                    ? relationship.ToAssetId
                    : relationship.FromAssetId;

                if (!visited.Add(neighborId))
                    continue;

                if (!_assets.TryGetValue(neighborId, out var neighborAsset))
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
                    current.CurrentDepth + 1));

                queue.Enqueue((neighborId, current.CurrentDepth + 1));
            }
        }

        return Task.FromResult(new AssetLineageDto(assetId, nodes, edges));
    }

    public Task<IReadOnlyCollection<AssetRelatedGroupDto>> GetRelatedAsync(Guid assetId, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
            throw new InvalidOperationException("Asset not found.");

        var groups = new List<AssetRelatedGroupDto>();

        var parentIds = _relationships.Values
            .Where(r => r.ToAssetId == assetId)
            .Select(r => r.FromAssetId)
            .Distinct()
            .ToArray();

        if (parentIds.Length > 0)
        {
            var parentAssets = _assets.Values.Where(a => parentIds.Contains(a.AssetId)).ToList();
            if (parentAssets.Count > 0)
                groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "parent", parentAssets, parentAssets.Count));
        }

        var childIds = _relationships.Values
            .Where(r => r.FromAssetId == assetId)
            .Select(r => r.ToAssetId)
            .Distinct()
            .ToArray();

        if (childIds.Length > 0)
        {
            var childAssets = _assets.Values.Where(a => childIds.Contains(a.AssetId)).ToList();
            if (childAssets.Count > 0)
                groups.Add(new AssetRelatedGroupDto(assetId, asset.Value, asset.Type, "child", childAssets, childAssets.Count));
        }

        return Task.FromResult<IReadOnlyCollection<AssetRelatedGroupDto>>(groups);
    }

    public Task<IReadOnlyCollection<AssetObservationRecord>> GetObservationsAsync(Guid assetId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<AssetObservationRecord>>([]);

    public Task<AssetObservationRecord> AddObservationAsync(Guid assetId, string observationType, string status, string? summary, IDictionary<string, object>? data, CancellationToken cancellationToken)
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

        return Task.FromResult(observation);
    }

    public Task<BulkOperationResult> BulkOperationAsync(AssetBulkActionRequest request, CancellationToken cancellationToken)
    {
        var results = new List<AssetDto>();
        var errors = new List<string>();

        foreach (var assetId in request.AssetIds)
        {
            if (!_assets.TryGetValue(assetId, out var asset))
            {
                errors.Add($"Asset {assetId} not found.");
                continue;
            }

            results.Add(asset);
        }

        return Task.FromResult(new BulkOperationResult(results, results.Count, request.AssetIds.Count - results.Count, errors));
    }
}