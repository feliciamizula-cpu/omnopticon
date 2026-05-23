namespace Argus.AgentService.Data;

public sealed class ProviderUsageSnapshotRecord
{
    public Guid SnapshotId { get; set; }
    public Guid AccountId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public string WindowKind { get; set; } = "unknown";
    public string Unit { get; set; } = "unknown";
    public decimal? LimitAmount { get; set; }
    public decimal? UsedAmount { get; set; }
    public decimal? RemainingAmount { get; set; }
    public decimal? RemainingPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string Source { get; set; } = "unknown";
    public string Confidence { get; set; } = "low";
    public string Status { get; set; } = "unknown";
    public string? Message { get; set; }
    public string? RawJson { get; set; }
}
