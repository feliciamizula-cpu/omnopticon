using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Findings;
using Argus.ServiceDefaults;
using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<FindingDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<FindingDbContext>();
    builder.Services.AddArgusInboxConsumer<FindingDbContext>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
    builder.Services.AddScoped<IFindingStore, EfFindingStore>();
}
else
{
    builder.Services.AddSingleton<IFindingStore, InMemoryFindingStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.FindingService");
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeFindingStoreAsync();
app.MapDefaultEndpoints();

app.MapPost("/findings/search", async (
    SearchFindingsRequest request,
    IFindingStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.SearchAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/findings", async (
    CreateFindingRequest request,
    IFindingStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var finding = await store.CreateAsync(request, cancellationToken);

    await events.PublishAsync(
        new FindingCreated(finding.FindingId, finding.TargetId, finding.Title, finding.Severity.ToString()),
        nameof(FindingCreated),
        "Argus.FindingService",
        cancellationToken: cancellationToken);

    return Results.Created($"/findings/{finding.FindingId}", finding);
});

app.MapGet("/findings/{findingId:guid}", async (
    Guid findingId,
    IFindingStore store,
    CancellationToken cancellationToken) =>
{
    var finding = await store.FindAsync(findingId, cancellationToken);
    return finding is not null ? Results.Ok(finding) : Results.NotFound();
});

app.MapPatch("/findings/{findingId:guid}", async (
    Guid findingId,
    UpdateFindingRequest request,
    IFindingStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var finding = await store.UpdateAsync(findingId, request, cancellationToken);
    if (finding is null) return Results.NotFound();

    await events.PublishAsync(
        new FindingUpdated(finding.FindingId, finding.Status.ToString()),
        nameof(FindingUpdated),
        "Argus.FindingService",
        cancellationToken: cancellationToken);

    return Results.Ok(finding);
});

app.MapPatch("/findings/{findingId:guid}/triage", async (
    Guid findingId,
    TriageFindingRequest request,
    IFindingStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var result = await store.TriageAsync(findingId, request, cancellationToken);
    if (result is null) return Results.NotFound();

    await events.PublishAsync(
        new FindingTriaged(findingId, result.OldStatus.ToString(), request.NewStatus.ToString(), request.Reason ?? ""),
        nameof(FindingTriaged),
        "Argus.FindingService",
        cancellationToken: cancellationToken);

    return Results.Ok(result.Finding);
});

app.MapPost("/findings/{findingId:guid}/notes", async (
    Guid findingId,
    AddFindingNoteRequest request,
    IFindingStore store,
    CancellationToken cancellationToken) =>
{
    var note = await store.AddNoteAsync(findingId, request, cancellationToken);
    if (note is null) return Results.NotFound();
    return Results.Created($"/findings/{findingId}/notes", note);
});

app.MapPost("/findings/{findingId:guid}/tags", async (
    Guid findingId,
    UpdateFindingTagsRequest request,
    IFindingStore store,
    CancellationToken cancellationToken) =>
{
    var finding = await store.UpdateTagsAsync(findingId, request, cancellationToken);
    return finding is not null ? Results.Ok(finding) : Results.NotFound();
});

app.MapPost("/findings/export", async (
    ExportFindingsRequest request,
    IFindingStore store,
    CancellationToken cancellationToken) =>
{
    var exportData = await store.ExportAsync(request, cancellationToken);
    return Results.Ok(exportData);
});

app.Run();

internal interface IFindingStore
{
    Task<FindingSearchResult> SearchAsync(SearchFindingsRequest request, CancellationToken cancellationToken);
    Task<FindingDto> CreateAsync(CreateFindingRequest request, CancellationToken cancellationToken);
    Task<FindingDto?> FindAsync(Guid findingId, CancellationToken cancellationToken);
    Task<FindingDto?> UpdateAsync(Guid findingId, UpdateFindingRequest request, CancellationToken cancellationToken);
    Task<TriageResult?> TriageAsync(Guid findingId, TriageFindingRequest request, CancellationToken cancellationToken);
    Task<FindingNoteDto?> AddNoteAsync(Guid findingId, AddFindingNoteRequest request, CancellationToken cancellationToken);
    Task<FindingDto?> UpdateTagsAsync(Guid findingId, UpdateFindingTagsRequest request, CancellationToken cancellationToken);
    Task<ExportResult> ExportAsync(ExportFindingsRequest request, CancellationToken cancellationToken);
}

internal sealed class InMemoryFindingStore : IFindingStore
{
    private readonly ConcurrentDictionary<Guid, FindingDto> _findings = new();
    private readonly ConcurrentDictionary<Guid, List<FindingNoteDto>> _notes = new();
    private readonly ConcurrentDictionary<Guid, List<Guid>> _evidence = new();

    public Task<FindingSearchResult> SearchAsync(SearchFindingsRequest request, CancellationToken cancellationToken)
    {
        var query = _findings.Values.AsEnumerable();

        if (request.ProgramId.HasValue)
            query = query.Where(f => f.ProgramId == request.ProgramId.Value);
        if (request.TargetId.HasValue)
            query = query.Where(f => f.TargetId == request.TargetId.Value);
        if (request.Severity.Count > 0)
            query = query.Where(f => request.Severity.Contains(f.Severity));
        if (request.Status.Count > 0)
            query = query.Where(f => request.Status.Contains(f.Status));
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(f => f.Title.Contains(request.Search, StringComparison.OrdinalIgnoreCase) || f.Description.Contains(request.Search, StringComparison.OrdinalIgnoreCase));
        if (request.Tags.Count > 0)
            query = query.Where(f => request.Tags.Any(t => f.Tags.Contains(t)));

        var total = query.Count();
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return Task.FromResult(new FindingSearchResult(items, page, pageSize, total));
    }

    public Task<FindingDto> CreateAsync(CreateFindingRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var finding = new FindingDto(
            FindingId: Guid.NewGuid(),
            TargetId: request.TargetId,
            ProgramId: request.ProgramId,
            Title: request.Title,
            Description: request.Description,
            Severity: request.Severity,
            Confidence: request.Confidence,
            Status: FindingStatus.New,
            AffectedAssetId: request.AffectedAssetId,
            SourceAssetId: request.SourceAssetId,
            SourceWorkerType: request.SourceWorkerType,
            SourceTaskRunId: request.SourceTaskRunId,
            SourceEventId: request.SourceEventId,
            CorrelationId: request.CorrelationId,
            EvidenceArtifactIds: request.EvidenceArtifactIds ?? [],
            Tags: request.Tags ?? [],
            Notes: null,
            FirstSeenAt: now,
            LastUpdatedAt: now);

        _findings[finding.FindingId] = finding;
        return Task.FromResult(finding);
    }

    public Task<FindingDto?> FindAsync(Guid findingId, CancellationToken cancellationToken)
    {
        _findings.TryGetValue(findingId, out var finding);
        return Task.FromResult(finding);
    }

    public Task<FindingDto?> UpdateAsync(Guid findingId, UpdateFindingRequest request, CancellationToken cancellationToken)
    {
        if (!_findings.TryGetValue(findingId, out var existing))
            return Task.FromResult<FindingDto?>(null);

        var updated = existing with
        {
            Title = request.Title ?? existing.Title,
            Description = request.Description ?? existing.Description,
            Severity = request.Severity ?? existing.Severity,
            Confidence = request.Confidence ?? existing.Confidence,
            Status = request.Status ?? existing.Status,
            Tags = request.Tags ?? existing.Tags,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        _findings[findingId] = updated;
        return Task.FromResult<FindingDto?>(updated);
    }

    public Task<TriageResult?> TriageAsync(Guid findingId, TriageFindingRequest request, CancellationToken cancellationToken)
    {
        if (!_findings.TryGetValue(findingId, out var existing))
            return Task.FromResult<TriageResult?>(null);

        var oldStatus = existing.Status;
        var updated = existing with { Status = request.NewStatus, LastUpdatedAt = DateTimeOffset.UtcNow };
        _findings[findingId] = updated;
        return Task.FromResult<TriageResult?>(new TriageResult(updated, oldStatus));
    }

    public Task<FindingNoteDto?> AddNoteAsync(Guid findingId, AddFindingNoteRequest request, CancellationToken cancellationToken)
    {
        if (!_findings.ContainsKey(findingId))
            return Task.FromResult<FindingNoteDto?>(null);

        var note = new FindingNoteDto(
            NoteId: Guid.NewGuid(),
            FindingId: findingId,
            UserId: request.UserId,
            Note: request.Note,
            CreatedAt: DateTimeOffset.UtcNow);

        if (!_notes.ContainsKey(findingId))
            _notes[findingId] = new List<FindingNoteDto>();
        _notes[findingId].Add(note);

        return Task.FromResult<FindingNoteDto?>(note);
    }

    public Task<FindingDto?> UpdateTagsAsync(Guid findingId, UpdateFindingTagsRequest request, CancellationToken cancellationToken)
    {
        if (!_findings.TryGetValue(findingId, out var existing))
            return Task.FromResult<FindingDto?>(null);

        var updated = existing with { Tags = request.Tags, LastUpdatedAt = DateTimeOffset.UtcNow };
        _findings[findingId] = updated;
        return Task.FromResult<FindingDto?>(updated);
    }

    public Task<ExportResult> ExportAsync(ExportFindingsRequest request, CancellationToken cancellationToken)
    {
        var findings = _findings.Values.AsEnumerable();

        if (request.ProgramId.HasValue)
            findings = findings.Where(f => f.ProgramId == request.ProgramId.Value);
        if (request.Severity.Count > 0)
            findings = findings.Where(f => request.Severity.Contains(f.Severity));
        if (request.Status.Count > 0)
            findings = findings.Where(f => request.Status.Contains(f.Status));

        var format = request.Format?.ToLowerInvariant() ?? "json";
        var content = format == "csv" ? ExportToCsv(findings.ToArray()) : ExportToJson(findings.ToArray());

        return Task.FromResult(new ExportResult(content, format, findings.Count()));
    }

    private static string ExportToJson(FindingDto[] findings) =>
        JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true });

    private static string ExportToCsv(FindingDto[] findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FindingId,TargetId,ProgramId,Title,Severity,Status,Confidence,FirstSeenAt,LastUpdatedAt");
        foreach (var f in findings)
            sb.AppendLine($"{f.FindingId},{f.TargetId},{f.ProgramId},{EscapeCsv(f.Title)},{f.Severity},{f.Status},{f.Confidence},{f.FirstSeenAt},{f.LastUpdatedAt}");
        return sb.ToString();
    }

    private static string EscapeCsv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}

internal sealed class EfFindingStore(FindingDbContext dbContext) : IFindingStore
{
    public async Task<FindingSearchResult> SearchAsync(SearchFindingsRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Findings.AsNoTracking().AsQueryable();

        if (request.ProgramId.HasValue)
            query = query.Where(f => f.ProgramId == request.ProgramId.Value);
        if (request.TargetId.HasValue)
            query = query.Where(f => f.TargetId == request.TargetId.Value);
        if (request.Severity.Count > 0)
            query = query.Where(f => request.Severity.Contains(f.Severity));
        if (request.Status.Count > 0)
            query = query.Where(f => request.Status.Contains(f.Status));
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(f => EF.Functions.ILike(f.Title, $"%{request.Search}%") || EF.Functions.ILike(f.Description, $"%{request.Search}%"));
        if (request.Tags.Count > 0)
            query = query.Where(f => f.TagsJson.Contains(request.Tags.First()));

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var items = await query.OrderByDescending(f => f.FirstSeenAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return new FindingSearchResult(items.Select(r => r.ToDto()).ToArray(), page, pageSize, total);
    }

    public async Task<FindingDto> CreateAsync(CreateFindingRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new FindingRecord
        {
            FindingId = Guid.NewGuid(),
            TargetId = request.TargetId,
            ProgramId = request.ProgramId,
            Title = request.Title,
            Description = request.Description,
            Severity = request.Severity,
            Confidence = request.Confidence,
            Status = FindingStatus.New,
            AffectedAssetId = request.AffectedAssetId,
            SourceAssetId = request.SourceAssetId,
            SourceWorkerType = request.SourceWorkerType,
            SourceTaskRunId = request.SourceTaskRunId,
            SourceEventId = request.SourceEventId,
            CorrelationId = request.CorrelationId,
            EvidenceArtifactIdsJson = JsonSerializer.Serialize(request.EvidenceArtifactIds ?? []),
            TagsJson = JsonSerializer.Serialize(request.Tags ?? []),
            FirstSeenAt = now,
            LastUpdatedAt = now
        };

        dbContext.Findings.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        return record.ToDto();
    }

    public async Task<FindingDto?> FindAsync(Guid findingId, CancellationToken cancellationToken)
    {
        var record = await dbContext.Findings.AsNoTracking().FirstOrDefaultAsync(f => f.FindingId == findingId, cancellationToken);
        return record?.ToDto();
    }

    public async Task<FindingDto?> UpdateAsync(Guid findingId, UpdateFindingRequest request, CancellationToken cancellationToken)
    {
        var record = await dbContext.Findings.FirstOrDefaultAsync(f => f.FindingId == findingId, cancellationToken);
        if (record is null) return null;

        if (request.Title is not null) record.Title = request.Title;
        if (request.Description is not null) record.Description = request.Description;
        if (request.Severity.HasValue) record.Severity = request.Severity.Value;
        if (request.Confidence.HasValue) record.Confidence = request.Confidence.Value;
        if (request.Status.HasValue) record.Status = request.Status.Value;
        if (request.Tags is not null) record.TagsJson = JsonSerializer.Serialize(request.Tags);
        record.LastUpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<TriageResult?> TriageAsync(Guid findingId, TriageFindingRequest request, CancellationToken cancellationToken)
    {
        var record = await dbContext.Findings.FirstOrDefaultAsync(f => f.FindingId == findingId, cancellationToken);
        if (record is null) return null;

        var oldStatus = record.Status;
        record.Status = request.NewStatus;
        record.LastUpdatedAt = DateTimeOffset.UtcNow;

        dbContext.TriageHistory.Add(new FindingTriageHistoryRecord
        {
            HistoryId = Guid.NewGuid(),
            FindingId = findingId,
            OldStatus = oldStatus.ToString(),
            NewStatus = request.NewStatus.ToString(),
            UserId = request.UserId,
            Reason = request.Reason,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new TriageResult(record.ToDto(), oldStatus);
    }

    public async Task<FindingNoteDto?> AddNoteAsync(Guid findingId, AddFindingNoteRequest request, CancellationToken cancellationToken)
    {
        if (!await dbContext.Findings.AnyAsync(f => f.FindingId == findingId, cancellationToken))
            return null;

        var note = new FindingNoteRecord
        {
            NoteId = Guid.NewGuid(),
            FindingId = findingId,
            UserId = request.UserId,
            Note = request.Note,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.Notes.Add(note);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new FindingNoteDto(note.NoteId, note.FindingId, note.UserId, note.Note, note.CreatedAt);
    }

    public async Task<FindingDto?> UpdateTagsAsync(Guid findingId, UpdateFindingTagsRequest request, CancellationToken cancellationToken)
    {
        var record = await dbContext.Findings.FirstOrDefaultAsync(f => f.FindingId == findingId, cancellationToken);
        if (record is null) return null;

        record.TagsJson = JsonSerializer.Serialize(request.Tags);
        record.LastUpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return record.ToDto();
    }

    public async Task<ExportResult> ExportAsync(ExportFindingsRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Findings.AsNoTracking().AsQueryable();

        if (request.ProgramId.HasValue)
            query = query.Where(f => f.ProgramId == request.ProgramId.Value);
        if (request.Severity.Count > 0)
            query = query.Where(f => request.Severity.Contains(f.Severity));
        if (request.Status.Count > 0)
            query = query.Where(f => request.Status.Contains(f.Status));

        var findings = await query.ToListAsync(cancellationToken);
        var format = request.Format?.ToLowerInvariant() ?? "json";
        var content = format == "csv" ? ExportToCsv(findings) : ExportToJson(findings);

        return new ExportResult(content, format, findings.Count);
    }

    private static string ExportToJson(List<FindingRecord> findings) =>
        JsonSerializer.Serialize(findings.Select(r => r.ToDto()).ToArray(), new JsonSerializerOptions { WriteIndented = true });

    private static string ExportToCsv(List<FindingRecord> findings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("FindingId,TargetId,ProgramId,Title,Severity,Status,Confidence,FirstSeenAt,LastUpdatedAt");
        foreach (var f in findings)
            sb.AppendLine($"{f.FindingId},{f.TargetId},{f.ProgramId},{EscapeCsv(f.Title)},{f.Severity},{f.Status},{f.Confidence},{f.FirstSeenAt},{f.LastUpdatedAt}");
        return sb.ToString();
    }

    private static string EscapeCsv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}

internal sealed class FindingDbContext(DbContextOptions<FindingDbContext> options) : DbContext(options)
{
    public DbSet<FindingRecord> Findings => Set<FindingRecord>();
    public DbSet<FindingNoteRecord> Notes => Set<FindingNoteRecord>();
    public DbSet<FindingEvidenceRecord> Evidence => Set<FindingEvidenceRecord>();
    public DbSet<FindingTriageHistoryRecord> TriageHistory => Set<FindingTriageHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var finding = modelBuilder.Entity<FindingRecord>();
        finding.ToTable("findings");
        finding.HasKey(f => f.FindingId);
        finding.HasIndex(f => f.ProgramId);
        finding.HasIndex(f => f.TargetId);
        finding.HasIndex(f => f.Severity);
        finding.HasIndex(f => f.Status);
        finding.HasIndex(f => f.FirstSeenAt);
        finding.Property(f => f.Severity).HasConversion<string>().HasMaxLength(32);
        finding.Property(f => f.Status).HasConversion<string>().HasMaxLength(32);
        finding.Property(f => f.TagsJson).HasColumnType("jsonb");
        finding.Property(f => f.EvidenceArtifactIdsJson).HasColumnType("jsonb");

        var note = modelBuilder.Entity<FindingNoteRecord>();
        note.ToTable("finding_notes");
        note.HasKey(n => n.NoteId);
        note.HasIndex(n => n.FindingId);
        note.Property(n => n.UserId).HasMaxLength(256);

        var evidence = modelBuilder.Entity<FindingEvidenceRecord>();
        evidence.ToTable("finding_evidence");
        evidence.HasKey(e => e.EvidenceId);
        evidence.HasIndex(e => e.FindingId);
        evidence.HasIndex(e => e.ArtifactId);

        var triage = modelBuilder.Entity<FindingTriageHistoryRecord>();
        triage.ToTable("finding_triage_history");
        triage.HasKey(t => t.HistoryId);
        triage.HasIndex(t => t.FindingId);
        triage.Property(t => t.OldStatus).HasMaxLength(32);
        triage.Property(t => t.NewStatus).HasMaxLength(32);
        triage.Property(t => t.UserId).HasMaxLength(256);
        triage.Property(t => t.Reason).HasMaxLength(1024);

        modelBuilder.ConfigureArgusOutbox();
    }
}

internal sealed class FindingRecord
{
    public Guid FindingId { get; set; }
    public Guid TargetId { get; set; }
    public Guid ProgramId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public FindingSeverity Severity { get; set; }
    public decimal Confidence { get; set; }
    public FindingStatus Status { get; set; }
    public Guid? AffectedAssetId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public string? SourceWorkerType { get; set; }
    public Guid? SourceTaskRunId { get; set; }
    public Guid? SourceEventId { get; set; }
    public Guid? CorrelationId { get; set; }
    public string EvidenceArtifactIdsJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    public IReadOnlyCollection<Guid> EvidenceArtifactIds =>
        JsonSerializer.Deserialize<Guid[]>(EvidenceArtifactIdsJson) ?? [];
    public IReadOnlyCollection<string> Tags =>
        JsonSerializer.Deserialize<string[]>(TagsJson) ?? [];

    public FindingDto ToDto() => new(
        FindingId,
        TargetId,
        ProgramId,
        Title,
        Description,
        Severity,
        Confidence,
        Status,
        AffectedAssetId,
        SourceAssetId,
        SourceWorkerType,
        SourceTaskRunId,
        SourceEventId,
        CorrelationId,
        EvidenceArtifactIds,
        Tags,
        null,
        FirstSeenAt,
        LastUpdatedAt);
}

internal sealed class FindingNoteRecord
{
    public Guid NoteId { get; set; }
    public Guid FindingId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class FindingEvidenceRecord
{
    public Guid EvidenceId { get; set; }
    public Guid FindingId { get; set; }
    public Guid ArtifactId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class FindingTriageHistoryRecord
{
    public Guid HistoryId { get; set; }
    public Guid FindingId { get; set; }
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed record TriageResult(FindingDto Finding, FindingStatus OldStatus);

internal static class FindingStoreInitialization
{
    public static async Task InitializeFindingStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<FindingDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}