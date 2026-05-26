using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Artifacts;
using Argus.ServiceDefaults;
using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<ArtifactDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<ArtifactDbContext>();
    builder.Services.AddArgusInboxConsumer<ArtifactDbContext>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
    builder.Services.AddScoped<IArtifactStore, EfArtifactStore>();
}
else
{
    builder.Services.AddSingleton<IArtifactStore, InMemoryArtifactStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.ArtifactService");
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeArtifactStoreAsync();
app.MapDefaultEndpoints();

app.MapPost("/artifacts", async (
    CreateArtifactRequest request,
    IArtifactStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.StorageKey))
    {
        return Results.BadRequest("Storage key is required.");
    }

    var artifact = await store.CreateAsync(request, cancellationToken);

    await events.PublishAsync(
        new ArtifactCreated(artifact.ArtifactId, artifact.TargetId, artifact.ArtifactType, artifact.ContentType),
        nameof(ArtifactCreated),
        "Argus.ArtifactService",
        cancellationToken: cancellationToken);

    return Results.Created($"/artifacts/{artifact.ArtifactId}", artifact);
});

app.MapGet("/artifacts/{artifactId:guid}", async (
    Guid artifactId,
    IArtifactStore store,
    CancellationToken cancellationToken) =>
{
    var artifact = await store.FindAsync(artifactId, cancellationToken);
    return artifact is not null ? Results.Ok(artifact) : Results.NotFound();
});

app.MapGet("/artifacts/{artifactId:guid}/preview", async (
    Guid artifactId,
    IArtifactStore store,
    CancellationToken cancellationToken) =>
{
    var preview = await store.GetPreviewAsync(artifactId, cancellationToken);
    return preview is not null ? Results.Ok(preview) : Results.NotFound();
});

app.MapGet("/artifacts/{artifactId:guid}/download", async (
    Guid artifactId,
    IArtifactStore store,
    CancellationToken cancellationToken) =>
{
    var downloadInfo = await store.GetDownloadInfoAsync(artifactId, cancellationToken);
    return downloadInfo is not null ? Results.Ok(downloadInfo) : Results.NotFound();
});

app.MapGet("/assets/{assetId:guid}/artifacts", async (
    Guid assetId,
    int? page,
    int? pageSize,
    IArtifactStore store,
    CancellationToken cancellationToken) =>
{
    var artifacts = await store.GetByAssetIdAsync(assetId, page ?? 1, pageSize ?? 50, cancellationToken);
    return Results.Ok(artifacts);
});

app.MapGet("/tasks/{taskRunId:guid}/artifacts", async (
    Guid taskRunId,
    int? page,
    int? pageSize,
    IArtifactStore store,
    CancellationToken cancellationToken) =>
{
    var artifacts = await store.GetByTaskRunIdAsync(taskRunId, page ?? 1, pageSize ?? 50, cancellationToken);
    return Results.Ok(artifacts);
});

app.MapPatch("/artifacts/{artifactId:guid}/redaction", async (
    Guid artifactId,
    UpdateArtifactRedactionRequest request,
    IArtifactStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var artifact = await store.UpdateRedactionStatusAsync(artifactId, request, cancellationToken);
    if (artifact is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(artifact);
});

app.Run();

internal interface IArtifactStore
{
    Task<ArtifactDto> CreateAsync(CreateArtifactRequest request, CancellationToken cancellationToken);
    Task<ArtifactDto?> FindAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<ArtifactPreviewDto?> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<ArtifactDownloadDto?> GetDownloadInfoAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<PagedResult<ArtifactDto>> GetByAssetIdAsync(Guid assetId, int page, int pageSize, CancellationToken cancellationToken);
    Task<PagedResult<ArtifactDto>> GetByTaskRunIdAsync(Guid taskRunId, int page, int pageSize, CancellationToken cancellationToken);
    Task<ArtifactDto?> UpdateRedactionStatusAsync(Guid artifactId, UpdateArtifactRedactionRequest request, CancellationToken cancellationToken);
}

internal sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, ArtifactDto> _artifacts = new();
    private readonly ConcurrentDictionary<Guid, List<ArtifactDto>> _byAssetId = new();
    private readonly ConcurrentDictionary<Guid, List<ArtifactDto>> _byTaskRunId = new();

    public Task<ArtifactDto> CreateAsync(CreateArtifactRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var artifact = new ArtifactDto(
            ArtifactId: Guid.NewGuid(),
            TargetId: request.TargetId,
            ProgramId: request.ProgramId,
            AssetId: request.AssetId,
            TaskRunId: request.TaskRunId,
            WorkerType: request.WorkerType,
            ArtifactType: request.ArtifactType,
            ContentType: request.ContentType,
            StorageProvider: request.StorageProvider ?? "local",
            StorageKey: request.StorageKey,
            SizeBytes: request.SizeBytes,
            Sha256: ComputeSha256(request.StorageKey),
            PreviewText: null,
            RedactionStatus: RedactionStatus.None,
            RetentionExpiresAt: now.AddYears(1),
            CreatedAt: now);

        _artifacts[artifact.ArtifactId] = artifact;

        if (request.AssetId.HasValue)
        {
            if (!_byAssetId.ContainsKey(request.AssetId.Value))
                _byAssetId[request.AssetId.Value] = new List<ArtifactDto>();
            _byAssetId[request.AssetId.Value].Add(artifact);
        }

        if (request.TaskRunId.HasValue)
        {
            if (!_byTaskRunId.ContainsKey(request.TaskRunId.Value))
                _byTaskRunId[request.TaskRunId.Value] = new List<ArtifactDto>();
            _byTaskRunId[request.TaskRunId.Value].Add(artifact);
        }

        return Task.FromResult(artifact);
    }

    public Task<ArtifactDto?> FindAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        _artifacts.TryGetValue(artifactId, out var artifact);
        return Task.FromResult(artifact);
    }

    public Task<ArtifactPreviewDto?> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        if (!_artifacts.TryGetValue(artifactId, out var artifact))
            return Task.FromResult<ArtifactPreviewDto?>(null);

        var preview = new ArtifactPreviewDto(
            artifact.ArtifactId,
            artifact.ArtifactType,
            artifact.ContentType,
            artifact.PreviewText ?? $"Preview of {artifact.StorageKey}",
            artifact.SizeBytes);
        return Task.FromResult<ArtifactPreviewDto?>(preview);
    }

    public Task<ArtifactDownloadDto?> GetDownloadInfoAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        if (!_artifacts.TryGetValue(artifactId, out var artifact))
            return Task.FromResult<ArtifactDownloadDto?>(null);

        var download = new ArtifactDownloadDto(
            artifact.ArtifactId,
            artifact.StorageProvider,
            artifact.StorageKey,
            artifact.ContentType,
            artifact.SizeBytes,
            artifact.Sha256,
            $"attachment; filename=\"{Path.GetFileName(artifact.StorageKey)}\"");
        return Task.FromResult<ArtifactDownloadDto?>(download);
    }

    public Task<PagedResult<ArtifactDto>> GetByAssetIdAsync(Guid assetId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var artifacts = _byAssetId.TryGetValue(assetId, out var list) ? list : new List<ArtifactDto>();
        var pageResult = Paginate(artifacts, page, pageSize);
        return Task.FromResult(pageResult);
    }

    public Task<PagedResult<ArtifactDto>> GetByTaskRunIdAsync(Guid taskRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var artifacts = _byTaskRunId.TryGetValue(taskRunId, out var list) ? list : new List<ArtifactDto>();
        var pageResult = Paginate(artifacts, page, pageSize);
        return Task.FromResult(pageResult);
    }

    public Task<ArtifactDto?> UpdateRedactionStatusAsync(Guid artifactId, UpdateArtifactRedactionRequest request, CancellationToken cancellationToken)
    {
        if (!_artifacts.TryGetValue(artifactId, out var artifact))
            return Task.FromResult<ArtifactDto?>(null);

        var updated = artifact with { RedactionStatus = request.Status };
        _artifacts[artifactId] = updated;
        return Task.FromResult<ArtifactDto?>(updated);
    }

    private static PagedResult<ArtifactDto> Paginate(List<ArtifactDto> artifacts, int page, int pageSize)
    {
        var p = Math.Max(1, page);
        var ps = Math.Clamp(pageSize, 1, 200);
        var total = artifacts.Count;
        var items = artifacts.Skip((p - 1) * ps).Take(ps).ToArray();
        return new PagedResult<ArtifactDto>(items, p, ps, total);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class EfArtifactStore(ArtifactDbContext dbContext) : IArtifactStore
{
    public async Task<ArtifactDto> CreateAsync(CreateArtifactRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ArtifactRecord
        {
            ArtifactId = Guid.NewGuid(),
            TargetId = request.TargetId,
            ProgramId = request.ProgramId,
            AssetId = request.AssetId,
            TaskRunId = request.TaskRunId,
            WorkerType = request.WorkerType,
            ArtifactType = request.ArtifactType,
            ContentType = request.ContentType,
            StorageProvider = request.StorageProvider ?? "local",
            StorageKey = request.StorageKey,
            SizeBytes = request.SizeBytes,
            Sha256 = request.Sha256,
            PreviewText = request.PreviewText,
            RedactionStatus = RedactionStatus.None,
            RetentionExpiresAt = now.AddYears(1),
            CreatedAt = now
        };

        dbContext.Artifacts.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return record.ToDto();
    }

    public async Task<ArtifactDto?> FindAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var record = await dbContext.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
        return record?.ToDto();
    }

    public async Task<ArtifactPreviewDto?> GetPreviewAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var record = await dbContext.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
        if (record is null) return null;

        return new ArtifactPreviewDto(
            record.ArtifactId,
            record.ArtifactType,
            record.ContentType,
            record.PreviewText ?? $"Preview of {record.StorageKey}",
            record.SizeBytes);
    }

    public async Task<ArtifactDownloadDto?> GetDownloadInfoAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var record = await dbContext.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
        if (record is null) return null;

        return new ArtifactDownloadDto(
            record.ArtifactId,
            record.StorageProvider,
            record.StorageKey,
            record.ContentType,
            record.SizeBytes,
            record.Sha256,
            $"attachment; filename=\"{Path.GetFileName(record.StorageKey)}\"");
    }

    public async Task<PagedResult<ArtifactDto>> GetByAssetIdAsync(Guid assetId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var p = Math.Max(1, page);
        var ps = Math.Clamp(pageSize, 1, 200);

        var query = dbContext.Artifacts.AsNoTracking().Where(a => a.AssetId == assetId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.CreatedAt).Skip((p - 1) * ps).Take(ps).Select(a => a.ToDto()).ToListAsync(cancellationToken);

        return new PagedResult<ArtifactDto>(items, p, ps, total);
    }

    public async Task<PagedResult<ArtifactDto>> GetByTaskRunIdAsync(Guid taskRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var p = Math.Max(1, page);
        var ps = Math.Clamp(pageSize, 1, 200);

        var query = dbContext.Artifacts.AsNoTracking().Where(a => a.TaskRunId == taskRunId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.CreatedAt).Skip((p - 1) * ps).Take(ps).Select(a => a.ToDto()).ToListAsync(cancellationToken);

        return new PagedResult<ArtifactDto>(items, p, ps, total);
    }

    public async Task<ArtifactDto?> UpdateRedactionStatusAsync(Guid artifactId, UpdateArtifactRedactionRequest request, CancellationToken cancellationToken)
    {
        var record = await dbContext.Artifacts.FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken);
        if (record is null) return null;

        record.RedactionStatus = request.Status;
        await dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }
}

internal sealed class ArtifactDbContext(DbContextOptions<ArtifactDbContext> options) : DbContext(options)
{
    public DbSet<ArtifactRecord> Artifacts => Set<ArtifactRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var artifact = modelBuilder.Entity<ArtifactRecord>();
        artifact.ToTable("artifacts");
        artifact.HasKey(record => record.ArtifactId);
        artifact.HasIndex(record => record.AssetId);
        artifact.HasIndex(record => record.TaskRunId);
        artifact.HasIndex(record => record.ProgramId);
        artifact.HasIndex(record => record.TargetId);
        artifact.HasIndex(record => record.ArtifactType);
        artifact.Property(record => record.ArtifactType).HasConversion<string>().HasMaxLength(64);
        artifact.Property(record => record.ContentType).HasMaxLength(128);
        artifact.Property(record => record.StorageProvider).HasMaxLength(64);
        artifact.Property(record => record.StorageKey).HasMaxLength(2048);
        artifact.Property(record => record.Sha256).HasMaxLength(64);
        artifact.Property(record => record.WorkerType).HasMaxLength(128);
        artifact.Property(record => record.RedactionStatus).HasConversion<string>().HasMaxLength(64);

        modelBuilder.ConfigureArgusOutbox();
    }
}

internal sealed class ArtifactRecord
{
    public Guid ArtifactId { get; set; }
    public Guid TargetId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? TaskRunId { get; set; }
    public string? WorkerType { get; set; }
    public string ArtifactType { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string StorageProvider { get; set; } = "local";
    public string StorageKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? PreviewText { get; set; }
    public RedactionStatus RedactionStatus { get; set; }
    public DateTimeOffset? RetentionExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ArtifactDto ToDto() => new(
        ArtifactId,
        TargetId,
        ProgramId,
        AssetId,
        TaskRunId,
        WorkerType,
        ArtifactType,
        ContentType,
        StorageProvider,
        StorageKey,
        SizeBytes,
        Sha256,
        PreviewText,
        RedactionStatus,
        RetentionExpiresAt,
        CreatedAt);
}

internal static class ArtifactStoreInitialization
{
    public static async Task InitializeArtifactStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<ArtifactDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}