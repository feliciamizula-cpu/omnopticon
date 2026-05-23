namespace Argus.AgentService.ProviderUsage.Adapters;

using Argus.AgentService.Data;

public sealed class CodexCliUsageAdapter : IProviderUsageAdapter
{
    public string ProviderKey => "codex_cli";

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
            Unit = "requests",
            Source = "local_ledger",
            Confidence = "low",
            Status = "unknown",
            Message = "Codex CLI subscription quota is not machine-readable. Use Manual Snapshot to record remaining plan usage, or check the Codex dashboard."
        };

        return Task.FromResult(new ProviderUsageProbeResult([snapshot]));
    }
}
