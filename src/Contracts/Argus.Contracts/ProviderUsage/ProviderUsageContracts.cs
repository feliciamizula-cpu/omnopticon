namespace Argus.Contracts.ProviderUsage;

public sealed record ProviderAccountDto(
    Guid AccountId,
    string ProviderKey,
    string DisplayName,
    string AccountType,
    string AuthMode,
    string? SecretName,
    string? BaseUrl,
    string[] RelatedTools,
    string[] RelatedModels,
    decimal WarningThresholdPercent,
    decimal CriticalThresholdPercent,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ProviderUsageSnapshotDto? LatestSnapshot);

public sealed record ProviderUsageSnapshotDto(
    Guid SnapshotId,
    Guid AccountId,
    string ProviderKey,
    DateTimeOffset ObservedAt,
    string WindowKind,
    string Unit,
    decimal? LimitAmount,
    decimal? UsedAmount,
    decimal? RemainingAmount,
    decimal? RemainingPercent,
    DateTimeOffset? ResetsAt,
    string Source,
    string Confidence,
    string Status,
    string? Message,
    bool IsStale);

public sealed record ProviderUsageSummaryDto(
    DateTimeOffset GeneratedAt,
    int TotalProviders,
    int HealthyCount,
    int WarningCount,
    int CriticalCount,
    int ErrorCount,
    IReadOnlyList<ProviderAccountDto> Accounts);

public sealed record CreateProviderAccountRequest(
    string ProviderKey,
    string DisplayName,
    string AccountType,
    string AuthMode,
    string? SecretName,
    string? BaseUrl,
    string[] RelatedTools,
    string[] RelatedModels,
    decimal WarningThresholdPercent = 25,
    decimal CriticalThresholdPercent = 10,
    bool Enabled = true);

public sealed record UpdateProviderAccountRequest(
    string? DisplayName = null,
    string? SecretName = null,
    string? BaseUrl = null,
    string[]? RelatedTools = null,
    string[]? RelatedModels = null,
    decimal? WarningThresholdPercent = null,
    decimal? CriticalThresholdPercent = null,
    bool? Enabled = null);

public sealed record ManualUsageSnapshotRequest(
    string WindowKind,
    string Unit,
    decimal? LimitAmount,
    decimal? UsedAmount,
    decimal? RemainingAmount,
    DateTimeOffset? ResetsAt,
    string? Message = null);
