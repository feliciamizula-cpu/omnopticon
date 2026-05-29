using Argus.Contracts.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Argus.RealtimeService.Workers;

public sealed class WorkerScaleCommandService : IWorkerScaleCommandService
{
    private readonly IDbContextFactory<RealtimeDbContext> _dbFactory;
    private readonly IEnumerable<IWorkerScaler> _scalers;
    private readonly IOptions<WorkerOptions> _options;
    private readonly TimeProvider _timeProvider;

    public WorkerScaleCommandService(
        IDbContextFactory<RealtimeDbContext> dbFactory,
        IEnumerable<IWorkerScaler> scalers,
        IOptions<WorkerOptions> options,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _scalers = scalers;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<WorkerScaleCommandDto> ScaleAsync(
        string workerType,
        int desiredReplicas,
        string action,
        string? reason,
        string? actor,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Get current settings
        var settings = await db.WorkerScaleSettings
            .FirstOrDefaultAsync(x => string.Equals(x.WorkerType, workerType, StringComparison.OrdinalIgnoreCase), ct)
            ?? throw new InvalidOperationException($"Worker type {workerType} not found");

        // Create scale command record
        var command = new WorkerScaleCommandRecord
        {
            CommandId = Guid.NewGuid(),
            WorkerType = workerType,
            PreviousDesiredReplicas = settings.DesiredReplicas,
            RequestedDesiredReplicas = desiredReplicas,
            Action = action,
            Status = WorkerScaleCommandStatus.Pending.ToString(),
            Message = reason,
            Actor = actor,
            RequestedAt = _timeProvider.GetUtcNow()
        };

        db.WorkerScaleCommands.Add(command);
        
        // Update settings with new desired replicas
        settings.DesiredReplicas = desiredReplicas;
        settings.UpdatedBy = actor;
        settings.UpdatedAt = _timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);

        // Execute scaling
        WorkerScalerResult? result = null;
        try
        {
            var target = new WorkerScaleTarget(
                WorkerType: workerType,
                DeploymentName: settings.DeploymentName ?? workerType,
                Namespace: settings.Namespace,
                DesiredReplicas: desiredReplicas);
            
            var scaler = _scalers.FirstOrDefault(s => string.Equals(s.Kind, _options.Value.ScalerKind, StringComparison.OrdinalIgnoreCase))
                          ?? _scalers.First();
            
            result = await scaler.ScaleAsync(target, ct);
            
            command.Status = result.Status.ToString();
            command.AppliedReplicas = result.AppliedReplicas;
            command.Message = result.Message;
            command.ScalerKind = scaler.Kind;
            command.AppliedAt = _timeProvider.GetUtcNow();
            
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            command.Status = WorkerScaleCommandStatus.Failed.ToString();
            command.Message = ex.Message;
            await db.SaveChangesAsync(ct);
        }

        return ToDto(command);
    }

    public async Task<IReadOnlyCollection<WorkerScaleCommandDto>> GetRecentCommandsAsync(int take, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        
        var commands = await db.WorkerScaleCommands
            .OrderByDescending(x => x.RequestedAt)
            .Take(take)
            .ToListAsync(ct);
        
        return commands.Select(ToDto).ToList();
    }

    private static WorkerScaleCommandDto ToDto(WorkerScaleCommandRecord record)
    {
        return new WorkerScaleCommandDto(
            CommandId: record.CommandId,
            WorkerType: record.WorkerType,
            Action: record.Action,
            PreviousDesiredReplicas: record.PreviousDesiredReplicas,
            RequestedDesiredReplicas: record.RequestedDesiredReplicas,
            AppliedReplicas: record.AppliedReplicas,
            Status: Enum.Parse<WorkerScaleCommandStatus>(record.Status),
            Message: record.Message,
            ScalerKind: record.ScalerKind,
            Actor: record.Actor,
            RequestedAt: record.RequestedAt,
            AppliedAt: record.AppliedAt);
    }
}