using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AssetScoringWorker>();

await builder.Build().RunAsync();

internal sealed class AssetScoringWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "AssetScoringWorker",
        ["Domain", "Subdomain", "Url", "ApiEndpoint", "JavaScriptFile", "FindingCandidate", "Technology"],
        ["Observation"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 100);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var value = GetPayloadString(task.InputPayloadJson, "value")
            ?? GetPayloadString(task.InputPayloadJson, "url")
            ?? GetPayloadString(task.InputPayloadJson, "domain")
            ?? "unknown";

        var assetType = GetPayloadString(task.InputPayloadJson, "assetType") ?? "Unknown";
        var programId = task.ProgramId;
        var targetId = GetPayloadGuid(task.InputPayloadJson, "targetId") ?? Guid.Empty;

        await context.ReportProgressAsync(20, $"Analyzing asset: {value}", null);

        var observations = ComputeObservations(value, assetType, targetId, programId, task.TaskId);

        await context.ReportProgressAsync(80, $"Generated {observations.Length} observations", null);

        return new WorkerProcessResult(
            HasMoreWork: false,
            NextTaskPayloadJson: null,
            ProducedAssets: observations.Select(o => new WorkerProducedAsset(
                AssetType: "Observation",
                Value: o.Value,
                Subtype: o.Subtype,
                ParentAssetId: task.InputAssetId,
                Metadata: o.Metadata,
                Tags: o.Tags
            )).ToArray(),
            OutputSummaryJson: JsonSerializer.Serialize(new
            {
                assetValue = value,
                observationCount = observations.Length,
                highestScore = observations.Max(o => o.Score)
            }));
    }

    private static ScoringObservation[] ComputeObservations(string value, string assetType, Guid targetId, Guid programId, Guid taskId)
    {
        var observations = new List<ScoringObservation>();

        var isHighValue = value.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("internal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("graphql", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api", StringComparison.OrdinalIgnoreCase)
            || value.Contains("vpn", StringComparison.OrdinalIgnoreCase)
            || value.Contains("git", StringComparison.OrdinalIgnoreCase)
            || value.Contains("jenkins", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ci", StringComparison.OrdinalIgnoreCase)
            || value.Contains("staging", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dev", StringComparison.OrdinalIgnoreCase)
            || value.Contains("test", StringComparison.OrdinalIgnoreCase);

        if (isHighValue)
        {
            observations.Add(new ScoringObservation(
                $"High-value endpoint detected: {value}",
                "InterestingAsset",
                75,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "high-value-endpoint",
                    ["taskId"] = taskId.ToString()
                },
                ["high-value", "scored", "interesting-asset"]));
        }

        if (value.Contains("graphql", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add(new ScoringObservation(
                $"GraphQL endpoint found: {value}",
                "GraphQLEndpoint",
                85,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "graphql-detected",
                    ["taskId"] = taskId.ToString()
                },
                ["graphql", "api", "scored", "interesting-asset"]));
        }

        if (value.Contains("swagger", StringComparison.OrdinalIgnoreCase)
            || value.Contains("openapi", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api-docs", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add(new ScoringObservation(
                $"API documentation endpoint: {value}",
                "ApiDocumentation",
                70,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "api-docs-detected",
                    ["taskId"] = taskId.ToString()
                },
                ["api-docs", "swagger", "scored", "interesting-asset"]));
        }

        if (assetType.Equals("JavaScriptFile", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add(new ScoringObservation(
                $"JavaScript file analyzed: {value}",
                "JsFile",
                30 + (value.Contains(".min.", StringComparison.OrdinalIgnoreCase) ? 0 : 20),
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "js-file",
                    ["minified"] = value.Contains(".min.", StringComparison.OrdinalIgnoreCase).ToString(),
                    ["taskId"] = taskId.ToString()
                },
                ["javascript", "scored"]));
        }

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add(new ScoringObservation(
                $"HTTPS endpoint: {value}",
                "SecureEndpoint",
                15,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "https-detected",
                    ["taskId"] = taskId.ToString()
                },
                ["https", "scored"]));
        }

        if (value.Contains("debug", StringComparison.OrdinalIgnoreCase)
            || value.Contains("trace", StringComparison.OrdinalIgnoreCase)
            || value.Contains("env", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add(new ScoringObservation(
                $"Potential debug/misconfiguration: {value}",
                "PotentialMisconfiguration",
                80,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = "debug-detected",
                    ["taskId"] = taskId.ToString()
                },
                ["debug", "misconfiguration", "scored", "interesting-asset"]));
        }

        return observations.ToArray();
    }

    private static string? GetPayloadString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(propertyName, out var value))
                return value.GetString();
        }
        catch { }
        return null;
    }

    private static Guid? GetPayloadGuid(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                return Guid.TryParse(value.GetString(), out var guid) ? guid : null;
            if (document.RootElement.TryGetProperty(propertyName, out var value2) && value2.ValueKind == JsonValueKind.Number)
                return Guid.NewGuid();
        }
        catch { }
        return null;
    }

    private record ScoringObservation(
        string Value,
        string Subtype,
        int Score,
        Dictionary<string, string> Metadata,
        string[] Tags);
}