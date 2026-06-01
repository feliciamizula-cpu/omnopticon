namespace Argus.BuildingBlocks.Workers;

public sealed class ArgusWorkerOptions
{
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    public bool EventDrivenMode { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan TaskTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public Uri TaskServiceBaseAddress { get; set; } = new("http://task-service");
    public Uri AssetServiceBaseAddress { get; set; } = new("http://asset-service");
    public Uri ArtifactServiceBaseAddress { get; set; } = new("http://artifact-service");
    public Uri ScopeServiceBaseAddress { get; set; } = new("http://program-scope-service");
    public Uri RateLimitServiceBaseAddress { get; set; } = new("http://rate-limit-service");
    public Uri RealtimeServiceBaseAddress { get; set; } = new("http://realtime-service");
    public bool ScopeValidationRequired { get; set; }
    public string SnapshotSecretKey { get; set; } = string.Empty;
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public bool SaveCheckpointOnShutdown { get; set; } = true;
    public int MaxConcurrencyOverride { get; set; }
    public int SubscriptionConcurrencyLimit { get; set; }
    public string EventBusConnectionString { get; set; } = string.Empty;
    public string EventExchangeName { get; set; } = "argus.integration.events";
}
