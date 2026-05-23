using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.AssetService.Data;
using Argus.AssetService.Stores;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Argus.BuildingBlocks.EventBus;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Argus.AssetService.Endpoints;

public static class AssetEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/assets", GetAssets);
        app.MapPost("/assets", CreateAsset);
        app.MapPost("/assets/search", SearchAssets);
        app.MapGet("/assets/{assetId:guid}", GetAsset);
        app.MapGet("/assets/{assetId:guid}/relationships", GetRelationships);
        app.MapGet("/assets/{assetId:guid}/subgraph", GetSubgraph);
        app.MapGet("/assets/{assetId:guid}/lineage", GetLineage);
        app.MapGet("/assets/{assetId:guid}/related", GetRelated);
        app.MapGet("/assets/{assetId:guid}/observations", GetObservations);
        app.MapPost("/assets/relationships", CreateRelationship);
        app.MapPatch("/assets/{assetId:guid}", UpdateAsset);
        app.MapPatch("/assets/{assetId:guid}/status", UpdateAssetStatus);
        app.MapPost("/assets/{assetId:guid}/verify", VerifyAsset);
        app.MapPost("/assets/{assetId:guid}/reject", RejectAsset);
        app.MapPost("/assets/{assetId:guid}/mark-high-value", MarkHighValue);
        app.MapPost("/assets/{assetId:guid}/tags", AddTags);
        app.MapDelete("/assets/{assetId:guid}/tags/{tag}", RemoveTag);
        app.MapPost("/assets/bulk", BulkOperation);
        app.MapPost("/assets/bulk/tag", BulkTag);
        app.MapPost("/assets/bulk/enqueue", BulkEnqueue);

        app.MapGet("/asset-types", GetAssetTypes);
        app.MapGet("/asset-types/{typeKey}", GetAssetType);
        app.MapPost("/asset-types", CreateAssetType);
        app.MapPatch("/asset-types/{typeKey}", UpdateAssetType);
        app.MapPost("/asset-types/{typeKey}/enable", EnableAssetType);
        app.MapPost("/asset-types/{typeKey}/disable", DisableAssetType);
        app.MapGet("/asset-types/{typeKey}/usage", GetAssetTypeUsage);
    }

    private static async Task<IResult> GetAssets(
        Guid? programId,
        AssetType? type,
        AssetStatus? status,
        string? search,
        string? tag,
        int? minInterestingScore,
        int? minRiskScore,
        int? minStalenessScore,
        int? maxStalenessScore,
        string? sort,
        string? direction,
        int? page,
        int? pageSize,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(page ?? 1, 1);
        var safePageSize = Math.Clamp(pageSize ?? 100, 1, 500);
        var query = new AssetQuery(
            ProgramId: programId,
            Type: type,
            Status: status,
            Search: search,
            Tag: tag,
            MinInterestingScore: minInterestingScore,
            MinRiskScore: minRiskScore,
            MinStalenessScore: minStalenessScore,
            MaxStalenessScore: maxStalenessScore,
            Sort: sort,
            Direction: direction,
            Page: safePage,
            PageSize: safePageSize);
        return Results.Ok(await store.QueryAsync(query, cancellationToken));
    }

    private static async Task<IResult> CreateAsset(
        CreateAssetRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
            return Results.BadRequest("Asset value is required.");

        var result = await store.UpsertAsync(request, cancellationToken);
        var asset = result.Asset;
        var eventType = result.WasCreated ? nameof(AssetDiscovered) : nameof(AssetUpdated);

        if (result.WasCreated)
        {
            await events.PublishAsync(
                new AssetDiscovered(asset.AssetId, asset.ProgramId, asset.Type.ToString(), asset.Value),
                eventType,
                "Argus.AssetService",
                cancellationToken: cancellationToken);

            if (asset.Type == AssetType.FindingCandidate)
            {
                await events.PublishAsync(
                    new FindingCandidateCreated(asset.AssetId, asset.ProgramId, asset.Type.ToString(), asset.Value, asset.InterestingScore),
                    nameof(FindingCandidateCreated),
                    "Argus.AssetService",
                    cancellationToken: cancellationToken);
            }
        }
        else
        {
            await events.PublishAsync(
                new AssetUpdated(asset.AssetId, asset.ProgramId, asset.Type.ToString(), asset.Value),
                eventType,
                "Argus.AssetService",
                cancellationToken: cancellationToken);
        }

        return Results.Created($"/assets/{asset.AssetId}", asset);
    }

    private static async Task<IResult> SearchAssets(
        [FromBody] AssetSearchRequest request,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        if (request.ProgramId == Guid.Empty)
            request = request with { ProgramId = null };

        return Results.Ok(await store.SearchAsync(request, cancellationToken));
    }

    private static async Task<IResult> GetAsset(
        Guid assetId,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        var asset = await store.FindAsync(assetId, cancellationToken);
        return asset is not null ? Results.Ok(asset) : Results.NotFound();
    }

    private static async Task<IResult> GetRelationships(
        Guid assetId,
        IAssetStore store,
        CancellationToken cancellationToken)
        => Results.Ok(await store.GetRelationshipsAsync(assetId, cancellationToken));

    private static async Task<IResult> GetSubgraph(
        Guid assetId,
        int? maxDepth,
        string? assetTypes,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<AssetType>? types = null;
        if (!string.IsNullOrWhiteSpace(assetTypes))
        {
            var parts = assetTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<AssetType>(parts.Length);
            foreach (var part in parts)
            {
                if (!Enum.TryParse<AssetType>(part, ignoreCase: true, out var type))
                    return Results.BadRequest($"Invalid asset type: '{part}'.");
                parsed.Add(type);
            }
            types = parsed;
        }

        return Results.Ok(await store.GetSubgraphAsync(assetId, maxDepth, types, cancellationToken));
    }

    private static async Task<IResult> GetLineage(
        Guid assetId,
        string direction,
        int depth,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(direction))
            direction = "both";

        if (direction.ToLowerInvariant() is not ("parents" or "children" or "both"))
            return Results.BadRequest("Direction must be 'parents', 'children', or 'both'.");

        if (depth < 1)
            depth = 5;
        else if (depth > 10)
            depth = 10;

        try
        {
            return Results.Ok(await store.GetLineageAsync(assetId, direction, depth, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> GetRelated(
        Guid assetId,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.GetRelatedAsync(assetId, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> GetObservations(
        Guid assetId,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.GetObservationsAsync(assetId, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> CreateRelationship(
        CreateAssetRelationshipRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (!await store.ContainsAsync(request.FromAssetId, cancellationToken)
            || !await store.ContainsAsync(request.ToAssetId, cancellationToken))
        {
            return Results.NotFound("Both assets must exist before a relationship can be created.");
        }

        try
        {
            var relationship = await store.AddRelationshipAsync(request, cancellationToken);
            await events.PublishAsync(
                new AssetRelationshipDiscovered(relationship.FromAssetId, relationship.ToAssetId, relationship.EdgeType),
                nameof(AssetRelationshipDiscovered),
                "Argus.AssetService",
                cancellationToken: cancellationToken);

            return Results.Created($"/assets/{request.FromAssetId}/relationships", relationship);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> UpdateAsset(
        Guid assetId,
        UpdateAssetRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await store.UpdateAsync(assetId, request, cancellationToken);
            await events.PublishAsync(
                new AssetUpdated(updated.AssetId, updated.ProgramId, updated.Type.ToString(), updated.Value),
                nameof(AssetUpdated),
                "Argus.AssetService",
                cancellationToken: cancellationToken);

            return Results.Ok(updated);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> UpdateAssetStatus(
        Guid assetId,
        UpdateAssetStatusRequest request,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.UpdateStatusAsync(assetId, request.Status, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> VerifyAsset(
        Guid assetId,
        VerifyAssetRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        try
        {
            var verified = await store.VerifyAsync(assetId, request.VerificationStatus, request.Notes, cancellationToken);

            await events.PublishAsync(
                new AssetConfirmed(verified.AssetId, verified.ProgramId, verified.Type.ToString(), verified.Value, null),
                nameof(AssetConfirmed),
                "Argus.AssetService",
                cancellationToken: cancellationToken);

            return Results.Ok(verified);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> RejectAsset(
        Guid assetId,
        RejectAssetRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        try
        {
            var rejected = await store.RejectAsync(assetId, request.Reason, cancellationToken);

            await events.PublishAsync(
                new AssetUpdated(rejected.AssetId, rejected.ProgramId, rejected.Type.ToString(), rejected.Value),
                nameof(AssetUpdated),
                "Argus.AssetService",
                cancellationToken: cancellationToken);

            return Results.Ok(rejected);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> MarkHighValue(
        Guid assetId,
        MarkHighValueAssetRequest request,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.MarkHighValueAsync(assetId, request.HighValue, request.Reason, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> AddTags(
        Guid assetId,
        AddAssetTagsRequest request,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.AddTagsAsync(assetId, request.Tags, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> RemoveTag(
        Guid assetId,
        string tag,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.RemoveTagAsync(assetId, tag, cancellationToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> BulkOperation(
        [FromBody] AssetBulkActionRequest request,
        IAssetStore store,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (request.AssetIds.Count == 0)
            return Results.BadRequest("At least one asset ID is required.");

        var result = await store.BulkOperationAsync(request, cancellationToken);

        foreach (var asset in result.Results)
        {
            await events.PublishAsync(
                new AssetUpdated(asset.AssetId, asset.ProgramId, asset.Type.ToString(), asset.Value),
                nameof(AssetUpdated),
                "Argus.AssetService",
                cancellationToken: cancellationToken);
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> BulkTag(
        BulkTagRequest request,
        IAssetStore store,
        CancellationToken cancellationToken)
    {
        if (request.AssetIds.Count == 0)
            return Results.BadRequest("At least one asset ID is required.");

        if (request.Tags.Count == 0)
            return Results.BadRequest("At least one tag is required.");

        var updatedAssets = new List<AssetDto>();
        var errors = new List<string>();

        foreach (var assetId in request.AssetIds)
        {
            try
            {
                var asset = await store.AddTagsAsync(assetId, request.Tags, cancellationToken);
                updatedAssets.Add(asset);
            }
            catch (InvalidOperationException)
            {
                errors.Add($"Asset {assetId} not found.");
            }
        }

        return Results.Ok(new { UpdatedAssets = updatedAssets, UpdatedCount = updatedAssets.Count, ErrorCount = errors.Count, Errors = errors });
    }

    private static async Task<IResult> BulkEnqueue(
        BulkEnqueueRequest request,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (request.AssetIds.Count == 0)
            return Results.BadRequest("At least one asset ID is required.");

        if (string.IsNullOrWhiteSpace(request.TaskType))
            return Results.BadRequest("Task type is required.");

        if (string.IsNullOrWhiteSpace(request.WorkerCapability))
            return Results.BadRequest("Worker capability is required.");

        var taskClient = httpClientFactory.CreateClient();
        taskClient.BaseAddress = new Uri(GetServiceUri("ARGUS_TASK_SERVICE", "http://task-service"));

        var createdCount = 0;
        var skippedCount = 0;
        var results = new List<ReconTaskDto>();

        foreach (var assetId in request.AssetIds)
        {
            var createRequest = new CreateReconTaskRequest(
                TaskType: request.TaskType,
                ProgramId: request.ProgramId,
                ScopeId: request.ScopeId,
                InputAssetId: assetId,
                InputPayloadJson: null,
                WorkerCapability: request.WorkerCapability,
                RequiredAssetType: null,
                MaxAttempts: request.MaxAttempts,
                Priority: request.Priority,
                DedupeHash: ComputeTaskDedupeHash(request.ProgramId, request.ScopeId, request.TaskType, assetId, request.WorkerCapability));

            using var createResponse = await taskClient.PostAsJsonAsync("/tasks", createRequest, JsonOptions, cancellationToken);
            if (createResponse.IsSuccessStatusCode)
            {
                var createdTask = await createResponse.Content.ReadFromJsonAsync<ReconTaskDto>(JsonOptions, cancellationToken: cancellationToken);
                if (createdTask is not null)
                {
                    createdCount++;
                    results.Add(createdTask);
                }
                else
                {
                    skippedCount++;
                }
            }
            else
            {
                skippedCount++;
            }
        }

        return Results.Ok(new BulkEnqueueResponse(results.ToArray(), createdCount, skippedCount));
    }

    private static async Task<IResult> GetAssetTypes(
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var types = await dbContext.AssetTypeDefinitions
            .AsNoTracking()
            .OrderBy(t => t.DisplayName)
            .ToArrayAsync(cancellationToken);

        return Results.Ok(types);
    }

    private static async Task<IResult> GetAssetType(
        string typeKey,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var type = await dbContext.AssetTypeDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TypeKey == typeKey, cancellationToken);

        return type is not null ? Results.Ok(type) : Results.NotFound();
    }

    private static async Task<IResult> CreateAssetType(
        [FromBody] AssetTypeDefinitionRecord request,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TypeKey))
            return Results.BadRequest("Type key is required.");

        if (await dbContext.AssetTypeDefinitions.AnyAsync(t => t.TypeKey == request.TypeKey, cancellationToken))
            return Results.Conflict($"Asset type '{request.TypeKey}' already exists.");

        request.CreatedAt = DateTimeOffset.UtcNow;
        dbContext.AssetTypeDefinitions.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/asset-types/{request.TypeKey}", request);
    }

    private static async Task<IResult> UpdateAssetType(
        string typeKey,
        [FromBody] AssetTypeDefinitionRecord request,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.AssetTypeDefinitions.FirstOrDefaultAsync(t => t.TypeKey == typeKey, cancellationToken);
        if (existing is null)
            return Results.NotFound();

        existing.DisplayName = request.DisplayName;
        existing.Description = request.Description;
        existing.Category = request.Category;
        existing.Subtype = request.Subtype;
        existing.IsEnabled = request.IsEnabled;
        existing.ConfidenceWeight = request.ConfidenceWeight;
        existing.InterestingScoreBase = request.InterestingScoreBase;
        existing.MetdataJson = request.MetdataJson;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(existing);
    }

    private static async Task<IResult> EnableAssetType(
        string typeKey,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.AssetTypeDefinitions.FirstOrDefaultAsync(t => t.TypeKey == typeKey, cancellationToken);
        if (existing is null)
            return Results.NotFound();

        existing.IsEnabled = true;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(existing);
    }

    private static async Task<IResult> DisableAssetType(
        string typeKey,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.AssetTypeDefinitions.FirstOrDefaultAsync(t => t.TypeKey == typeKey, cancellationToken);
        if (existing is null)
            return Results.NotFound();

        existing.IsEnabled = false;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(existing);
    }

    private static async Task<IResult> GetAssetTypeUsage(
        string typeKey,
        [FromServices] AssetDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var count = await dbContext.Assets
            .CountAsync(a => a.TypeKey == typeKey, cancellationToken);

        return Results.Ok(new { TypeKey = typeKey, AssetCount = count });
    }

    private static string ComputeTaskDedupeHash(Guid programId, Guid? scopeId, string taskType, Guid inputAssetId, string workerCapability)
    {
        var input = $"{programId:N}:{scopeId:N}:{taskType}:{inputAssetId:N}:{workerCapability}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string GetServiceUri(string configKey, string fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(configKey);
        if (!string.IsNullOrWhiteSpace(envValue) && Uri.TryCreate(envValue, UriKind.Absolute, out var uri))
            return uri.ToString();
        return fallback;
    }
}