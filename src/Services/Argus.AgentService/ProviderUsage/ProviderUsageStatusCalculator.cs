namespace Argus.AgentService.ProviderUsage;

using Argus.AgentService.Data;

public static class ProviderUsageStatusCalculator
{
    public static string CalculateStatus(
        ProviderAccountRecord account,
        ProviderUsageSnapshotRecord snapshot,
        DateTimeOffset now)
    {
        if (!account.Enabled) return "unknown";
        if (snapshot.Source == "not_configured") return "not_configured";
        if (snapshot.Status == "error") return "error";
        if (snapshot.ObservedAt < now.AddMinutes(-30)) return "stale";

        if (snapshot.RemainingAmount is <= 0) return "exhausted";

        if (snapshot.RemainingPercent is decimal pct)
        {
            if (pct <= account.CriticalThresholdPercent) return "critical";
            if (pct <= account.WarningThresholdPercent) return "warning";
            return "healthy";
        }

        return "unknown";
    }
}
