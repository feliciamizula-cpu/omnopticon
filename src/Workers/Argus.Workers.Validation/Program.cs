using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.ServiceDefaults;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.Workers.Validation");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<FindingCandidatePromotionConsumer>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapPost("/validation/{assetId:guid}/promote", async (
    Guid assetId,
    PromoteFindingRequest request,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

    var assetResponse = await client.GetAsync($"/assets/{assetId}", cancellationToken);
    if (!assetResponse.IsSuccessStatusCode)
    {
        return Results.Problem($"FindingCandidate {assetId} not found.", statusCode: 404);
    }

    var candidate = await assetResponse.Content.ReadFromJsonAsync<AssetDto>(cancellationToken: cancellationToken);
    if (candidate is null)
    {
        return Results.Problem("Failed to deserialize candidate.", statusCode: 500);
    }

    var metadata = new Dictionary<string, string>(candidate.Metadata ?? new Dictionary<string, string>())
    {
        ["severity"] = request.Severity,
        ["cvss_score"] = request.CvssScore.ToString("F1"),
        ["cvss_vector"] = request.CvssVector ?? "",
        ["promoted_by"] = request.PromotedBy ?? "manual",
        ["promoted_at"] = DateTimeOffset.UtcNow.ToString("O"),
        ["finding_type"] = request.FindingType ?? "unknown",
        ["promoted_from_candidate_id"] = assetId.ToString()
    };

    var createRequest = new CreateAssetRequest(
        candidate.ProgramId,
        candidate.ScopeId,
        AssetType.Finding,
        candidate.Value,
        candidate.Subtype,
        candidate.Confidence,
        candidate.DiscoveredByTaskId,
        metadata,
        [.. request.Tags ?? [], "finding", $"severity:{request.Severity}"]);

    using var createResponse = await client.PostAsJsonAsync("/assets", createRequest, cancellationToken);
    if (!createResponse.IsSuccessStatusCode)
    {
        var body = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        return Results.Problem($"Failed to create Finding asset: {body}", statusCode: 500);
    }

    var findingAsset = await createResponse.Content.ReadFromJsonAsync<AssetDto>(cancellationToken: cancellationToken);

    var archiveStatus = new UpdateAssetStatusRequest(AssetStatus.Archived, $"Promoted to Finding {findingAsset?.AssetId}");
    using var archiveResponse = await client.PatchAsJsonAsync($"/assets/{assetId}/status", archiveStatus, cancellationToken);
    if (!archiveResponse.IsSuccessStatusCode)
    {
        return Results.Problem("Failed to archive original FindingCandidate.", statusCode: 500);
    }

    return Results.Ok(new
    {
        OriginalCandidateId = assetId,
        FindingAssetId = findingAsset?.AssetId,
        Status = "Promoted",
        Severity = request.Severity
    });
});

app.MapPost("/validation/{assetId:guid}/dismiss", async (
    Guid assetId,
    DismissFindingRequest request,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var client = httpClientFactory.CreateClient();
    client.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

    var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Dismissed by manual review" : request.Reason;

    var updateStatus = new UpdateAssetStatusRequest(AssetStatus.Archived, reason);
    using var statusResponse = await client.PatchAsJsonAsync($"/assets/{assetId}/status", updateStatus, cancellationToken);
    if (!statusResponse.IsSuccessStatusCode)
    {
        return Results.Problem("Failed to dismiss FindingCandidate.", statusCode: 500);
    }

    var addTags = new AddAssetTagsRequest(["dismissed", "archived"]);
    using var tagsResponse = await client.PostAsJsonAsync($"/assets/{assetId}/tags", addTags, cancellationToken);
    if (!tagsResponse.IsSuccessStatusCode)
    {
        return Results.Problem("Failed to add tags.", statusCode: 500);
    }

    return Results.Ok(new { AssetId = assetId, Status = "Dismissed", Reason = reason });
});

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Argus.Workers.Validation" }));

app.Run();

public sealed record PromoteFindingRequest(
    string Severity,
    double CvssScore,
    string? CvssVector,
    string? FindingType,
    string? PromotedBy,
    IReadOnlyCollection<string>? Tags);

public sealed record DismissFindingRequest(string? Reason);

internal sealed class FindingCandidatePromotionConsumer(
    IHttpClientFactory httpClientFactory,
    ILogger<FindingCandidatePromotionConsumer> logger) : IIntegrationEventConsumer<FindingCandidateCreated>
{
    public async Task HandleAsync(IntegrationEventEnvelope<FindingCandidateCreated> envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;

        logger.LogInformation("Processing FindingCandidateCreated: AssetId={AssetId}, Value={Value}, Score={Score}",
            payload.AssetId, payload.Value, payload.InterestingScore);

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

        var addTags = new AddAssetTagsRequest(["pending-review", $"score:{payload.InterestingScore}"]);
        using var tagsResponse = await client.PostAsJsonAsync($"/assets/{payload.AssetId}/tags", addTags, cancellationToken);
        if (tagsResponse.IsSuccessStatusCode)
        {
            logger.LogDebug("Added pending-review tags to FindingCandidate {AssetId}", payload.AssetId);
        }
    }
}

internal static class ServiceUriHelper
{
    public static string GetServiceUri(string configKey, string fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(configKey);
        if (!string.IsNullOrWhiteSpace(envValue) && Uri.TryCreate(envValue, UriKind.Absolute, out var uri))
            return uri.ToString();
        return fallback;
    }
}
