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
    string? DiscoveredByTaskId,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyCollection<string> Tags);

public sealed record CreateAssetRequest(
    Guid ProgramId,
    Guid? ScopeId,
    AssetType Type,
    string Value,
    string? Subtype,
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
    int Page = 1,
    int PageSize = 100);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
