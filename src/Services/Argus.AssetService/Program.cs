using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

var JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<AssetDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<AssetDbContext>();
    builder.Services.AddArgusInboxConsumer<AssetDbContext>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
    builder.Services.AddScoped<IAssetStore, EfAssetStore>();
    builder.Services.AddScoped<TaskCompletedConsumer>();
}
else
{
    builder.Services.AddSingleton<IAssetStore, InMemoryAssetStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeAssetStoreAsync();
app.MapDefaultEndpoints();

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
    var query = new AssetQuery(ProgramId: programId, Type: type, Status: status, Search: search, Tag: tag, MinInterestingScore: minInterestingScore, MinRiskScore: minRiskScore, MinStalenessScore: null, MaxStalenessScore: null, Sort: sort, Direction: direction, Page: page ?? 1, PageSize: pageSize ?? 100);
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

    return Results.Ok(new { UpdatedAssets = updatedAssets, UpdatedCount = updatedAssets.Count, ErrorCount = errors.Count, Errors = errors });
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

    var taskClient = httpClientFactory.CreateClient();
    taskClient.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_TASK_SERVICE", "http://task-service"));

    var createdCount = 0;
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
            DedupeHash: null);

        using var createResponse = await taskClient.PostAsJsonAsync("/tasks", createRequest, JsonOptions, cancellationToken);
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

app.Run();

internal static class TaskDedupeHash
{
    public static string Compute(Guid programId, Guid? scopeId, string taskType, Guid inputAssetId, string workerCapability)
    {
        var input = $"{programId:N}:{scopeId:N}:{taskType}:{inputAssetId:N}:{workerCapability}";
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
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

internal interface IAssetStore
{
    Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken);
    Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken);
    Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken);
    Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken);
    Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken);
    Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken);
    Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken);
    Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken);
}

internal sealed class InMemoryAssetStore : IAssetStore
{
    private readonly ConcurrentDictionary<Guid, AssetDto> _assets = new();
    private readonly ConcurrentDictionary<string, Guid> _naturalKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, AssetRelationshipDto> _relationships = new();

    public Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var assets = _assets.Values.AsEnumerable();

        if (query.ProgramId is not null)
        {
            assets = assets.Where(asset => asset.ProgramId == query.ProgramId);
        }

        if (query.Type is not null)
        {
            assets = assets.Where(asset => asset.Type == query.Type);
        }

        if (query.Status is not null)
        {
            assets = assets.Where(asset => asset.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            assets = assets.Where(asset => asset.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            assets = assets.Where(asset => asset.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase));
        }

        if (query.MinInterestingScore is not null)
        {
            assets = assets.Where(asset => asset.InterestingScore >= query.MinInterestingScore);
        }

        if (query.MinRiskScore is not null)
        {
            assets = assets.Where(asset => asset.RiskScore >= query.MinRiskScore);
        }

        var sort = query.Sort?.ToLowerInvariant() ?? "lastseenat";
        var direction = query.Direction?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        assets = sort switch
        {
            "interesting_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.InterestingScore)
                : assets.OrderByDescending(asset => asset.InterestingScore),
            "risk_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.RiskScore)
                : assets.OrderByDescending(asset => asset.RiskScore),
            "firstseenat" => direction == "asc"
                ? assets.OrderBy(asset => asset.FirstSeenAt)
                : assets.OrderByDescending(asset => asset.FirstSeenAt),
            "value" => direction == "asc"
                ? assets.OrderBy(asset => asset.Value, StringComparer.OrdinalIgnoreCase)
                : assets.OrderByDescending(asset => asset.Value, StringComparer.OrdinalIgnoreCase),
            _ => assets.OrderByDescending(asset => asset.LastSeenAt)
        };

        var ordered = assets.ToArray();

        var result = new PagedResult<AssetDto>(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            page,
            pageSize,
            ordered.Length);

        return Task.FromResult(result);
    }

    public Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedValue = AssetNormalization.NormalizeValue(request.Type, request.Value);
        var naturalKey = AssetNormalization.BuildNaturalKey(request.ProgramId, request.Type, normalizedValue, request.ScopeId);

        if (_naturalKeys.TryGetValue(naturalKey, out var existingId) && _assets.TryGetValue(existingId, out var existing))
        {
            var updated = existing with
            {
                LastSeenAt = now,
                Metadata = AssetSerialization.MergeMetadata(existing.Metadata, request.Metadata),
                Tags = AssetSerialization.MergeTags(existing.Tags, request.Tags)
            };

            _assets[existing.AssetId] = updated;
            return Task.FromResult(new AssetUpsertResult(updated, WasCreated: false));
        }

        var asset = new AssetDto(
            Guid.NewGuid(),
            request.ProgramId,
            request.ScopeId,
            request.Type,
            request.Subtype,
            normalizedValue,
            naturalKey,
            Confidence: request.Confidence ?? 1.0m,
            Status: AssetStatus.New,
            RiskScore: 0,
            InterestingScore: AssetScoring.ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue),
            FirstSeenAt: now,
            LastSeenAt: now,
            LastScannedAt: null,
            StalenessScore: 100,
            DiscoveredByTaskId: request.DiscoveredByTaskId,
            Metadata: request.Metadata ?? new Dictionary<string, string>(),
            Tags: request.Tags ?? []);

        _assets[asset.AssetId] = asset;
        _naturalKeys[naturalKey] = asset.AssetId;

        return Task.FromResult(new AssetUpsertResult(asset, WasCreated: true));
    }

    public Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        _assets.TryGetValue(assetId, out var asset);
        return Task.FromResult(asset);
    }

    public Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken) =>
        Task.FromResult(_assets.ContainsKey(assetId));

    public Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<AssetRelationshipDto> relationships = _relationships.Values
            .Where(edge => edge.FromAssetId == assetId || edge.ToAssetId == assetId)
            .OrderBy(edge => edge.EdgeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(relationships);
    }

    public Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken)
    {
        var edgeType = request.EdgeType.Trim().ToLowerInvariant();
        var exists = _relationships.Values.Any(r =>
            r.FromAssetId == request.FromAssetId && r.ToAssetId == request.ToAssetId && r.EdgeType == edgeType);
        if (exists)
        {
            throw new InvalidOperationException("Relationship already exists.");
        }

        var relationship = new AssetRelationshipDto(
            Guid.NewGuid(),
            request.FromAssetId,
            request.ToAssetId,
            edgeType,
            DateTimeOffset.UtcNow,
            request.DiscoveredByTaskId);

        _relationships[relationship.RelationshipId] = relationship;

        return Task.FromResult(relationship);
    }

    public Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
        {
            throw new InvalidOperationException("Asset not found.");
        }

        var updated = asset with { Status = status };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
        {
            throw new InvalidOperationException("Asset not found.");
        }

        var updated = asset with
        {
            Tags = AssetSerialization.MergeTags(asset.Tags, tags)
        };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
        {
            throw new InvalidOperationException("Asset not found.");
        }

        var updated = asset with
        {
            Tags = asset.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }

    public Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken)
    {
        if (!_assets.TryGetValue(assetId, out var asset))
        {
            throw new InvalidOperationException("Asset not found.");
        }

        var updated = asset with { Confidence = confidence };
        _assets[assetId] = updated;
        return Task.FromResult(updated);
    }
}

internal sealed class EfAssetStore(AssetDbContext dbContext) : IAssetStore
{
    public async Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var assets = dbContext.Assets.AsNoTracking().AsQueryable();

        if (query.ProgramId is not null)
        {
            assets = assets.Where(asset => asset.ProgramId == query.ProgramId);
        }

        if (query.Type is not null)
        {
            assets = assets.Where(asset => asset.Type == query.Type);
        }

        if (query.Status is not null)
        {
            assets = assets.Where(asset => asset.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            assets = assets.Where(asset => EF.Functions.ILike(asset.Value, $"%{query.Search}%"));
        }

        if (query.MinInterestingScore is not null)
        {
            assets = assets.Where(asset => asset.InterestingScore >= query.MinInterestingScore);
        }

        if (query.MinRiskScore is not null)
        {
            assets = assets.Where(asset => asset.RiskScore >= query.MinRiskScore);
        }

        var totalCount = await assets.CountAsync(cancellationToken);

        var sort = query.Sort?.ToLowerInvariant() ?? "lastseenat";
        var direction = query.Direction?.ToLowerInvariant() == "asc" ? "asc" : "desc";

        assets = sort switch
        {
            "interesting_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.InterestingScore)
                : assets.OrderByDescending(asset => asset.InterestingScore),
            "risk_score" => direction == "asc"
                ? assets.OrderBy(asset => asset.RiskScore)
                : assets.OrderByDescending(asset => asset.RiskScore),
            "firstseenat" => direction == "asc"
                ? assets.OrderBy(asset => asset.FirstSeenAt)
                : assets.OrderByDescending(asset => asset.FirstSeenAt),
            "value" => direction == "asc"
                ? assets.OrderBy(asset => asset.Value)
                : assets.OrderByDescending(asset => asset.Value),
            _ => assets.OrderByDescending(asset => asset.LastSeenAt)
        };

        var records = await assets
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return new PagedResult<AssetDto>(records.Select(asset => asset.ToDto()).ToArray(), page, pageSize, totalCount);
    }

    public async Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedValue = AssetNormalization.NormalizeValue(request.Type, request.Value);
        var naturalKey = AssetNormalization.BuildNaturalKey(request.ProgramId, request.Type, normalizedValue, request.ScopeId);

        var existing = await dbContext.Assets
            .FirstOrDefaultAsync(asset =>
                asset.ProgramId == request.ProgramId
                && asset.Type == request.Type
                && asset.NaturalKey == naturalKey,
                cancellationToken);

        if (existing is not null)
        {
            existing.LastSeenAt = now;
            existing.MetadataJson = JsonSerializer.Serialize(AssetSerialization.MergeMetadata(existing.Metadata, request.Metadata));
            existing.TagsJson = JsonSerializer.Serialize(AssetSerialization.MergeTags(existing.Tags, request.Tags));

            await dbContext.SaveChangesAsync(cancellationToken);
            return new AssetUpsertResult(existing.ToDto(), WasCreated: false);
        }

        var created = new AssetRecord
        {
            AssetId = Guid.NewGuid(),
            ProgramId = request.ProgramId,
            ScopeId = request.ScopeId,
            Type = request.Type,
            Subtype = request.Subtype,
            Value = normalizedValue,
            NaturalKey = naturalKey,
            Confidence = request.Confidence ?? 1.0m,
            Status = AssetStatus.New,
            RiskScore = 0,
            InterestingScore = AssetScoring.ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue),
            FirstSeenAt = now,
            LastSeenAt = now,
            DiscoveredByTaskId = request.DiscoveredByTaskId,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>()),
            TagsJson = JsonSerializer.Serialize(request.Tags ?? [])
        };

        dbContext.Assets.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AssetUpsertResult(created.ToDto(), WasCreated: true);
    }

    public async Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.AssetId == assetId, cancellationToken);

        return asset?.ToDto();
    }

    public async Task<AssetDto?> FindByNaturalKeyAsync(string naturalKey, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.NaturalKey == naturalKey, cancellationToken);

        return asset?.ToDto();
    }

    public Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken) =>
        dbContext.Assets.AnyAsync(asset => asset.AssetId == assetId, cancellationToken);

    public async Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        return await dbContext.AssetRelationships
            .AsNoTracking()
            .Where(edge => edge.FromAssetId == assetId || edge.ToAssetId == assetId)
            .OrderBy(edge => edge.EdgeType)
            .Select(edge => edge.ToDto())
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken)
    {
        var edgeType = request.EdgeType.Trim().ToLowerInvariant();
        var exists = await dbContext.AssetRelationships.AnyAsync(
            r => r.FromAssetId == request.FromAssetId && r.ToAssetId == request.ToAssetId && r.EdgeType == edgeType,
            cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException("Relationship already exists.");
        }

        var relationship = new AssetRelationshipRecord
        {
            RelationshipId = Guid.NewGuid(),
            FromAssetId = request.FromAssetId,
            ToAssetId = request.ToAssetId,
            EdgeType = edgeType,
            CreatedAt = DateTimeOffset.UtcNow,
            DiscoveredByTaskId = request.DiscoveredByTaskId
        };

        dbContext.AssetRelationships.Add(relationship);
        await dbContext.SaveChangesAsync(cancellationToken);

        return relationship.ToDto();
    }

    public async Task<AssetDto> UpdateStatusAsync(Guid assetId, AssetStatus status, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.Status = status;
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> AddTagsAsync(Guid assetId, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        var existingTags = asset.Tags;
        asset.TagsJson = JsonSerializer.Serialize(AssetSerialization.MergeTags(existingTags, tags));
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> RemoveTagAsync(Guid assetId, string tag, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        var updatedTags = asset.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
        asset.TagsJson = JsonSerializer.Serialize(updatedTags);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }

    public async Task<AssetDto> UpdateConfidenceAsync(Guid assetId, decimal confidence, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.AssetId == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");

        asset.Confidence = confidence;
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset.ToDto();
    }
}

internal sealed class AssetDbContext(DbContextOptions<AssetDbContext> options) : DbContext(options)
{
    public DbSet<AssetRecord> Assets => Set<AssetRecord>();
    public DbSet<AssetRelationshipRecord> AssetRelationships => Set<AssetRelationshipRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var asset = modelBuilder.Entity<AssetRecord>();
        asset.ToTable("assets");
        asset.HasKey(record => record.AssetId);
        asset.HasIndex(record => new { record.ProgramId, record.Type, record.NaturalKey }).IsUnique();
        asset.HasIndex(record => new { record.ProgramId, record.Type, record.FirstSeenAt });
        asset.HasIndex(record => new { record.ProgramId, record.Type, record.LastSeenAt });
        asset.HasIndex(record => new { record.ProgramId, record.Status });
        asset.HasIndex(record => new { record.ProgramId, record.InterestingScore });
        asset.Property(record => record.Type).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.Subtype).HasMaxLength(128);
        asset.Property(record => record.Value).HasMaxLength(2048);
        asset.Property(record => record.NaturalKey).HasMaxLength(128);
        asset.Property(record => record.MetadataJson).HasColumnType("jsonb");
        asset.Property(record => record.TagsJson).HasColumnType("jsonb");

        var relationship = modelBuilder.Entity<AssetRelationshipRecord>();
        relationship.ToTable("asset_edges");
        relationship.HasKey(record => record.RelationshipId);
        relationship.HasIndex(record => record.FromAssetId);
        relationship.HasIndex(record => record.ToAssetId);
        relationship.HasIndex(record => record.EdgeType);
        relationship.HasIndex(record => new { record.FromAssetId, record.ToAssetId, record.EdgeType }).IsUnique();
        relationship.Property(record => record.EdgeType).HasMaxLength(128);

        modelBuilder.ConfigureArgusOutbox();
    }
}

internal sealed class AssetRecord
{
    private static readonly Dictionary<AssetType, TimeSpan> ExpectedScanIntervals = new()
    {
        [AssetType.Subdomain] = TimeSpan.FromDays(1),
        [AssetType.Domain] = TimeSpan.FromDays(7),
        [AssetType.Ip] = TimeSpan.FromDays(7),
        [AssetType.Url] = TimeSpan.FromDays(3),
        [AssetType.HttpResponse] = TimeSpan.FromDays(7),
        [AssetType.HtmlPage] = TimeSpan.FromDays(7),
        [AssetType.JavaScriptFile] = TimeSpan.FromDays(14),
        [AssetType.CssFile] = TimeSpan.FromDays(30),
        [AssetType.JsonDocument] = TimeSpan.FromDays(30),
        [AssetType.ApiEndpoint] = TimeSpan.FromDays(3),
        [AssetType.Technology] = TimeSpan.FromDays(14),
        [AssetType.Finding] = TimeSpan.FromDays(30),
        [AssetType.Port] = TimeSpan.FromDays(7),
        [AssetType.DnsRecord] = TimeSpan.FromDays(7)
    };

    private static readonly TimeSpan MaxStalenessInterval = TimeSpan.FromDays(90);

    public Guid AssetId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public AssetType Type { get; set; }
    public string? Subtype { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NaturalKey { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public AssetStatus Status { get; set; }
    public int RiskScore { get; set; }
    public int InterestingScore { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastScannedAt { get; set; }
    public string? DiscoveredByTaskId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string TagsJson { get; set; } = "[]";

    public IReadOnlyDictionary<string, string> Metadata =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? new Dictionary<string, string>();

    public IReadOnlyCollection<string> Tags =>
        JsonSerializer.Deserialize<string[]>(TagsJson) ?? [];

    public int ComputeStalenessScore()
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveLastSeen = LastScannedAt ?? LastSeenAt;
        var interval = now - effectiveLastSeen;
        if (interval < TimeSpan.Zero) return 0;
        var expectedInterval = ExpectedScanIntervals.GetValueOrDefault(Type, TimeSpan.FromDays(7));
        if (interval >= MaxStalenessInterval) return 100;
        var score = (int)((interval.TotalHours / expectedInterval.TotalHours) * 100);
        return Math.Min(score, 100);
    }

    public AssetDto ToDto()
    {
        var stalenessScore = ComputeStalenessScore();
        return new AssetDto(
            AssetId,
            ProgramId,
            ScopeId,
            Type,
            Subtype,
            Value,
            NaturalKey,
            Confidence,
            Status,
            RiskScore,
            InterestingScore,
            FirstSeenAt,
            LastSeenAt,
            LastScannedAt,
            stalenessScore,
            DiscoveredByTaskId,
            Metadata,
            Tags);
    }
}

internal sealed class AssetRelationshipRecord
{
    public Guid RelationshipId { get; set; }
    public Guid FromAssetId { get; set; }
    public Guid ToAssetId { get; set; }
    public string EdgeType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? DiscoveredByTaskId { get; set; }

    public AssetRelationshipDto ToDto() =>
        new(RelationshipId, FromAssetId, ToAssetId, EdgeType, CreatedAt, DiscoveredByTaskId);
}

internal static class AssetNormalization
{
    public static string NormalizeValue(AssetType type, string value)
    {
        var trimmed = value.Trim();

        return type is AssetType.Domain or AssetType.Subdomain or AssetType.Url or AssetType.ApiEndpoint
            ? trimmed.ToLowerInvariant()
            : trimmed;
    }

    public static string BuildNaturalKey(Guid programId, AssetType type, string normalizedValue, Guid? scopeId)
    {
        var input = $"{programId:N}:{scopeId:N}:{type}:{normalizedValue}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

}

internal static class AssetSerialization
{
    public static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in incoming)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    public static IReadOnlyCollection<string> MergeTags(
        IReadOnlyCollection<string> existing,
        IReadOnlyCollection<string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return existing;
        }

        return existing
            .Concat(incoming)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}

internal static class AssetScoring
{
    public static int ScoreInitialInterestingness(AssetType type, string? subtype, string value) =>
        type switch
        {
            AssetType.ApiEndpoint => 40,
            AssetType.FindingCandidate => 70,
            AssetType.Finding => 85,
            AssetType.Url when value.Contains("admin", StringComparison.OrdinalIgnoreCase) => 35,
            AssetType.Url when value.Contains("login", StringComparison.OrdinalIgnoreCase) => 25,
            AssetType.JavaScriptFile => 20,
            _ when string.Equals(subtype, "graphql", StringComparison.OrdinalIgnoreCase) => 45,
            _ => 5
        };
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
                _logger.LogDebug("Updated LastScannedAt for asset {AssetId} after task {TaskId} completed",
                    envelope.Payload.InputAssetId, envelope.Payload.TaskId);
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

internal sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);
