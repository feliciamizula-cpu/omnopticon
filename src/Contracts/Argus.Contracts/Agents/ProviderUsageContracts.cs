namespace Argus.Contracts.Agents;

public sealed record ProviderUsageOverviewDto(
    DateTimeOffset GeneratedAt,
    ProviderUsageDto[] Providers,
    ProviderRouteSelectionDto? RecommendedRoute);

public sealed record ProviderUsageDto(
    string ProviderId,
    string ProviderName,
    string ToolId,
    string[] ToolAliases,
    string DisplayModelHint,
    CliToolStatusDto ToolStatus,
    bool IsAuthenticated,
    string AuthStatus,
    string LoginCommand,
    string LoginInstructions,
    ProviderUsageWindowDto FiveHour,
    ProviderUsageWindowDto TwentyFourHour,
    ProviderUsageWindowDto Weekly,
    ProviderUsageWindowDto Monthly,
    decimal RoutingScore,
    string RoutingStatus,
    DateTimeOffset? LastCheckedAt,
    string? LastError);

public sealed record CliToolStatusDto(
    string ToolId,
    string Executable,
    bool IsAvailable,
    string Status,
    string? Version,
    string? Error,
    DateTimeOffset? CheckedAt);

public sealed record ProviderUsageWindowDto(
    string WindowId,
    string Label,
    decimal Limit,
    decimal Used,
    decimal Remaining,
    decimal RemainingPercent,
    DateTimeOffset? ResetsAt,
    bool IsKnown,
    string Source);

public sealed record ProviderLoginResponseDto(
    string ProviderId,
    string ProviderName,
    bool Started,
    bool Completed,
    int? ExitCode,
    string Command,
    string Output,
    string Error,
    string Message,
    ProviderUsageDto Provider);

public sealed record ProviderRouteSelectionDto(
    string? ProviderId,
    string? ProviderName,
    string? ToolId,
    string? AgentId,
    string? AgentName,
    bool IsRunnable,
    decimal RoutingScore,
    string Reason);
