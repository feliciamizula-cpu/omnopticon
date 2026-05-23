namespace Argus.AgentService.ProviderUsage.Adapters;

using Argus.AgentService.Data;

public sealed class QwenDashScopeUsageAdapter : IProviderUsageAdapter
{
    public string ProviderKey => "qwen_dashscope";

    public Task<ProviderUsageProbeResult> ProbeAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        var apiKey = account.SecretName is not null
            ? Environment.GetEnvironmentVariable(account.SecretName)
            : null;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(new ProviderUsageProbeResult([new ProviderUsageSnapshotRecord
            {
                SnapshotId = Guid.NewGuid(),
                AccountId = account.AccountId,
                ProviderKey = account.ProviderKey,
                ObservedAt = DateTimeOffset.UtcNow,
                WindowKind = "observed_day",
                Unit = "requests",
                Source = "not_configured",
                Confidence = "high",
                Status = "not_configured",
                Message = $"Set {account.SecretName} to enable Qwen/DashScope monitoring."
            }]));
        }

        var snapshot = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "observed_day",
            Unit = "requests",
            Source = "local_ledger",
            Confidence = "low",
            Status = "unknown",
            Message = "No official remaining-quota API available for DashScope. Use Manual Snapshot to record console quota values."
        };

        return Task.FromResult(new ProviderUsageProbeResult([snapshot]));
    }
}
