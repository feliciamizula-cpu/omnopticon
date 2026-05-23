namespace Argus.AgentService.ProviderUsage;

using Argus.AgentService.Data;

public interface IProviderUsageAdapter
{
    string ProviderKey { get; }
    Task<ProviderUsageProbeResult> ProbeAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken);
}

public sealed record ProviderUsageProbeResult(
    IReadOnlyList<ProviderUsageSnapshotRecord> Snapshots,
    string? RawJson = null);
