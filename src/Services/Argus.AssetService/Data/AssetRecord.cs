using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.Contracts.Assets;

namespace Argus.AssetService.Data;

public sealed class AssetRecord
{
    private static readonly Dictionary<AssetType, TimeSpan> ExpectedScanIntervals = new()
    {
        [AssetType.Subdomain] = TimeSpan.FromDays(1),
        [AssetType.Domain] = TimeSpan.FromDays(7),
        [AssetType.Ip] = TimeSpan.FromDays(7),
        [AssetType.Url] = TimeSpan.FromDays(3),
        [AssetType.HttpResponse] = TimeSpan.FromDays(7),
        [AssetType.HtmlPage] = TimeSpan.FromDays(7),
        [AssetType.JavaScriptFile] = TimeSpan.FromDays(14),
        [AssetType.CssFile] = TimeSpan.FromDays(30),
        [AssetType.JsonDocument] = TimeSpan.FromDays(30),
        [AssetType.ApiEndpoint] = TimeSpan.FromDays(3),
        [AssetType.Technology] = TimeSpan.FromDays(14),
        [AssetType.Form] = TimeSpan.FromDays(14),
        [AssetType.Observation] = TimeSpan.FromDays(30),
        [AssetType.Finding] = TimeSpan.FromDays(30),
        [AssetType.FindingCandidate] = TimeSpan.FromDays(30),
        [AssetType.Port] = TimeSpan.FromDays(7),
        [AssetType.DnsRecord] = TimeSpan.FromDays(7)
    };

    private static readonly TimeSpan MaxStalenessInterval = TimeSpan.FromDays(90);

    public Guid AssetId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public AssetType Type { get; set; }
    public string? Subtype { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NaturalKey { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public AssetStatus Status { get; set; }
    public int RiskScore { get; set; }
    public int InterestingScore { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastScannedAt { get; set; }
    public string? DiscoveredByTaskId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string TagsJson { get; set; } = "[]";

    public AssetCategory Category { get; set; }
    public AssetSubcategory? Subcategory { get; set; }
    public string? TypeKey { get; set; }
    public ScopeStatus? ScopeStatus { get; set; }
    public VerificationStatus? VerificationStatus { get; set; }
    public AssetLifecycleStatus LifecycleStatus { get; set; }
    public bool HighValue { get; set; }
    public string? SourceWorkerType { get; set; }
    public string? SourceWorkerId { get; set; }
    public Guid? SourceTaskRunId { get; set; }
    public Guid? SourceEventId { get; set; }
    public Guid? CorrelationId { get; set; }
    public int ParentCount { get; set; }
    public int ChildCount { get; set; }
    public int FindingCount { get; set; }
    public int ArtifactCount { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public string? Metadata2Json { get; set; }
    public string? Tags2Json { get; set; }

    public IReadOnlyDictionary<string, string> Metadata =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? new Dictionary<string, string>();

    public IReadOnlyCollection<string> Tags =>
        JsonSerializer.Deserialize<string[]>(TagsJson) ?? [];

    public IDictionary<string, object>? Metadata2 =>
        string.IsNullOrEmpty(Metadata2Json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(Metadata2Json);

    public IReadOnlyCollection<string>? Tags2 =>
        string.IsNullOrEmpty(Tags2Json)
            ? null
            : JsonSerializer.Deserialize<string[]>(Tags2Json);

    public int ComputeStalenessScore()
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveLastSeen = LastScannedAt ?? LastSeenAt;
        var interval = now - effectiveLastSeen;
        if (interval < TimeSpan.Zero) return 0;
        var expectedInterval = ExpectedScanIntervals.GetValueOrDefault(Type, TimeSpan.FromDays(7));
        if (interval >= MaxStalenessInterval) return 100;
        var score = (int)((interval.TotalHours / expectedInterval.TotalHours) * 100);
        return Math.Min(score, 100);
    }

    public AssetDto ToDto()
    {
        var stalenessScore = ComputeStalenessScore();
        return new AssetDto(
            AssetId,
            ProgramId,
            ScopeId,
            Type,
            Subtype,
            Value,
            NaturalKey,
            Confidence,
            Status,
            RiskScore,
            InterestingScore,
            FirstSeenAt,
            LastSeenAt,
            LastScannedAt,
            stalenessScore,
            DiscoveredByTaskId,
            Metadata,
            Tags)
        {
            Category = Category,
            Subcategory = Subcategory,
            TypeKey = TypeKey,
            ScopeStatus = ScopeStatus?.ToString(),
            VerificationStatus = VerificationStatus ?? Argus.Contracts.Assets.VerificationStatus.Unverified,
            LifecycleStatus = LifecycleStatus,
            HighValue = HighValue,
            SourceWorkerType = SourceWorkerType,
            SourceWorkerId = SourceWorkerId,
            SourceTaskRunId = SourceTaskRunId,
            SourceEventId = SourceEventId,
            CorrelationId = CorrelationId,
            ParentCount = ParentCount,
            ChildCount = ChildCount,
            FindingCount = FindingCount,
            ArtifactCount = ArtifactCount,
            LastObservedAt = LastObservedAt,
            Metadata2 = Metadata2,
            Tags2 = Tags2
        };
    }

    public static string BuildNaturalKey(Guid programId, AssetType type, string normalizedValue, Guid? scopeId)
    {
        var input = $"{programId:N}:{scopeId:N}:{type}:{normalizedValue}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}