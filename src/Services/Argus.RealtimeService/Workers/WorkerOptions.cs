namespace Argus.RealtimeService.Workers;

public sealed class WorkerOptions
{
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public double SaturationThresholdPercent { get; set; } = 90.0;
    public string ScalerKind { get; set; } = "NoOp";
    public string? KubernetesNamespace { get; set; }
    public string? DeploymentNameSuffix { get; set; }
}