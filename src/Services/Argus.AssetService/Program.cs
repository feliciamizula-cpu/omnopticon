using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRealtimeIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<AssetStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/assets", (
    Guid? programId,
    AssetType? type,
    AssetStatus? status,
    string? search,
    int? page,
    int? pageSize,
    AssetStore store) =>
{
    var query = new AssetQuery(programId, type, status, search, page ?? 1, pageSize ?? 100);
    return store.Query(query);
});

app.MapPost("/assets", async (
    CreateAssetRequest request,
    AssetStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Value))
    {
        return Results.BadRequest("Asset value is required.");
    }

    var result = store.Upsert(request);
    var asset = result.Asset;
    var eventType = result.WasCreated ? nameof(AssetDiscovered) : nameof(AssetUpdated);

    if (result.WasCreated)
    {
        await events.PublishAsync(
            new AssetDiscovered(asset.AssetId, asset.ProgramId, asset.Type.ToString(), asset.Value),
            eventType,
            "Argus.AssetService",
            cancellationToken: cancellationToken);
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

app.MapGet("/assets/{assetId:guid}", (Guid assetId, AssetStore store) =>
    store.TryGet(assetId, out var asset) ? Results.Ok(asset) : Results.NotFound());

app.MapGet("/assets/{assetId:guid}/relationships", (Guid assetId, AssetStore store) =>
    store.GetRelationships(assetId));

app.MapPost("/assets/relationships", async (
    CreateAssetRelationshipRequest request,
    AssetStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (!store.Contains(request.FromAssetId) || !store.Contains(request.ToAssetId))
    {
        return Results.NotFound("Both assets must exist before a relationship can be created.");
    }

    var relationship = store.AddRelationship(request);
    await events.PublishAsync(
        new AssetRelationshipDiscovered(relationship.FromAssetId, relationship.ToAssetId, relationship.EdgeType),
        nameof(AssetRelationshipDiscovered),
        "Argus.AssetService",
        cancellationToken: cancellationToken);

    return Results.Created($"/assets/{request.FromAssetId}/relationships", relationship);
});

app.Run();

internal sealed class AssetStore
{
    private readonly ConcurrentDictionary<Guid, AssetDto> _assets = new();
    private readonly ConcurrentDictionary<string, Guid> _naturalKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, AssetRelationshipDto> _relationships = new();

    public PagedResult<AssetDto> Query(AssetQuery query)
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

        var ordered = assets
            .OrderByDescending(asset => asset.InterestingScore)
            .ThenByDescending(asset => asset.LastSeenAt)
            .ToArray();

        return new PagedResult<AssetDto>(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            page,
            pageSize,
            ordered.Length);
    }

    public AssetUpsertResult Upsert(CreateAssetRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedValue = NormalizeValue(request.Type, request.Value);
        var naturalKey = BuildNaturalKey(request.ProgramId, request.Type, normalizedValue, request.ScopeId);

        if (_naturalKeys.TryGetValue(naturalKey, out var existingId) && _assets.TryGetValue(existingId, out var existing))
        {
            var updated = existing with
            {
                LastSeenAt = now,
                Metadata = MergeMetadata(existing.Metadata, request.Metadata),
                Tags = MergeTags(existing.Tags, request.Tags)
            };

            _assets[existing.AssetId] = updated;
            return new AssetUpsertResult(updated, WasCreated: false);
        }

        var asset = new AssetDto(
            Guid.NewGuid(),
            request.ProgramId,
            request.ScopeId,
            request.Type,
            request.Subtype,
            normalizedValue,
            naturalKey,
            Confidence: 1.0m,
            Status: AssetStatus.New,
            RiskScore: 0,
            InterestingScore: ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue),
            FirstSeenAt: now,
            LastSeenAt: now,
            DiscoveredByTaskId: request.DiscoveredByTaskId,
            Metadata: request.Metadata ?? new Dictionary<string, string>(),
            Tags: request.Tags ?? []);

        _assets[asset.AssetId] = asset;
        _naturalKeys[naturalKey] = asset.AssetId;

        return new AssetUpsertResult(asset, WasCreated: true);
    }

    public bool TryGet(Guid assetId, out AssetDto? asset) => _assets.TryGetValue(assetId, out asset);

    public bool Contains(Guid assetId) => _assets.ContainsKey(assetId);

    public IReadOnlyCollection<AssetRelationshipDto> GetRelationships(Guid assetId) =>
        _relationships.Values
            .Where(edge => edge.FromAssetId == assetId || edge.ToAssetId == assetId)
            .OrderBy(edge => edge.EdgeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public AssetRelationshipDto AddRelationship(CreateAssetRelationshipRequest request)
    {
        var relationship = new AssetRelationshipDto(
            Guid.NewGuid(),
            request.FromAssetId,
            request.ToAssetId,
            request.EdgeType.Trim().ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            request.DiscoveredByTaskId);

        _relationships[relationship.RelationshipId] = relationship;

        return relationship;
    }

    private static string NormalizeValue(AssetType type, string value)
    {
        var trimmed = value.Trim();

        return type is AssetType.Domain or AssetType.Subdomain or AssetType.Url or AssetType.ApiEndpoint
            ? trimmed.ToLowerInvariant()
            : trimmed;
    }

    private static string BuildNaturalKey(Guid programId, AssetType type, string normalizedValue, Guid? scopeId)
    {
        var input = $"{programId:N}:{scopeId:N}:{type}:{normalizedValue}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
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

    private static IReadOnlyCollection<string> MergeTags(
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

    private static int ScoreInitialInterestingness(AssetType type, string? subtype, string value) =>
        type switch
        {
            AssetType.ApiEndpoint => 40,
            AssetType.FindingCandidate => 70,
            AssetType.Url when value.Contains("admin", StringComparison.OrdinalIgnoreCase) => 35,
            AssetType.Url when value.Contains("login", StringComparison.OrdinalIgnoreCase) => 25,
            AssetType.JavaScriptFile => 20,
            _ when string.Equals(subtype, "graphql", StringComparison.OrdinalIgnoreCase) => 45,
            _ => 5
        };
}

internal sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);
