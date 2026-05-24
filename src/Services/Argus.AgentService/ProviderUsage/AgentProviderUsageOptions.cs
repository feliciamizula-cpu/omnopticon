namespace Argus.AgentService.ProviderUsage;

internal sealed class AgentProviderUsageOptions
{
    public int RefreshIntervalSeconds { get; set; } = 60;
    public int CommandTimeoutSeconds { get; set; } = 8;
    public List<AgentProviderOptions> Providers { get; set; } = [];
}

internal sealed class AgentProviderOptions
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string[] ToolAliases { get; set; } = [];
    public string Executable { get; set; } = string.Empty;
    public string VersionArguments { get; set; } = "--version";
    public string LoginArguments { get; set; } = string.Empty;
    public string AuthCheckArguments { get; set; } = string.Empty;
    public string[] AuthEnvironmentVariables { get; set; } = [];
    public string UsageExecutable { get; set; } = string.Empty;
    public string UsageArguments { get; set; } = string.Empty;
    public string DisplayModelHint { get; set; } = string.Empty;
    public string LoginInstructions { get; set; } = string.Empty;
    public AgentUsageWindowOptions FiveHour { get; set; } = new() { WindowId = "fiveHour", Label = "5 hour" };
    public AgentUsageWindowOptions TwentyFourHour { get; set; } = new() { WindowId = "twentyFourHour", Label = "24 hour" };
    public AgentUsageWindowOptions Weekly { get; set; } = new() { WindowId = "weekly", Label = "weekly" };
    public AgentUsageWindowOptions Monthly { get; set; } = new() { WindowId = "monthly", Label = "monthly" };
}

internal sealed class AgentUsageWindowOptions
{
    public string WindowId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal? Limit { get; set; }
    public decimal? Used { get; set; }
    public decimal? Remaining { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string Source { get; set; } = "configuration";
}
