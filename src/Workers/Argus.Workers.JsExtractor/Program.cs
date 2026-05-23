using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<JsEndpointExtractorWorker>();

await builder.Build().RunAsync();

internal sealed class JsEndpointExtractorWorker : IReconWorker
{
    public WorkerCapabilityDescriptor Capability { get; } = new(
        "JsEndpointExtractorWorker",
        ["JavaScriptFile"],
        ["ApiEndpoint", "Url", "FindingCandidate"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 30);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var jsUrl = WorkerPayload.GetString(task.InputPayloadJson, "url") ?? "https://example.com/static/app.js";
        var host = new Uri(jsUrl).Host;

        await context.ReportProgressAsync(20, $"Checking fetch quota for {host}", null);
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerPayload.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { jsUrl, delayed = true }), []);
        }

        await context.ReportProgressAsync(65, $"Extracting endpoint candidates from {jsUrl}", "{\"scanner\":\"regex\"}");
        await Task.Delay(100, cancellationToken);

        var baseUri = new Uri(jsUrl);
        var assets = new[]
        {
            new WorkerProducedAsset("ApiEndpoint", new Uri(baseUri, "/graphql").ToString(), "GraphQL", null, new Dictionary<string, string> { ["source"] = "javascript" }, Tags: null, ArtifactReferences: null),
            new WorkerProducedAsset("ApiEndpoint", new Uri(baseUri, "/api/internal/users").ToString(), "REST", null, new Dictionary<string, string> { ["source"] = "javascript" }, Tags: null, ArtifactReferences: null),
            new WorkerProducedAsset("FindingCandidate", "possible api key literal in app.js", "PossibleSecret", null, new Dictionary<string, string> { ["source"] = jsUrl }, Tags: null, ArtifactReferences: null)
        };

        return new WorkerProcessResult(false, JsonSerializer.Serialize(new { jsUrl, produced = assets.Length }), assets);
    }
}

internal static class WorkerPayload
{
    public static string? GetString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    public static string GetRegisteredDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? host : string.Join('.', parts[^2..]);
    }
}
