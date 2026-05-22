using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRealtimeIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddProblemDetails();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<AssetDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddScoped<IAssetStore, EfAssetStore>();
}
else
{
    builder.Services.AddSingleton<IAssetStore, InMemoryAssetStore>();
}

var app = builder.Build();

await app.InitializeAssetStoreAsync();
app.MapDefaultEndpoints();

app.MapGet("/assets", (
    Guid? programId,
    AssetType? type,
    AssetStatus? status,
    string? search,
    int? page,
    int? pageSize,
    IAssetStore store,
    CancellationToken cancellationToken) =>
{
    var query = new AssetQuery(programId, type, status, search, page ?? 1, pageSize ?? 100);
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

    var relationship = await store.AddRelationshipAsync(request, cancellationToken);
    await events.PublishAsync(
        new AssetRelationshipDiscovered(relationship.FromAssetId, relationship.ToAssetId, relationship.EdgeType),
        nameof(AssetRelationshipDiscovered),
        "Argus.AssetService",
        cancellationToken: cancellationToken);

    return Results.Created($"/assets/{request.FromAssetId}/relationships", relationship);
});

app.Run();

internal interface IAssetStore
{
    Task<PagedResult<AssetDto>> QueryAsync(AssetQuery query, CancellationToken cancellationToken);
    Task<AssetUpsertResult> UpsertAsync(CreateAssetRequest request, CancellationToken cancellationToken);
    Task<AssetDto?> FindAsync(Guid assetId, CancellationToken cancellationToken);
    Task<bool> ContainsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AssetRelationshipDto>> GetRelationshipsAsync(Guid assetId, CancellationToken cancellationToken);
    Task<AssetRelationshipDto> AddRelationshipAsync(CreateAssetRelationshipRequest request, CancellationToken cancellationToken);
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

        var ordered = assets
            .OrderByDescending(asset => asset.InterestingScore)
            .ThenByDescending(asset => asset.LastSeenAt)
            .ToArray();

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
            Confidence: 1.0m,
            Status: AssetStatus.New,
            RiskScore: 0,
            InterestingScore: AssetScoring.ScoreInitialInterestingness(request.Type, request.Subtype, normalizedValue),
            FirstSeenAt: now,
            LastSeenAt: now,
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
        var relationship = new AssetRelationshipDto(
            Guid.NewGuid(),
            request.FromAssetId,
            request.ToAssetId,
            request.EdgeType.Trim().ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            request.DiscoveredByTaskId);

        _relationships[relationship.RelationshipId] = relationship;

        return Task.FromResult(relationship);
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

        var totalCount = await assets.CountAsync(cancellationToken);
        var records = await assets
            .OrderByDescending(asset => asset.InterestingScore)
            .ThenByDescending(asset => asset.LastSeenAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        var items = records.Select(asset => asset.ToDto()).ToArray();

        return new PagedResult<AssetDto>(items, page, pageSize, totalCount);
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
            Confidence = 1.0m,
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
        var relationship = new AssetRelationshipRecord
        {
            RelationshipId = Guid.NewGuid(),
            FromAssetId = request.FromAssetId,
            ToAssetId = request.ToAssetId,
            EdgeType = request.EdgeType.Trim().ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            DiscoveredByTaskId = request.DiscoveredByTaskId
        };

        dbContext.AssetRelationships.Add(relationship);
        await dbContext.SaveChangesAsync(cancellationToken);

        return relationship.ToDto();
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
        relationship.Property(record => record.EdgeType).HasMaxLength(128);
    }
}

internal sealed class AssetRecord
{
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
    public string? DiscoveredByTaskId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string TagsJson { get; set; } = "[]";

    public IReadOnlyDictionary<string, string> Metadata =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? new Dictionary<string, string>();

    public IReadOnlyCollection<string> Tags =>
        JsonSerializer.Deserialize<string[]>(TagsJson) ?? [];

    public AssetDto ToDto() =>
        new(
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
            DiscoveredByTaskId,
            Metadata,
            Tags);
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
            AssetType.Url when value.Contains("admin", StringComparison.OrdinalIgnoreCase) => 35,
            AssetType.Url when value.Contains("login", StringComparison.OrdinalIgnoreCase) => 25,
            AssetType.JavaScriptFile => 20,
            _ when string.Equals(subtype, "graphql", StringComparison.OrdinalIgnoreCase) => 45,
            _ => 5
        };
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
        }
    }
}

internal sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);
