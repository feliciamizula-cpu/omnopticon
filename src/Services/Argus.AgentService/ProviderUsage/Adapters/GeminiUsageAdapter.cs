namespace Argus.AgentService.ProviderUsage.Adapters;

using Argus.AgentService.Data;

public sealed class GeminiUsageAdapter : IProviderUsageAdapter
{
    public string ProviderKey => "gemini";

    public Task<ProviderUsageProbeResult> ProbeAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        var credFile = account.SecretName is not null
            ? Environment.GetEnvironmentVariable(account.SecretName)
            : null;

        if (string.IsNullOrWhiteSpace(credFile))
        {
            return Task.FromResult(new ProviderUsageProbeResult([new ProviderUsageSnapshotRecord
            {
                SnapshotId = Guid.NewGuid(),
                AccountId = account.AccountId,
                ProviderKey = account.ProviderKey,
                ObservedAt = DateTimeOffset.UtcNow,
                WindowKind = "day",
                Unit = "requests",
                Source = "not_configured",
                Confidence = "high",
                Status = "not_configured",
                Message = $"Set {account.SecretName} to enable Google Cloud quota monitoring."
            }]));
        }

        var snapshot = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "day",
            Unit = "requests",
            Source = "cloud_quota_api",
            Confidence = "low",
            Status = "unknown",
            Message = "Google Cloud quota API polling not yet implemented. Use Manual Snapshot to record observed usage."
        };

        return Task.FromResult(new ProviderUsageProbeResult([snapshot]));
    }
}
