using System.Text.Json;
using Argus.AssetService.Data;
using Argus.Contracts.Assets;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Argus.AssetService.Search;

public sealed class AssetSearchService
{
    public async Task<AssetSearchResult> SearchAsync(AssetDbContext dbContext, AssetSearchRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var query = dbContext.Assets.AsNoTracking().AsQueryable();

        query = ApplyFilters(query, request);

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        query = ApplySort(query, request.SortBy, request.SortDirection);

        var records = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return new AssetSearchResult(
            records.Select(r => r.ToDto()).ToArray(),
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    private static IQueryable<AssetRecord> ApplyFilters(IQueryable<AssetRecord> query, AssetSearchRequest request)
    {
        if (request.ProgramId.HasValue)
            query = query.Where(a => a.ProgramId == request.ProgramId);

        if (request.ScopeId.HasValue)
            query = query.Where(a => a.ScopeId == request.ScopeId);

        if (request.Types?.Count > 0)
            query = query.Where(a => request.Types.Contains(a.Type));

        if (request.Categories?.Count > 0)
            query = query.Where(a => request.Categories.Contains(a.Category));

        if (request.Subcategories?.Count > 0)
            query = query.Where(a => a.Subcategory != null && request.Subcategories.Any(s => s == a.Subcategory));

        if (request.TypeKeys?.Count > 0)
            query = query.Where(a => a.TypeKey != null && request.TypeKeys.Contains(a.TypeKey));

        if (request.Statuses?.Count > 0)
            query = query.Where(a => request.Statuses.Contains(a.Status));

        if (request.ScopeStatuses?.Count > 0)
            query = query.Where(a => a.ScopeStatus != null && request.ScopeStatuses.Contains(a.ScopeStatus.Value));

        if (request.VerificationStatuses?.Count > 0)
            query = query.Where(a => a.VerificationStatus != null && request.VerificationStatuses.Contains(a.VerificationStatus.Value));

        if (request.LifecycleStatuses?.Count > 0)
            query = query.Where(a => request.LifecycleStatuses.Contains(a.LifecycleStatus));

        if (request.Tags?.Count > 0)
            query = query.Where(a => a.Tags.Any(t => request.Tags.Contains(t)));

        if (request.RequiredTags?.Count > 0)
            foreach (var tag in request.RequiredTags)
                query = query.Where(a => a.Tags.Contains(tag));

        if (request.ExcludedTags?.Count > 0)
            query = query.Where(a => !a.Tags.Any(t => request.ExcludedTags.Contains(t)));

        if (request.MinConfidence.HasValue)
            query = query.Where(a => a.Confidence >= request.MinConfidence);

        if (request.MaxConfidence.HasValue)
            query = query.Where(a => a.Confidence <= request.MaxConfidence);

        if (request.MinRiskScore.HasValue)
            query = query.Where(a => a.RiskScore >= request.MinRiskScore);

        if (request.MaxRiskScore.HasValue)
            query = query.Where(a => a.RiskScore <= request.MaxRiskScore);

        if (request.MinInterestingScore.HasValue)
            query = query.Where(a => a.InterestingScore >= request.MinInterestingScore);

        if (request.MaxInterestingScore.HasValue)
            query = query.Where(a => a.InterestingScore <= request.MaxInterestingScore);

        if (request.HighValueOnly == true)
            query = query.Where(a => a.HighValue);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(a => EF.Functions.ILike(a.Value, $"%{request.Search}%"));

        if (!string.IsNullOrWhiteSpace(request.ValuePattern))
            query = query.Where(a => EF.Functions.ILike(a.Value, request.ValuePattern));

        return query;
    }

    private static IQueryable<AssetRecord> ApplySort(IQueryable<AssetRecord> query, string? sortBy, string? sortDirection)
    {
        var sort = sortBy?.ToLowerInvariant() ?? "lastseenat";
        var direction = sortDirection?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        return sort switch
        {
            "firstseenat" => direction == "asc"
                ? query.OrderBy(a => a.FirstSeenAt)
                : query.OrderByDescending(a => a.FirstSeenAt),
            "confidence" => direction == "asc"
                ? query.OrderBy(a => a.Confidence)
                : query.OrderByDescending(a => a.Confidence),
            "interesting_score" => direction == "asc"
                ? query.OrderBy(a => a.InterestingScore)
                : query.OrderByDescending(a => a.InterestingScore),
            "risk_score" => direction == "asc"
                ? query.OrderBy(a => a.RiskScore)
                : query.OrderByDescending(a => a.RiskScore),
            "value" => direction == "asc"
                ? query.OrderBy(a => a.Value)
                : query.OrderByDescending(a => a.Value),
            "type" => direction == "asc"
                ? query.OrderBy(a => a.Type)
                : query.OrderByDescending(a => a.Type),
            _ => direction == "asc"
                ? query.OrderBy(a => a.LastSeenAt)
                : query.OrderByDescending(a => a.LastSeenAt)
        };
    }
}