namespace Argus.AgentService.ProviderUsage.Adapters;

using Argus.AgentService.Data;

public sealed class ClaudeCodeUsageAdapter : IProviderUsageAdapter
{
    public string ProviderKey => "claude_code";

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
            WindowKind = "subscription",
            Unit = "messages",
            Source = "local_ledger",
            Confidence = "low",
            Status = "unknown",
            Message = "Claude Code subscription quota is not machine-readable. Use Manual Snapshot to record remaining plan usage, or set ANTHROPIC_ADMIN_API_KEY for organization-level usage data."
        };

        return Task.FromResult(new ProviderUsageProbeResult([snapshot]));
    }
}
