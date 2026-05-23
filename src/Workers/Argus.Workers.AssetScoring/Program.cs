using Argus.BuildingBlocks.EventBus;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AssetScoringWorker>();

await builder.Build().RunAsync();

internal sealed class AssetScoringWorker : IReconWorker, IIntegrationEventConsumer<AssetDiscovered>
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "AssetScoringWorker",
        ["Domain", "Subdomain", "Url", "ApiEndpoint", "JavaScriptFile", "Technology"],
        ["Observation"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 100);

    public Task HandleAsync(IntegrationEventEnvelope<AssetDiscovered> eventEnvelope, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

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

        var observations = ComputeObservations(value, assetType, targetId, programId, task.TaskId, task.InputAssetId);

        await context.ReportProgressAsync(80, $"Generated {observations.Length} observations", null);

        return new WorkerProcessResult(
            PartiallySucceeded: false,
            OutputSummaryJson: JsonSerializer.Serialize(new
            {
                assetValue = value,
                assetType,
                observationCount = observations.Length,
                highestScore = observations.Length > 0 ? observations.Max(o => o.Score) : 0
            }),
            ProducedAssets: observations.Select(o => new WorkerProducedAsset(
                AssetType: "Observation",
                Value: o.Value,
                Subtype: o.Subtype,
                Confidence: null,
                Metadata: o.Metadata,
                Tags: o.Tags,
                ArtifactReferences: null
            )).ToArray());
    }

    private static ScoringObservation[] ComputeObservations(string value, string assetType, Guid targetId, Guid programId, Guid taskId, Guid? parentAssetId)
    {
        var observations = new List<ScoringObservation>();
        var signal = DetermineSignal(value, assetType);

        if (signal.Score >= 50)
        {
            observations.Add(new ScoringObservation(
                signal.Description,
                signal.Subtype,
                signal.Score,
                new Dictionary<string, string>
                {
                    ["source"] = "asset-scoring",
                    ["signal"] = signal.Name,
                    ["taskId"] = taskId.ToString(),
                    ["targetId"] = targetId.ToString(),
                    ["programId"] = programId.ToString(),
                    ["parentAssetId"] = parentAssetId?.ToString() ?? ""
                },
                signal.Tags));
        }

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && signal.Score < 70)
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

        return observations.ToArray();
    }

    private static SignalInfo DetermineSignal(string value, string assetType)
    {
        if (value.Contains("graphql", StringComparison.OrdinalIgnoreCase) || value.Contains("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("graphql-detected", "GraphQLEndpoint", $"GraphQL endpoint found: {value}", 85,
                ["graphql", "api", "scored", "interesting-asset"]);
        }

        if (value.Contains("swagger", StringComparison.OrdinalIgnoreCase)
            || value.Contains("openapi", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api-docs", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/v2/api-docs", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("api-docs-detected", "ApiDocumentation", $"API documentation endpoint: {value}", 70,
                ["api-docs", "swagger", "scored", "interesting-asset"]);
        }

        if (value.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/admin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("admin-panel-detected", "AdminPanel", $"Potential admin panel: {value}", 80,
                ["admin", "scored", "interesting-asset"]);
        }

        if (value.Contains("jenkins", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ci", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gitlab", StringComparison.OrdinalIgnoreCase)
            || value.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("dev-tool-detected", "DevTool", $"Development tool endpoint: {value}", 75,
                ["dev-tool", "scored", "interesting-asset"]);
        }

        if (value.Contains("internal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("intranet", StringComparison.OrdinalIgnoreCase)
            || value.Contains("corp", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("internal-endpoint-detected", "InternalEndpoint", $"Potential internal endpoint: {value}", 78,
                ["internal", "scored", "interesting-asset"]);
        }

        if (value.Contains("debug", StringComparison.OrdinalIgnoreCase)
            || value.Contains("trace", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".env", StringComparison.OrdinalIgnoreCase)
            || value.Contains("phpinfo", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("debug-info-detected", "DebugEndpoint", $"Potential debug/misconfiguration: {value}", 88,
                ["debug", "misconfiguration", "scored", "interesting-asset"]);
        }

        if (value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reset", StringComparison.OrdinalIgnoreCase)
            || value.Contains("login", StringComparison.OrdinalIgnoreCase)
            || value.Contains("signin", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalInfo("auth-endpoint-detected", "AuthEndpoint", $"Authentication endpoint: {value}", 60,
                ["auth", "scored", "interesting-asset"]);
        }

        if (assetType.Equals("JavaScriptFile", StringComparison.OrdinalIgnoreCase))
        {
            var score = 30 + (value.Contains(".min.", StringComparison.OrdinalIgnoreCase) ? 0 : 20);
            return new SignalInfo("js-file-detected", "JsFile", $"JavaScript file analyzed: {value}", score,
                ["javascript", "scored"]);
        }

        return new SignalInfo("generic-asset", "GenericObservation", $"Asset observed: {value}", 20, ["scored"]);
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

    private record SignalInfo(
        string Name,
        string Subtype,
        string Description,
        int Score,
        string[] Tags);
}