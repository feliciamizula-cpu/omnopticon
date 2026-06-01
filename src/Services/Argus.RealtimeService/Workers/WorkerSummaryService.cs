using Argus.Contracts.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Argus.RealtimeService.Workers;

public sealed class WorkerSummaryService : IWorkerSummaryService
{
    private readonly IDbContextFactory<RealtimeDbContext> _dbFactory;
    private readonly IWorkerTypeCatalog _workerTypeCatalog;
    private readonly IOptions<WorkerOptions> _options;
    private readonly TimeProvider _timeProvider;

    public WorkerSummaryService(
        IDbContextFactory<RealtimeDbContext> dbFactory,
        IWorkerTypeCatalog workerTypeCatalog,
        IOptions<WorkerOptions> options,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _workerTypeCatalog = workerTypeCatalog;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<WorkersPageSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var heartbeatTimeout = _options.Value.HeartbeatTimeout;
        var catalog = _workerTypeCatalog.GetAll();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var workerRecords = await db.Workers.ToListAsync(ct);
        var settings = await db.WorkerScaleSettings.ToListAsync(ct);
        var recentCommands = await db.WorkerScaleCommands
            .GroupBy(x => x.WorkerType)
            .Select(g => g.OrderByDescending(x => x.RequestedAt).First())
            .ToListAsync(ct);

        var allTypes = catalog.Select(x => x.WorkerType)
            .Concat(workerRecords.Select(x => x.WorkerType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        var rows = new List<WorkerTypeSummaryDto>();

        foreach (var workerType in allTypes)
        {
            var definition = _workerTypeCatalog.Find(workerType);
            var typeWorkers = workerRecords
                .Where(x => x.WorkerType == workerType)
                .ToList();

            var scale = await GetOrCreateScaleSettingsAsync(db, workerType, definition, ct);

            var instances = typeWorkers.Select(w => 
            {
                var isOnline = now - w.LastSeenAt < heartbeatTimeout;
                return new { Record = w, IsOnline = isOnline };
            }).ToList();

            var online = instances.Count(x => x.IsOnline);
            var offline = instances.Count(x => !x.IsOnline);
            var runningTasks = instances.Where(x => x.IsOnline).Sum(x => x.Record.RunningTasks);
            var maxConcurrency = instances.Where(x => x.IsOnline).Sum(x => x.Record.MaxConcurrency);
            var utilization = maxConcurrency <= 0 ? 0 : (runningTasks / (double)maxConcurrency) * 100;

            var lastCommand = recentCommands.FirstOrDefault(x => 
                x.WorkerType == workerType);

            rows.Add(new WorkerTypeSummaryDto(
                WorkerType: workerType,
                DisplayName: scale.DisplayName,
                RuntimeMode: scale.RuntimeMode,
                OnlineWorkers: online,
                OfflineWorkers: offline,
                DesiredReplicas: scale.DesiredReplicas,
                MinReplicas: scale.MinReplicas,
                MaxReplicas: scale.MaxReplicas,
                RunningTasks: runningTasks,
                MaxConcurrency: maxConcurrency,
                UtilizationPercent: Math.Round(utilization, 1),
                IsPaused: scale.IsPaused,
                HealthStatus: ComputeHealth(scale, online, utilization, lastCommand),
                LastSeenAt: typeWorkers.Count == 0 ? null : typeWorkers.Max(x => x.LastSeenAt),
                LastScaleCommandId: lastCommand?.CommandId,
                LastScaleCommandStatus: lastCommand?.Status,
                LastScaleMessage: lastCommand?.Message,
                SubscribedAssetTypes: definition?.SubscribedAssetTypes ?? [],
                ProducedAssetTypes: definition?.ProducedAssetTypes ?? [],
                RequiresHttp: definition?.RequiresHttp ?? false,
                SupportsCheckpoint: definition?.SupportsCheckpoint ?? false));
        }

        return new WorkersPageSummaryDto(
            rows.Count,
            rows.Sum(x => x.OnlineWorkers),
            rows.Sum(x => x.OfflineWorkers),
            rows.Sum(x => x.DesiredReplicas),
            rows.Sum(x => x.RunningTasks),
            rows.Sum(x => x.MaxConcurrency),
            rows.Count(x => x.HealthStatus == WorkerTypeHealthStatus.Saturated),
            rows.Count(x => x.HealthStatus == WorkerTypeHealthStatus.Stale),
            rows.Count(x => x.HealthStatus == WorkerTypeHealthStatus.Paused),
            rows);
    }

    public async Task<WorkerTypeSummaryDto?> GetWorkerTypeAsync(string workerType, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var heartbeatTimeout = _options.Value.HeartbeatTimeout;
        var catalog = _workerTypeCatalog.GetAll();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var workerRecords = await db.Workers
            .Where(x => x.WorkerType == workerType)
            .ToListAsync(ct);

        var settings = await db.WorkerScaleSettings
            .FirstOrDefaultAsync(x => x.WorkerType == workerType, ct);

        var recentCommands = await db.WorkerScaleCommands
            .Where(x => x.WorkerType == workerType)
            .OrderByDescending(x => x.RequestedAt)
            .FirstOrDefaultAsync(ct);

        var definition = _workerTypeCatalog.Find(workerType);
        if (definition == null && workerRecords.Count == 0)
        {
            return null;
        }

        var scale = await GetOrCreateScaleSettingsAsync(db, workerType, definition, ct);

        var instances = workerRecords.Select(w => 
        {
            var isOnline = now - w.LastSeenAt < heartbeatTimeout;
            return new { Record = w, IsOnline = isOnline };
        }).ToList();

        var online = instances.Count(x => x.IsOnline);
        var offline = instances.Count(x => !x.IsOnline);
        var runningTasks = instances.Where(x => x.IsOnline).Sum(x => x.Record.RunningTasks);
        var maxConcurrency = instances.Where(x => x.IsOnline).Sum(x => x.Record.MaxConcurrency);
        var utilization = maxConcurrency <= 0 ? 0 : (runningTasks / (double)maxConcurrency) * 100;

        return new WorkerTypeSummaryDto(
            WorkerType: workerType,
            DisplayName: scale.DisplayName,
            RuntimeMode: scale.RuntimeMode,
            OnlineWorkers: online,
            OfflineWorkers: offline,
            DesiredReplicas: scale.DesiredReplicas,
            MinReplicas: scale.MinReplicas,
            MaxReplicas: scale.MaxReplicas,
            RunningTasks: runningTasks,
            MaxConcurrency: maxConcurrency,
            UtilizationPercent: Math.Round(utilization, 1),
            IsPaused: scale.IsPaused,
            HealthStatus: ComputeHealth(scale, online, utilization, recentCommands),
            LastSeenAt: workerRecords.Count == 0 ? null : workerRecords.Max(x => x.LastSeenAt),
            LastScaleCommandId: recentCommands?.CommandId,
            LastScaleCommandStatus: recentCommands?.Status,
            LastScaleMessage: recentCommands?.Message,
            SubscribedAssetTypes: definition?.SubscribedAssetTypes ?? [],
            ProducedAssetTypes: definition?.ProducedAssetTypes ?? [],
            RequiresHttp: definition?.RequiresHttp ?? false,
            SupportsCheckpoint: definition?.SupportsCheckpoint ?? false);
    }

    public async Task<IReadOnlyCollection<WorkerInstanceDto>> GetInstancesAsync(string workerType, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var heartbeatTimeout = _options.Value.HeartbeatTimeout;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var capabilities = await db.WorkerCapabilities.ToListAsync(ct);

        var workers = await db.Workers
            .Where(x => x.WorkerType == workerType)
            .ToListAsync(ct);

        return workers.Select(w => 
        {
            var isOnline = now - w.LastSeenAt < heartbeatTimeout;
            var isStale = w.LastSeenAt < now - heartbeatTimeout;
            var utilization = w.MaxConcurrency <= 0 ? 0 : (w.RunningTasks / (double)w.MaxConcurrency) * 100;
            
            return new WorkerInstanceDto(
                WorkerId: w.WorkerId,
                WorkerType: w.WorkerType,
                Version: w.Version,
                RunningTasks: w.RunningTasks,
                MaxConcurrency: w.MaxConcurrency,
                UtilizationPercent: Math.Round(utilization, 1),
                LastSeenAt: w.LastSeenAt,
                IsOnline: isOnline,
                IsStale: isStale);
        }).ToList();
    }

    private WorkerTypeHealthStatus ComputeHealth(
        WorkerScaleSettingRecord settings,
        int online,
        double utilization,
        WorkerScaleCommandRecord? lastCommand)
    {
        var saturationThresholdPercent = _options.Value.SaturationThresholdPercent;

        if (settings.IsPaused)
            return WorkerTypeHealthStatus.Paused;

        if (lastCommand?.Status == "Pending")
            return WorkerTypeHealthStatus.ScalingPending;

        if (lastCommand?.Status == "Failed")
            return WorkerTypeHealthStatus.ScalingFailed;

        if (online == 0 && settings.DesiredReplicas > 0)
            return WorkerTypeHealthStatus.Stale;

        if (online < settings.DesiredReplicas)
            return WorkerTypeHealthStatus.UnderScaled;

        if (online > settings.DesiredReplicas)
            return WorkerTypeHealthStatus.OverScaled;

        if (utilization >= saturationThresholdPercent)
            return WorkerTypeHealthStatus.Saturated;

        return WorkerTypeHealthStatus.Healthy;
    }

    private async Task<WorkerScaleSettingRecord> GetOrCreateScaleSettingsAsync(
        RealtimeDbContext db,
        string workerType,
        WorkerTypeDefinition? definition,
        CancellationToken ct)
    {
        var settings = await db.WorkerScaleSettings
            .FirstOrDefaultAsync(x => x.WorkerType == workerType, ct);

        if (settings != null)
            return settings;

        // Create default settings from catalog if not found
        settings = new WorkerScaleSettingRecord
        {
            WorkerType = workerType,
            DisplayName = definition?.DisplayName ?? workerType,
            RuntimeMode = definition?.RuntimeMode ?? "continuous",
            DesiredReplicas = definition?.DefaultDesiredReplicas ?? 1,
            MinReplicas = definition?.DefaultMinReplicas ?? 0,
            MaxReplicas = definition?.DefaultMaxReplicas ?? 10,
            DeploymentName = definition?.DeploymentName,
            Namespace = null,
            IsPaused = false,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        db.WorkerScaleSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }
}