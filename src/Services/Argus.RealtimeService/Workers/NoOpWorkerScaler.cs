using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public sealed class NoOpWorkerScaler : IWorkerScaler
{
    public string Kind => "NoOp";

    public Task<WorkerScalerResult> ScaleAsync(WorkerScaleTarget target, CancellationToken ct)
    {
        return Task.FromResult(new WorkerScalerResult(
            WorkerScaleCommandStatus.NoOp,
            AppliedReplicas: null,
            Message: "Desired replica count recorded. No runtime scaler is configured for this environment."));
    }
}