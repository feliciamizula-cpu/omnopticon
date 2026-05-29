using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public interface IWorkerScaleCommandService
{
    Task<WorkerScaleCommandDto> ScaleAsync(
        string workerType,
        int desiredReplicas,
        string action,
        string? reason,
        string? actor,
        CancellationToken ct);

    Task<IReadOnlyCollection<WorkerScaleCommandDto>> GetRecentCommandsAsync(
        int take,
        CancellationToken ct);
}