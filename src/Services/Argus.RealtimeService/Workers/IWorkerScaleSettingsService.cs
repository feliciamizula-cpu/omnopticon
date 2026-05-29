using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public interface IWorkerScaleSettingsService
{
    Task<WorkerTypeScaleSettingsDto> GetOrCreateAsync(string workerType, CancellationToken ct);
    Task<WorkerTypeScaleSettingsDto> UpdateAsync(
        string workerType,
        UpdateWorkerScaleSettingsRequest request,
        string? actor,
        CancellationToken ct);
}