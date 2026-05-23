namespace Argus.AgentService.Tests;

using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage;
using Xunit;

public sealed class ProviderUsageStatusCalculatorTests
{
    private static ProviderAccountRecord Account(decimal warning = 25, decimal critical = 10, bool enabled = true) =>
        new()
        {
            AccountId = Guid.NewGuid(),
            ProviderKey = "test",
            WarningThresholdPercent = warning,
            CriticalThresholdPercent = critical,
            Enabled = enabled
        };

    private static ProviderUsageSnapshotRecord Snap(
        decimal? remainingPct = null,
        decimal? remaining = null,
        string source = "api",
        string status = "unknown",
        DateTimeOffset? observedAt = null) =>
        new()
        {
            SnapshotId = Guid.NewGuid(),
            ObservedAt = observedAt ?? DateTimeOffset.UtcNow,
            RemainingPercent = remainingPct,
            RemainingAmount = remaining,
            Source = source,
            Status = status
        };

    [Fact]
    public void Healthy_WhenAboveWarningThreshold()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(remainingPct: 50), DateTimeOffset.UtcNow);
        Assert.Equal("healthy", result);
    }

    [Fact]
    public void Warning_AtWarningThreshold()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(warning: 25), Snap(remainingPct: 25), DateTimeOffset.UtcNow);
        Assert.Equal("warning", result);
    }

    [Fact]
    public void Warning_JustAboveCritical()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(remainingPct: 15), DateTimeOffset.UtcNow);
        Assert.Equal("warning", result);
    }

    [Fact]
    public void Critical_AtCriticalThreshold()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(critical: 10), Snap(remainingPct: 10), DateTimeOffset.UtcNow);
        Assert.Equal("critical", result);
    }

    [Fact]
    public void Critical_BelowCriticalThreshold()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(remainingPct: 5), DateTimeOffset.UtcNow);
        Assert.Equal("critical", result);
    }

    [Fact]
    public void Exhausted_WhenRemainingAmountIsZero()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(remaining: 0), DateTimeOffset.UtcNow);
        Assert.Equal("exhausted", result);
    }

    [Fact]
    public void Exhausted_WhenRemainingAmountIsNegative()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(remaining: -1), DateTimeOffset.UtcNow);
        Assert.Equal("exhausted", result);
    }

    [Fact]
    public void Stale_WhenObservedMoreThan30MinutesAgo()
    {
        var staleTime = DateTimeOffset.UtcNow.AddMinutes(-31);
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(observedAt: staleTime), DateTimeOffset.UtcNow);
        Assert.Equal("stale", result);
    }

    [Fact]
    public void NotConfigured_WhenSourceIsNotConfigured()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(source: "not_configured"), DateTimeOffset.UtcNow);
        Assert.Equal("not_configured", result);
    }

    [Fact]
    public void Unknown_WhenAccountDisabled()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(enabled: false), Snap(remainingPct: 80), DateTimeOffset.UtcNow);
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void Error_WhenSnapshotStatusIsError()
    {
        var result = ProviderUsageStatusCalculator.CalculateStatus(
            Account(), Snap(status: "error"), DateTimeOffset.UtcNow);
        Assert.Equal("error", result);
    }
}
