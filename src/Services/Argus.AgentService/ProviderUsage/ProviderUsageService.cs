namespace Argus.AgentService.ProviderUsage;

using System.Text.Json;
using Argus.AgentService.Data;
using Argus.Contracts.ProviderUsage;
using Microsoft.EntityFrameworkCore;

public sealed class ProviderUsageService(
    AgentDbContext dbContext,
    IEnumerable<IProviderUsageAdapter> adapters)
{
    private readonly Dictionary<string, IProviderUsageAdapter> _adapters =
        adapters.ToDictionary(a => a.ProviderKey, StringComparer.OrdinalIgnoreCase);

    public async Task<ProviderUsageSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = await dbContext.ProviderAccounts.AsNoTracking().ToListAsync(ct);

        var latestSnapshots = await dbContext.ProviderUsageSnapshots
            .AsNoTracking()
            .GroupBy(s => s.AccountId)
            .Select(g => g.OrderByDescending(s => s.ObservedAt).First())
            .ToListAsync(ct);

        var snapshotByAccount = latestSnapshots.ToDictionary(s => s.AccountId);

        var accountDtos = accounts.Select(a =>
        {
            snapshotByAccount.TryGetValue(a.AccountId, out var snap);
            return ToAccountDto(a, snap, now);
        }).ToList();

        return new ProviderUsageSummaryDto(
            GeneratedAt: now,
            TotalProviders: accountDtos.Count,
            HealthyCount: accountDtos.Count(a => a.LatestSnapshot?.Status == "healthy"),
            WarningCount: accountDtos.Count(a => a.LatestSnapshot?.Status == "warning"),
            CriticalCount: accountDtos.Count(a =>
                a.LatestSnapshot?.Status is "critical" or "exhausted"),
            ErrorCount: accountDtos.Count(a =>
                a.LatestSnapshot?.Status is "error" or "stale" or "unknown" or "not_configured"),
            Accounts: accountDtos);
    }

    public async Task<IReadOnlyList<ProviderUsageSnapshotDto>> GetHistoryAsync(
        Guid accountId, int take, CancellationToken ct)
    {
        var snapshots = await dbContext.ProviderUsageSnapshots
            .AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.ObservedAt)
            .Take(take)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return snapshots.Select(s => ToSnapshotDto(s, now)).ToList();
    }

    public async Task<ProviderAccountDto> CreateAccountAsync(
        CreateProviderAccountRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProviderAccountRecord
        {
            AccountId = Guid.NewGuid(),
            ProviderKey = request.ProviderKey.ToLowerInvariant(),
            DisplayName = request.DisplayName,
            AccountType = request.AccountType,
            AuthMode = request.AuthMode,
            SecretName = request.SecretName,
            BaseUrl = request.BaseUrl,
            RelatedToolsJson = JsonSerializer.Serialize(request.RelatedTools),
            RelatedModelsJson = JsonSerializer.Serialize(request.RelatedModels),
            WarningThresholdPercent = request.WarningThresholdPercent,
            CriticalThresholdPercent = request.CriticalThresholdPercent,
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.ProviderAccounts.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToAccountDto(record, null, now);
    }

    public async Task<ProviderAccountDto?> UpdateAccountAsync(
        Guid accountId, UpdateProviderAccountRequest request, CancellationToken ct)
    {
        var record = await dbContext.ProviderAccounts.FindAsync([accountId], ct);
        if (record is null) return null;

        if (request.DisplayName is not null) record.DisplayName = request.DisplayName;
        if (request.SecretName is not null) record.SecretName = request.SecretName;
        if (request.BaseUrl is not null) record.BaseUrl = request.BaseUrl;
        if (request.RelatedTools is not null) record.RelatedToolsJson = JsonSerializer.Serialize(request.RelatedTools);
        if (request.RelatedModels is not null) record.RelatedModelsJson = JsonSerializer.Serialize(request.RelatedModels);
        if (request.WarningThresholdPercent.HasValue) record.WarningThresholdPercent = request.WarningThresholdPercent.Value;
        if (request.CriticalThresholdPercent.HasValue) record.CriticalThresholdPercent = request.CriticalThresholdPercent.Value;
        if (request.Enabled.HasValue) record.Enabled = request.Enabled.Value;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        var snap = await LatestSnapshotAsync(accountId, ct);
        return ToAccountDto(record, snap, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ProviderUsageSnapshotDto>> RefreshAllAsync(CancellationToken ct)
    {
        var accounts = await dbContext.ProviderAccounts
            .AsNoTracking()
            .Where(a => a.Enabled)
            .ToListAsync(ct);

        var results = new List<ProviderUsageSnapshotDto>();
        foreach (var account in accounts)
        {
            var snapshots = await ProbeAndPersistAsync(account, ct);
            results.AddRange(snapshots);
        }

        return results;
    }

    public async Task<IReadOnlyList<ProviderUsageSnapshotDto>?> RefreshAsync(
        Guid accountId, CancellationToken ct)
    {
        var account = await dbContext.ProviderAccounts.FindAsync([accountId], ct);
        if (account is null) return null;
        return await ProbeAndPersistAsync(account, ct);
    }

    public async Task<ProviderUsageSnapshotDto?> AddManualSnapshotAsync(
        Guid accountId, ManualUsageSnapshotRequest request, CancellationToken ct)
    {
        var account = await dbContext.ProviderAccounts.FindAsync([accountId], ct);
        if (account is null) return null;

        var now = DateTimeOffset.UtcNow;
        decimal? remainingPct = null;
        if (request.RemainingAmount.HasValue && request.LimitAmount.HasValue && request.LimitAmount > 0)
            remainingPct = request.RemainingAmount.Value / request.LimitAmount.Value * 100;

        var snap = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = accountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = now,
            WindowKind = request.WindowKind,
            Unit = request.Unit,
            LimitAmount = request.LimitAmount,
            UsedAmount = request.UsedAmount,
            RemainingAmount = request.RemainingAmount,
            RemainingPercent = remainingPct,
            ResetsAt = request.ResetsAt,
            Source = "manual",
            Confidence = "manual",
            Status = ProviderUsageStatusCalculator.CalculateStatus(account,
                new ProviderUsageSnapshotRecord
                {
                    ObservedAt = now,
                    Source = "manual",
                    RemainingAmount = request.RemainingAmount,
                    RemainingPercent = remainingPct,
                    Status = "unknown"
                }, now),
            Message = request.Message
        };

        dbContext.ProviderUsageSnapshots.Add(snap);
        await dbContext.SaveChangesAsync(ct);
        return ToSnapshotDto(snap, now);
    }

    public async Task<int> PauseAgentsForProviderAsync(Guid accountId, CancellationToken ct)
    {
        var account = await dbContext.ProviderAccounts.FindAsync([accountId], ct);
        if (account is null) return 0;

        var tools = JsonSerializer.Deserialize<string[]>(account.RelatedToolsJson) ?? [];
        var paused = await dbContext.Set<AgentRecord>()
            .Where(a => a.Status == "active" && tools.Contains(a.Tool))
            .ToListAsync(ct);

        foreach (var agent in paused)
        {
            agent.Status = "paused";
            agent.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (paused.Count > 0)
            await dbContext.SaveChangesAsync(ct);

        return paused.Count;
    }

    public async Task RecordInvocationStartedAsync(
        string tool, string model, Guid? agentId, string? taskId, CancellationToken ct)
    {
        var account = await dbContext.ProviderAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.RelatedToolsJson.Contains($"\"{tool}\""), ct);
        if (account is null) return;

        var snap = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "invocation",
            Unit = "requests",
            UsedAmount = 1,
            Source = "local_ledger",
            Confidence = "low",
            Status = "unknown",
            Message = $"Invocation started: tool={tool} model={model}"
        };

        dbContext.ProviderUsageSnapshots.Add(snap);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task RecordInvocationCompletedAsync(
        string tool, string model, int exitCode,
        string? output, string? error, CancellationToken ct)
    {
        var account = await dbContext.ProviderAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.RelatedToolsJson.Contains($"\"{tool}\""), ct);
        if (account is null) return;

        var classifiedStatus = exitCode == 0
            ? "unknown"
            : ProviderLimitErrorClassifier.ClassifyStatus(output, error, exitCode);

        var snap = new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "invocation",
            Unit = "requests",
            UsedAmount = 1,
            Source = "local_ledger",
            Confidence = "low",
            Status = classifiedStatus,
            Message = exitCode != 0
                ? $"Exit {exitCode}: {error?[..Math.Min(300, error?.Length ?? 0)]}"
                : null
        };

        dbContext.ProviderUsageSnapshots.Add(snap);
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<ProviderUsageSnapshotDto>> ProbeAndPersistAsync(
        ProviderAccountRecord account, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_adapters.TryGetValue(account.ProviderKey, out var adapter))
        {
            var unknownSnap = new ProviderUsageSnapshotRecord
            {
                SnapshotId = Guid.NewGuid(),
                AccountId = account.AccountId,
                ProviderKey = account.ProviderKey,
                ObservedAt = now,
                WindowKind = "unknown",
                Unit = "unknown",
                Source = "unknown",
                Confidence = "low",
                Status = "unknown",
                Message = $"No adapter registered for provider key '{account.ProviderKey}'."
            };
            dbContext.ProviderUsageSnapshots.Add(unknownSnap);
            await dbContext.SaveChangesAsync(ct);
            return [ToSnapshotDto(unknownSnap, now)];
        }

        ProviderUsageProbeResult result;
        try
        {
            result = await adapter.ProbeAsync(account, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errSnap = new ProviderUsageSnapshotRecord
            {
                SnapshotId = Guid.NewGuid(),
                AccountId = account.AccountId,
                ProviderKey = account.ProviderKey,
                ObservedAt = now,
                WindowKind = "unknown",
                Unit = "unknown",
                Source = "api",
                Confidence = "low",
                Status = "error",
                Message = $"Adapter threw unexpectedly: {ex.Message}"
            };
            dbContext.ProviderUsageSnapshots.Add(errSnap);
            await dbContext.SaveChangesAsync(ct);
            return [ToSnapshotDto(errSnap, now)];
        }

        foreach (var snap in result.Snapshots)
        {
            snap.Status = ProviderUsageStatusCalculator.CalculateStatus(account, snap, now);
            dbContext.ProviderUsageSnapshots.Add(snap);
        }

        await dbContext.SaveChangesAsync(ct);
        return result.Snapshots.Select(s => ToSnapshotDto(s, now)).ToList();
    }

    private async Task<ProviderUsageSnapshotRecord?> LatestSnapshotAsync(Guid accountId, CancellationToken ct) =>
        await dbContext.ProviderUsageSnapshots
            .AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.ObservedAt)
            .FirstOrDefaultAsync(ct);

    private static ProviderAccountDto ToAccountDto(
        ProviderAccountRecord a,
        ProviderUsageSnapshotRecord? snap,
        DateTimeOffset now) =>
        new(
            AccountId: a.AccountId,
            ProviderKey: a.ProviderKey,
            DisplayName: a.DisplayName,
            AccountType: a.AccountType,
            AuthMode: a.AuthMode,
            SecretName: a.SecretName,
            BaseUrl: a.BaseUrl,
            RelatedTools: JsonSerializer.Deserialize<string[]>(a.RelatedToolsJson) ?? [],
            RelatedModels: JsonSerializer.Deserialize<string[]>(a.RelatedModelsJson) ?? [],
            WarningThresholdPercent: a.WarningThresholdPercent,
            CriticalThresholdPercent: a.CriticalThresholdPercent,
            Enabled: a.Enabled,
            CreatedAt: a.CreatedAt,
            UpdatedAt: a.UpdatedAt,
            LatestSnapshot: snap is not null ? ToSnapshotDto(snap, now) : null);

    private static ProviderUsageSnapshotDto ToSnapshotDto(
        ProviderUsageSnapshotRecord s, DateTimeOffset now) =>
        new(
            SnapshotId: s.SnapshotId,
            AccountId: s.AccountId,
            ProviderKey: s.ProviderKey,
            ObservedAt: s.ObservedAt,
            WindowKind: s.WindowKind,
            Unit: s.Unit,
            LimitAmount: s.LimitAmount,
            UsedAmount: s.UsedAmount,
            RemainingAmount: s.RemainingAmount,
            RemainingPercent: s.RemainingPercent,
            ResetsAt: s.ResetsAt,
            Source: s.Source,
            Confidence: s.Confidence,
            Status: s.Status,
            Message: s.Message,
            IsStale: s.ObservedAt < now.AddMinutes(-30));
}
