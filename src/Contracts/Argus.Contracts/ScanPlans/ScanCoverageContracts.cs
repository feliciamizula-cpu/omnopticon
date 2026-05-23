namespace Argus.Contracts.ScanPlans;

public record TaskCoverageEntry(
    string TaskType,
    string WorkerCapability,
    bool IsCreated,
    Guid? TaskId,
    string? TaskState,
    int? ProgressPercent);

public record ScanCoverageReport(
    Guid ScanPlanId,
    string Target,
    string State,
    string WorkflowType,
    DateTimeOffset CreatedAt,
    int TotalPlannedTasks,
    int CreatedTasks,
    double CoveragePercent,
    List<TaskCoverageEntry> TaskDetails);