using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public interface IWorkerSummaryService
{
    Task<WorkersPageSummaryDto> GetSummaryAsync(CancellationToken ct);
    Task<WorkerTypeSummaryDto?> GetWorkerTypeAsync(string workerType, CancellationToken ct);
    Task<IReadOnlyCollection<WorkerInstanceDto>> GetInstancesAsync(string workerType, CancellationToken ct);
}