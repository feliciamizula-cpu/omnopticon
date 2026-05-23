using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;

namespace Argus.Contracts.Assets;

public enum AssetType
{
    Program,
    Scope,
    Domain,
    Subdomain,
    Ip,
    Cidr,
    Url,
    HttpResponse,
    HtmlPage,
    JavaScriptFile,
    CssFile,
    JsonDocument,
    ApiEndpoint,
    Technology,
    FindingCandidate,
    Port,
    DnsRecord
}

public enum AssetStatus
{
    New,
    InScope,
    OutOfScope,
    Active,
    Archived,
    Error
}

public sealed record AssetDto(
    Guid AssetId,
    Guid ProgramId,
    Guid? ScopeId,
    AssetType Type,
    string? Subtype,
    string Value,
    string NaturalKey,
    decimal Confidence,
    AssetStatus Status,
    int RiskScore,
    int InterestingScore,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastScannedAt,
    int StalenessScore,
    string? DiscoveredByTaskId,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyCollection<string> Tags);

public sealed record CreateAssetRequest(
    Guid ProgramId,
    Guid? ScopeId,
    AssetType Type,
    string Value,
    string? Subtype,
    decimal? Confidence,
    string? DiscoveredByTaskId,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyCollection<string>? Tags);

public sealed record AssetRelationshipDto(
    Guid RelationshipId,
    Guid FromAssetId,
    Guid ToAssetId,
    string EdgeType,
    DateTimeOffset CreatedAt,
    string? DiscoveredByTaskId);

public sealed record CreateAssetRelationshipRequest(
    Guid FromAssetId,
    Guid ToAssetId,
    string EdgeType,
    string? DiscoveredByTaskId);

public sealed record AssetQuery(
    Guid? ProgramId,
    AssetType? Type,
    AssetStatus? Status,
    string? Search,
    string? Tag,
    int? MinInterestingScore,
    int? MinRiskScore,
    int? MinStalenessScore,
    int? MaxStalenessScore,
    string? Sort,
    string? Direction,
    int Page = 1,
    int PageSize = 100);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record UpdateAssetStatusRequest(
    AssetStatus Status,
    string? Reason);

public sealed record AddAssetTagsRequest(
    IReadOnlyCollection<string> Tags);

public sealed record BulkTagRequest(
    IReadOnlyCollection<Guid> AssetIds,
    IReadOnlyCollection<string> Tags);

public sealed record BulkEnqueueRequest(
    IReadOnlyCollection<Guid> AssetIds,
    string TaskType,
    string WorkerCapability,
    Guid ProgramId,
    Guid? ScopeId = null,
    int MaxAttempts = 3,
    WorkerPriority Priority = WorkerPriority.Normal);

public sealed record BulkEnqueueResponse(
    IReadOnlyCollection<ReconTaskDto> Tasks,
    int CreatedCount,
    int SkippedCount);