namespace Argus.AgentService.ProviderUsage.Adapters;

using Argus.AgentService.Data;

public sealed class ManualUsageAdapter : IProviderUsageAdapter
{
    public string ProviderKey => "manual";

    public Task<ProviderUsageProbeResult> ProbeAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        var snapshot = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "manual",
            Unit = "manual",
            Source = "manual",
            Confidence = "manual",
            Status = "unknown",
            Message = "Use Manual Snapshot to enter usage data for this provider."
        };

        return Task.FromResult(new ProviderUsageProbeResult([snapshot]));
    }
}
