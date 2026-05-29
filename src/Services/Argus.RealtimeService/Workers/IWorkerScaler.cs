using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public interface IWorkerScaler
{
    string Kind { get; }

    Task<WorkerScalerResult> ScaleAsync(
        WorkerScaleTarget target,
        CancellationToken ct);
}

public sealed record WorkerScaleTarget(
    string WorkerType,
    string DeploymentName,
    string? Namespace,
    int DesiredReplicas);

public sealed record WorkerScalerResult(
    WorkerScaleCommandStatus Status,
    int? AppliedReplicas,
    string? Message);