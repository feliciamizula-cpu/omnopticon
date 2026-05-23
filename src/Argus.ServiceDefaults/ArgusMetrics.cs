using System.Diagnostics.Metrics;

namespace Argus.ServiceDefaults;

public sealed class ArgusMetrics
{
    private readonly Counter<long> _tasksProcessed;
    private readonly Counter<long> _tasksFailed;
    private readonly Counter<long> _assetsProduced;
    private readonly Counter<long> _rateLimitHits;
    private readonly Histogram<double> _taskDuration;

    public ArgusMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Argus.Recon");

        _tasksProcessed = meter.CreateCounter<long>(
            "argus.tasks.processed",
            description: "Total number of tasks processed");

        _tasksFailed = meter.CreateCounter<long>(
            "argus.tasks.failed",
            description: "Total number of tasks that failed");

        _assetsProduced = meter.CreateCounter<long>(
            "argus.assets.produced",
            description: "Total number of assets produced by workers");

        _rateLimitHits = meter.CreateCounter<long>(
            "argus.rate_limit.hits",
            description: "Total number of rate limit hits");

        _taskDuration = meter.CreateHistogram<double>(
            "argus.task.duration",
            unit: "ms",
            description: "Task processing duration in milliseconds");
    }

    public void RecordTaskProcessed(Guid programId, string workerType, bool partiallySucceeded)
    {
        _tasksProcessed.Add(1,
            new KeyValuePair<string, object?>("program_id", programId.ToString()),
            new KeyValuePair<string, object?>("worker_type", workerType),
            new KeyValuePair<string, object?>("result", partiallySucceeded ? "partial" : "success"));
    }

    public void RecordTaskFailed(Guid programId, string workerType, string errorType)
    {
        _tasksFailed.Add(1,
            new KeyValuePair<string, object?>("program_id", programId.ToString()),
            new KeyValuePair<string, object?>("worker_type", workerType),
            new KeyValuePair<string, object?>("error_type", errorType));
    }

    public void RecordAssetProduced(Guid programId, string workerType, string assetType)
    {
        _assetsProduced.Add(1,
            new KeyValuePair<string, object?>("program_id", programId.ToString()),
            new KeyValuePair<string, object?>("worker_type", workerType),
            new KeyValuePair<string, object?>("asset_type", assetType));
    }

    public void RecordRateLimitHit(Guid programId, string workerType, string host)
    {
        _rateLimitHits.Add(1,
            new KeyValuePair<string, object?>("program_id", programId.ToString()),
            new KeyValuePair<string, object?>("worker_type", workerType),
            new KeyValuePair<string, object?>("host", host));
    }

    public void RecordTaskDuration(Guid programId, string workerType, double durationMs)
    {
        _taskDuration.Record(durationMs,
            new KeyValuePair<string, object?>("program_id", programId.ToString()),
            new KeyValuePair<string, object?>("worker_type", workerType));
    }
}