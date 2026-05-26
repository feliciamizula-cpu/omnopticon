namespace Argus.Web;

public sealed record ProviderUsageAlertRule
{
    public int? RemBelow { get; init; }
    public int? RstWithin { get; init; }
}

public sealed record ProviderUsageAlertSaveArgs(string AlertKey, ProviderUsageAlertRule? Rule);
