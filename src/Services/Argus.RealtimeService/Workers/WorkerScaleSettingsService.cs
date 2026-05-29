using Argus.Contracts.Workers;
using Microsoft.EntityFrameworkCore;

namespace Argus.RealtimeService.Workers;

public sealed class WorkerScaleSettingsService : IWorkerScaleSettingsService
{
    private readonly IDbContextFactory<RealtimeDbContext> _dbFactory;
    private readonly IWorkerTypeCatalog _workerTypeCatalog;
    private readonly TimeProvider _timeProvider;

    public WorkerScaleSettingsService(
        IDbContextFactory<RealtimeDbContext> dbFactory,
        IWorkerTypeCatalog workerTypeCatalog,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _workerTypeCatalog = workerTypeCatalog;
        _timeProvider = timeProvider;
    }

    public async Task<WorkerTypeScaleSettingsDto> GetOrCreateAsync(string workerType, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var definition = _workerTypeCatalog.Find(workerType);
        var settings = await db.WorkerScaleSettings
            .FirstOrDefaultAsync(x => string.Equals(x.WorkerType, workerType, StringComparison.OrdinalIgnoreCase), ct);

        if (settings != null)
            return ToDto(settings);

        // Create default settings from catalog
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
        return ToDto(settings);
    }

    public async Task<WorkerTypeScaleSettingsDto> UpdateAsync(
        string workerType,
        UpdateWorkerScaleSettingsRequest request,
        string? actor,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var settings = await db.WorkerScaleSettings
            .FirstOrDefaultAsync(x => string.Equals(x.WorkerType, workerType, StringComparison.OrdinalIgnoreCase), ct)
            ?? throw new InvalidOperationException($"Worker type {workerType} not found");

        settings.DesiredReplicas = request.DesiredReplicas;
        settings.MinReplicas = request.MinReplicas;
        settings.MaxReplicas = request.MaxReplicas;
        settings.IsPaused = request.IsPaused;
        settings.DeploymentName = request.DeploymentName;
        settings.Namespace = request.Namespace;
        settings.UpdatedBy = actor;
        settings.UpdatedAt = _timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return ToDto(settings);
    }

    private static WorkerTypeScaleSettingsDto ToDto(WorkerScaleSettingRecord record)
    {
        return new WorkerTypeScaleSettingsDto(
            WorkerType: record.WorkerType,
            DisplayName: record.DisplayName,
            RuntimeMode: record.RuntimeMode,
            DesiredReplicas: record.DesiredReplicas,
            MinReplicas: record.MinReplicas,
            MaxReplicas: record.MaxReplicas,
            IsPaused: record.IsPaused,
            DeploymentName: record.DeploymentName,
            Namespace: record.Namespace,
            UpdatedBy: record.UpdatedBy,
            UpdatedAt: record.UpdatedAt);
    }
}