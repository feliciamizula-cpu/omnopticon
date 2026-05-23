using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.AssetService.Data;
using Argus.AssetService.Stores;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

var JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var argusDbConnectionString = builder.Configuration.GetConnectionString("argusdb");

if (!string.IsNullOrWhiteSpace(argusDbConnectionString))
{
    builder.Services.AddDbContext<AssetDbContext>(options =>
        options.UseNpgsql(argusDbConnectionString));

    builder.Services.AddArgusEfCoreOutbox<AssetDbContext>();
    builder.Services.AddArgusInboxConsumer<AssetDbContext>();

    builder.Services.AddHealthChecks()
        .AddNpgSql(argusDbConnectionString, name: "argusdb", tags: ["db", "sql", "postgres"]);

    builder.Services.AddScoped<IAssetStore, EfAssetStore>();
    builder.Services.AddScoped<TaskCompletedConsumer>();
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Missing required connection string 'argusdb'. In-memory asset storage is allowed only in Development.");
    }

    builder.Services.AddSingleton<IAssetStore, InMemoryAssetStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeAssetStoreAsync();

app.MapDefaultEndpoints();

AssetEndpoints.MapRoutes(app);

app.Run();

internal static class AssetEndpoints
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/assets", (
            Guid? programId,
            AssetType? type,
            AssetStatus? status,
            string? search,
            string? tag,
            int? minInterestingScore,
            int? minRiskScore,
            string? sort,
            string? direction,
            int? page,
            int? pageSize,
            IAssetStore store,
            CancellationToken cancellationToken) =>
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
                MinStalenessScore: null,
                MaxStalenessScore: null,
                Sort: sort,
                Direction: direction,
                Page: safePage,
                PageSize: safePageSize);

            return store.QueryAsync(query, cancellationToken);
        });

        app.MapPost("/assets", async (
            CreateAssetRequest request,
            IAssetStore store,
            IIntegrationEventPublisher events,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
            {
                return Results.BadRequest("Asset value is required.");
            }

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
        });

        app.MapGet("/assets/{assetId:guid}", async (
            Guid assetId,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            var asset = await store.FindAsync(assetId, cancellationToken);
            return asset is not null ? Results.Ok(asset) : Results.NotFound();
        });

        app.MapGet("/assets/{assetId:guid}/relationships", (
            Guid assetId,
            IAssetStore store,
            CancellationToken cancellationToken) =>
            store.GetRelationshipsAsync(assetId, cancellationToken));

        app.MapGet("/assets/{assetId:guid}/subgraph", async (
            Guid assetId,
            int? maxDepth,
            string? assetTypes,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyCollection<AssetType>? types = null;

            if (!string.IsNullOrWhiteSpace(assetTypes))
            {
                var parts = assetTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var parsed = new List<AssetType>(parts.Length);

                foreach (var part in parts)
                {
                    if (!Enum.TryParse<AssetType>(part, ignoreCase: true, out var type))
                    {
                        return Results.BadRequest($"Invalid asset type: '{part}'.");
                    }

                    parsed.Add(type);
                }

                types = parsed;
            }

            return Results.Ok(await store.GetSubgraphAsync(assetId, maxDepth, types, cancellationToken));
        });

        app.MapPost("/assets/relationships", async (
            CreateAssetRelationshipRequest request,
            IAssetStore store,
            IIntegrationEventPublisher events,
            CancellationToken cancellationToken) =>
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
        });

        app.MapPatch("/assets/{assetId:guid}/status", async (
            Guid assetId,
            UpdateAssetStatusRequest request,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            var asset = await store.UpdateStatusAsync(assetId, request.Status, cancellationToken);
            return Results.Ok(asset);
        });

        app.MapPost("/assets/{assetId:guid}/tags", async (
            Guid assetId,
            AddAssetTagsRequest request,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            var asset = await store.AddTagsAsync(assetId, request.Tags, cancellationToken);
            return Results.Ok(asset);
        });

        app.MapDelete("/assets/{assetId:guid}/tags/{tag}", async (
            Guid assetId,
            string tag,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            var asset = await store.RemoveTagAsync(assetId, tag, cancellationToken);
            return Results.Ok(asset);
        });

        app.MapPost("/assets/bulk/tag", async (
            BulkTagRequest request,
            IAssetStore store,
            CancellationToken cancellationToken) =>
        {
            if (request.AssetIds.Count == 0)
            {
                return Results.BadRequest("At least one asset ID is required.");
            }

            if (request.Tags.Count == 0)
            {
                return Results.BadRequest("At least one tag is required.");
            }

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

            return Results.Ok(new
            {
                UpdatedAssets = updatedAssets,
                UpdatedCount = updatedAssets.Count,
                ErrorCount = errors.Count,
                Errors = errors
            });
        });

        app.MapPost("/assets/bulk/enqueue", async (
            BulkEnqueueRequest request,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (request.AssetIds.Count == 0)
            {
                return Results.BadRequest("At least one asset ID is required.");
            }

            if (string.IsNullOrWhiteSpace(request.TaskType))
            {
                return Results.BadRequest("Task type is required.");
            }

            if (string.IsNullOrWhiteSpace(request.WorkerCapability))
            {
                return Results.BadRequest("Worker capability is required.");
            }

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var taskClient = httpClientFactory.CreateClient();
            taskClient.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_TASK_SERVICE", "http://task-service"));

            var createdCount = 0;
            var results = new List<ReconTaskDto>();

            foreach (var assetId in request.AssetIds)
            {
                var dedupeHash = TaskDedupeHash.Compute(request.ProgramId, request.ScopeId, request.TaskType, assetId, request.WorkerCapability);

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
                    DedupeHash: dedupeHash);

                using var createResponse = await taskClient.PostAsJsonAsync("/tasks", createRequest, jsonOptions, cancellationToken);

                if (createResponse.IsSuccessStatusCode)
                {
                    var createdTask = await createResponse.Content.ReadFromJsonAsync<ReconTaskDto>(cancellationToken: cancellationToken);
                    if (createdTask is not null)
                    {
                        createdCount++;
                        results.Add(createdTask);
                    }
                }
            }

            return Results.Ok(new BulkEnqueueResponse(results.ToArray(), createdCount, 0));
        });
    }
}

internal static class TaskDedupeHash
{
    public static string Compute(Guid programId, Guid? scopeId, string taskType, Guid inputAssetId, string workerCapability)
    {
        var input = $"{programId:N}:{scopeId:N}:{taskType}:{inputAssetId:N}:{workerCapability}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

internal static class ServiceUriHelper
{
    public static string GetServiceUri(string configKey, string fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(configKey);

        if (!string.IsNullOrWhiteSpace(envValue) && Uri.TryCreate(envValue, UriKind.Absolute, out var uri))
        {
            return uri.ToString();
        }

        return fallback;
    }
}

internal sealed class TaskCompletedConsumer : IIntegrationEventConsumer<TaskCompleted>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TaskCompletedConsumer> _logger;

    public TaskCompletedConsumer(IHttpClientFactory httpClientFactory, ILogger<TaskCompletedConsumer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task HandleAsync(IntegrationEventEnvelope<TaskCompleted> envelope, CancellationToken cancellationToken)
    {
        if (!envelope.Payload.InputAssetId.HasValue)
        {
            return;
        }

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

        try
        {
            using var response = await client.PatchAsync($"/assets/{envelope.Payload.InputAssetId}/last-scanned", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Updated LastScannedAt for asset {AssetId} after task {TaskId} completed",
                    envelope.Payload.InputAssetId,
                    envelope.Payload.TaskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastScannedAt for asset {AssetId}", envelope.Payload.InputAssetId);
        }
    }
}

internal static class AssetStoreInitialization
{
    public static async Task InitializeAssetStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<AssetDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}

public sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);

public sealed record BulkOperationResult(int SuccessCount, int FailureCount, IReadOnlyList<string> Errors);
