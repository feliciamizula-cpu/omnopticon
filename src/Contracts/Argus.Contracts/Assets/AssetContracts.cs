using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;

namespace Argus.Contracts.Assets;

public enum AssetCategory
{
    Domain,
    Subdomain,
    Ip,
    Network,
    Port,
    Service,
    Url,
    HttpResponse,
    Document,
    Script,
    Style,
    Form,
    Api,
    Technology,
    Finding,
    Secret,
    Vulnerability,
    Observation,
    ToolRun,
    Other
}

public enum AssetSubcategory
{
    DomainRoot,
    DomainSubdomain,
    DomainWildcard,
    IpV4,
    IpV6,
    Cidr,
    Asn,
    PortProtocol,
    PortService,
    PortBanner,
    UrlRoot,
    UrlParameter,
    UrlFragment,
    UrlQuery,
    HttpGet,
    HttpPost,
    HttpApi,
    HtmlDocument,
    XmlDocument,
    JsonDocument,
    PdfDocument,
    TextDocument,
    ImageDocument,
    JavaScriptFile,
    TypeScriptFile,
    CssFile,
    StyleSheet,
    HtmlForm,
    JsonForm,
    ApiEndpoint,
    ApiPath,
    ApiMethod,
    TechnologyFingerprint,
    TechnologyWaf,
    TechnologyCdn,
    TechnologyFramework,
    TechnologyDatabase,
    FindingSqlInjection,
    FindingXss,
    FindingCsrf,
    FindingOpenRedirect,
    FindingSsrf,
    FindingIdor,
    FindingInformationDisclosure,
    FindingSensitiveDataExposure,
    FindingAuthBypass,
    FindingPrivilegeEscalation,
    FindingRce,
    SecretApiKey,
    SecretAwsKey,
    SecretPrivateKey,
    SecretJwt,
    SecretBearerToken,
    SecretBasicAuth,
    VulnerabilityCritical,
    VulnerabilityHigh,
    VulnerabilityMedium,
    VulnerabilityLow,
    ObservationNote,
    ObservationScreenshot,
    ObservationTrace,
    ObservationLog,
    ToolRunNmap,
    ToolRunGobuster,
    ToolRunNikto,
    ToolRunSqlmap,
    ToolRunCustom,
    Other
}

public enum ScopeStatus
{
    Unknown,
    InScope,
    OutOfScope,
    Excluded,
    NeedsReview
}

public enum VerificationStatus
{
    Unverified,
    Verified,
    Rejected,
    Stale
}

public enum AssetLifecycleStatus
{
    Discovered,
    Confirmed,
    Updated,
    Rejected,
    Archived,
    Error
}

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
    Finding,
    Port,
    DnsRecord,
    Secret,
    Vulnerability
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
    IReadOnlyCollection<string> Tags)
{
    public AssetCategory Category { get; init; }
    public AssetSubcategory? Subcategory { get; init; }
    public string? TypeKey { get; init; }
    public string? ScopeStatus { get; init; }
    public VerificationStatus VerificationStatus { get; init; }
    public AssetLifecycleStatus LifecycleStatus { get; init; }
    public bool HighValue { get; init; }
    public string? SourceWorkerType { get; init; }
    public string? SourceWorkerId { get; init; }
    public Guid? SourceTaskRunId { get; init; }
    public Guid? SourceEventId { get; init; }
    public Guid? CorrelationId { get; init; }
    public int ParentCount { get; init; }
    public int ChildCount { get; init; }
    public int FindingCount { get; init; }
    public int ArtifactCount { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    public IDictionary<string, object>? Metadata2 { get; init; }
    public IReadOnlyCollection<string>? Tags2 { get; init; }
}

public sealed record CreateAssetRequest(
    Guid ProgramId,
    Guid? ScopeId,
    AssetType Type,
    string Value,
    string? Subtype,
    decimal? Confidence,
    string? DiscoveredByTaskId,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyCollection<string>? Tags)
{
    public AssetCategory? Category { get; init; }
    public AssetSubcategory? Subcategory { get; init; }
    public string? TypeKey { get; init; }
    public string? ScopeStatus { get; init; }
    public VerificationStatus? VerificationStatus { get; init; }
    public string? SourceWorkerType { get; init; }
    public string? SourceWorkerId { get; init; }
    public Guid? SourceTaskRunId { get; init; }
    public Guid? SourceEventId { get; init; }
    public Guid? CorrelationId { get; init; }
    public IReadOnlyCollection<Guid>? ParentAssetIds { get; init; }
    public IDictionary<string, object>? Metadata2 { get; init; }
    public IReadOnlyCollection<string>? Tags2 { get; init; }
}

public sealed record UpdateAssetRequest(
    Guid AssetId,
    AssetType? Type = null,
    string? Subtype = null,
    string? Value = null,
    decimal? Confidence = null,
    AssetStatus? Status = null,
    int? RiskScore = null,
    int? InterestingScore = null,
    ScopeStatus? ScopeStatusValue = null,
    VerificationStatus? VerificationStatusValue = null,
    AssetLifecycleStatus? LifecycleStatus = null,
    bool? HighValue = null,
    IDictionary<string, object>? Metadata = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<string>? TagsToAdd = null,
    IReadOnlyCollection<string>? TagsToRemove = null);

public sealed record VerifyAssetRequest(
    Guid AssetId,
    VerificationStatus VerificationStatus,
    string? Notes = null);

public sealed record RejectAssetRequest(
    Guid AssetId,
    string? Reason = null);

public sealed record MarkHighValueAssetRequest(
    Guid AssetId,
    bool HighValue,
    string? Reason = null);

public sealed record AssetBulkActionRequest(
    IReadOnlyCollection<Guid> AssetIds,
    string Action,
    IDictionary<string, object>? Parameters = null);

public sealed record AssetSearchRequest(
    Guid? ProgramId,
    Guid? ScopeId,
    IReadOnlyCollection<AssetCategory>? Categories = null,
    IReadOnlyCollection<AssetSubcategory>? Subcategories = null,
    IReadOnlyCollection<string>? TypeKeys = null,
    IReadOnlyCollection<AssetType>? Types = null,
    IReadOnlyCollection<AssetStatus>? Statuses = null,
    IReadOnlyCollection<ScopeStatus>? ScopeStatuses = null,
    IReadOnlyCollection<VerificationStatus>? VerificationStatuses = null,
    IReadOnlyCollection<AssetLifecycleStatus>? LifecycleStatuses = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<string>? RequiredTags = null,
    IReadOnlyCollection<string>? ExcludedTags = null,
    decimal? MinConfidence = null,
    decimal? MaxConfidence = null,
    int? MinRiskScore = null,
    int? MaxRiskScore = null,
    int? MinInterestingScore = null,
    int? MaxInterestingScore = null,
    bool? HighValueOnly = null,
    string? Search = null,
    string? ValuePattern = null,
    string? SortBy = null,
    string? SortDirection = null,
    int Page = 1,
    int PageSize = 100);

public sealed record AssetSearchResult(
    IReadOnlyCollection<AssetDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

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

public sealed record AssetLineageDto(
    Guid AssetId,
    IReadOnlyCollection<AssetLineageNodeDto> Nodes,
    IReadOnlyCollection<AssetLineageEdgeDto> Edges);

public sealed record AssetLineageNodeDto(
    Guid AssetId,
    string Value,
    AssetType Type,
    string? Subtype,
    int Depth,
    int Generation);

public sealed record AssetLineageEdgeDto(
    Guid FromAssetId,
    Guid ToAssetId,
    string EdgeType,
    int Depth);

public sealed record AssetRelatedGroupDto(
    Guid AssetId,
    string AssetValue,
    AssetType AssetType,
    string? RelationshipType,
    IReadOnlyCollection<AssetDto> RelatedAssets,
    int TotalCount);