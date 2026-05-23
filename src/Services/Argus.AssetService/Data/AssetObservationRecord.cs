using System.Text.Json;
using Argus.Contracts.Assets;

namespace Argus.AssetService.Data;

public sealed class AssetObservationRecord
{
    public Guid ObservationId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? TaskRunId { get; set; }
    public string? WorkerType { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string ObservationType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string DataJson { get; set; } = "{}";

    public IDictionary<string, object>? Data =>
        string.IsNullOrEmpty(DataJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(DataJson);
}

public sealed class AssetTypeDefinitionRecord
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AssetCategory Category { get; set; }
    public string? Subtype { get; set; }
    public bool IsEnabled { get; set; }
    public decimal ConfidenceWeight { get; set; } = 1.0m;
    public int InterestingScoreBase { get; set; }
    public string? MetdataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public IDictionary<string, object>? Metadata =>
        string.IsNullOrEmpty(MetdataJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(MetdataJson);
}