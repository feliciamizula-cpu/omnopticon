using Argus.BuildingBlocks.EventBus;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Events;
using Argus.Contracts.Programs;
using Argus.ProgramScopeService;
using Argus.ProgramScopeService.Providers;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.ProgramScopeService");
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<ProgramScopeDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<ProgramScopeDbContext>();
    builder.Services.AddArgusInboxConsumer<ProgramScopeDbContext>();
    builder.Services.AddScoped<IProgramScopeStore, EfProgramScopeStore>();
}
else
{
    builder.Services.AddSingleton<IProgramScopeStore, InMemoryProgramScopeStore>();
}

builder.Services.AddHttpClient();
builder.Services.AddScoped<IScopeProvider, HackerOneScopeProvider>();
builder.Services.AddScoped<IScopeProvider, BugcrowdScopeProvider>();
builder.Services.AddHostedService<ScopeSyncService>();

var snapshotSigningKey = builder.Configuration["ARGUS_SNAPSHOT_SECRET_KEY"] ?? string.Empty;

var app = builder.Build();

await app.InitializeProgramScopeStoreAsync();
app.MapDefaultEndpoints();

app.MapGet("/programs", (IProgramScopeStore store, CancellationToken cancellationToken) =>
    store.GetProgramsAsync(cancellationToken));

app.MapPost("/programs", async (
    CreateProgramRequest request,
    IProgramScopeStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Program name is required.");
    }

    var program = await store.CreateProgramAsync(request, cancellationToken);
    await events.PublishAsync(
        new ProgramCreated(program.ProgramId, program.Name),
        nameof(ProgramCreated),
        "Argus.ProgramScopeService",
        cancellationToken: cancellationToken);

    return Results.Created($"/programs/{program.ProgramId}", program);
});

app.MapGet("/programs/{programId:guid}", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var program = await store.FindProgramAsync(programId, cancellationToken);
    return program is not null ? Results.Ok(program) : Results.NotFound();
});

app.MapDelete("/programs/{programId:guid}/scopes/{scopeId:guid}", async (
    Guid programId,
    Guid scopeId,
    IProgramScopeStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var deleted = await store.DeleteScopeAsync(programId, scopeId, cancellationToken);
    if (!deleted)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new ProgramScopeChanged(programId, scopeId, "deleted"),
        nameof(ProgramScopeChanged),
        "Argus.ProgramScopeService",
        cancellationToken: cancellationToken);

    return Results.NoContent();
});

app.MapPost("/programs/{programId:guid}/scopes", async (
    Guid programId,
    CreateProgramScopeRequest request,
    IProgramScopeStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (programId != request.ProgramId)
    {
        return Results.BadRequest("Route program id must match request program id.");
    }

    if (string.IsNullOrWhiteSpace(request.Pattern))
    {
        return Results.BadRequest("Scope pattern is required.");
    }

    var scope = await store.CreateScopeAsync(request, cancellationToken);

    if (scope is null)
    {
        return Results.NotFound();
    }

    await events.PublishAsync(
        new ScopeCreated(scope.ProgramId, scope.ScopeId, scope.Pattern, scope.ScopeType),
        nameof(ScopeCreated),
        "Argus.ProgramScopeService",
        cancellationToken: cancellationToken);

    await events.PublishAsync(
        new ProgramScopeChanged(programId, scope.ScopeId, "created"),
        nameof(ProgramScopeChanged),
        "Argus.ProgramScopeService",
        cancellationToken: cancellationToken);

    return Results.Created($"/programs/{programId}/scopes/{scope.ScopeId}", scope);
});

app.MapPost("/scope-validation/check", (ScopeValidationRequest request, IProgramScopeStore store, CancellationToken cancellationToken) =>
    store.ValidateAsync(request, cancellationToken));

app.MapGet("/programs/{programId:guid}/rules/revisions", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var revisions = await store.GetRuleRevisionsAsync(programId, cancellationToken);
    return Results.Ok(revisions);
});

app.MapPost("/programs/{programId:guid}/exclusions", async (
    Guid programId,
    CreateScopeExclusionRequest request,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var exclusion = await store.CreateScopeExclusionAsync(programId, request.Pattern, request.Reason, request.ExpiresAt, cancellationToken);
    return Results.Created($"/programs/{programId}/exclusions/{exclusion.ExclusionId}", exclusion);
});

app.MapGet("/programs/{programId:guid}/rate-limit-policies", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var policies = await store.GetRateLimitPoliciesAsync(programId, cancellationToken);
    return Results.Ok(policies);
});

app.MapPost("/programs/{programId:guid}/rate-limit-policies", async (
    Guid programId,
    CreateRateLimitPolicyRequest request,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var policy = await store.CreateRateLimitPolicyAsync(programId, request.ScopeId, request.BucketKey, request.Capacity, request.RefillRate, request.Source, cancellationToken);
    return Results.Created($"/programs/{programId}/rate-limit-policies/{policy.PolicyId}", policy);
});

app.MapGet("/programs/{programId:guid}/export", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var export = await store.ExportProgramAsync(programId, cancellationToken);
    return export is not null ? Results.Ok(export) : Results.NotFound();
});

app.MapPost("/programs/import", async (
    ProgramImportRequest request,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    if (request.Export is null)
    {
        return Results.BadRequest("Export data is required.");
    }

    var program = await store.ImportProgramAsync(request, cancellationToken);
    return Results.Created($"/programs/{program.ProgramId}", program);
});

app.MapGet("/programs/{programId:guid}/snapshot", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var snapshot = await store.GetSnapshotAsync(programId, cancellationToken);
    if (snapshot is null)
    {
        return Results.NotFound();
    }
    if (!string.IsNullOrEmpty(snapshotSigningKey))
    {
        var signedSnapshot = SnapshotSigner.CreateSignedSnapshot(
            snapshot.ProgramId,
            snapshot.Scopes,
            snapshot.Exclusions,
            snapshotSigningKey);
        return Results.Ok(signedSnapshot);
    }
    return Results.Ok(snapshot);
});

app.MapGet("/programs/{programId:guid}/export", async (
    Guid programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var export = await store.ExportProgramAsync(programId, cancellationToken);
    return export is not null ? Results.Ok(export) : Results.NotFound();
});

app.MapPost("/programs/import", async (
    ProgramImportRequest request,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    if (request.Export is null)
    {
        return Results.BadRequest("Export data is required.");
    }

    var program = await store.ImportProgramAsync(request, cancellationToken);
    return Results.Created($"/programs/{program.ProgramId}", program);
});

app.MapPost("/scope-validation/check-batch", async (
    ScopeBatchValidationRequest request,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var results = new List<ScopeValidationResult>();
    foreach (var target in request.Targets)
    {
        var result = await store.ValidateAsync(new ScopeValidationRequest(request.ProgramId, target, request.TargetType), cancellationToken);
        results.Add(result);
    }
    return Results.Ok(results);
});

app.Run();

internal interface IProgramScopeStore
{
    Task<IReadOnlyCollection<ProgramDto>> GetProgramsAsync(CancellationToken cancellationToken);
    Task<ProgramDto?> FindProgramAsync(Guid programId, CancellationToken cancellationToken);
    Task<ProgramDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProgramScopeDto>> GetScopesAsync(CancellationToken cancellationToken);
    Task<ProgramScopeDto?> CreateScopeAsync(CreateProgramScopeRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteScopeAsync(Guid programId, Guid scopeId, CancellationToken cancellationToken);
    Task<ScopeValidationResult> ValidateAsync(ScopeValidationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProgramRuleRevisionDto>> GetRuleRevisionsAsync(Guid programId, CancellationToken cancellationToken);
    Task<ScopeExclusionDto> CreateScopeExclusionAsync(Guid programId, string pattern, string? reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RateLimitPolicyDto>> GetRateLimitPoliciesAsync(Guid programId, CancellationToken cancellationToken);
    Task<RateLimitPolicyDto> CreateRateLimitPolicyAsync(Guid programId, Guid? scopeId, string bucketKey, int capacity, int refillRate, string source, CancellationToken cancellationToken);
    Task<ScopeSnapshot?> GetSnapshotAsync(Guid programId, CancellationToken cancellationToken);
    Task<ProgramExportDto?> ExportProgramAsync(Guid programId, CancellationToken cancellationToken);
    Task<ProgramDto> ImportProgramAsync(ProgramImportRequest request, CancellationToken cancellationToken);
}

internal sealed class InMemoryProgramScopeStore : IProgramScopeStore
{
    private readonly ConcurrentDictionary<Guid, InMemoryProgramRecord> _programs = new();

    public Task<IReadOnlyCollection<ProgramDto>> GetProgramsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ProgramDto> programs = _programs.Values
            .OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
            .Select(program => program.ToDto())
            .ToArray();

        return Task.FromResult(programs);
    }

    public Task<ProgramDto?> FindProgramAsync(Guid programId, CancellationToken cancellationToken)
    {
        var program = _programs.TryGetValue(programId, out var record) ? record.ToDto() : null;
        return Task.FromResult(program);
    }

    public Task<ProgramDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new InMemoryProgramRecord
        {
            ProgramId = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Source = string.IsNullOrWhiteSpace(request.Source) ? "custom" : request.Source.Trim(),
            ExternalUrl = request.ExternalUrl,
            CreatedAt = now,
            UpdatedAt = now
        };

        _programs[record.ProgramId] = record;

        return Task.FromResult(ToDto(record));
    }

    public Task<IReadOnlyCollection<ProgramScopeDto>> GetScopesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ProgramScopeDto> scopes = _programs.Values
            .SelectMany(program => program.Scopes)
            .OrderBy(scope => scope.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(scopes);
    }

    public Task<ProgramScopeDto?> CreateScopeAsync(CreateProgramScopeRequest request, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            return Task.FromResult<ProgramScopeDto?>(null);
        }

        var scope = new ProgramScopeDto(
            Guid.NewGuid(),
            request.ProgramId,
            NormalizeScopeType(request.ScopeType),
            NormalizePattern(request.Pattern),
            request.Action,
            request.Notes,
            DateTimeOffset.UtcNow);

        lock (program.Scopes)
        {
            program.Scopes.Add(scope);
            program.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.FromResult<ProgramScopeDto?>(scope);
    }

    public Task<bool> DeleteScopeAsync(Guid programId, Guid scopeId, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            return Task.FromResult(false);
        }

        lock (program.Scopes)
        {
            var scope = program.Scopes.FirstOrDefault(s => s.ScopeId == scopeId);
            if (scope is null)
            {
                return Task.FromResult(false);
            }

            program.Scopes.Remove(scope);
            program.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<ScopeValidationResult> ValidateAsync(ScopeValidationRequest request, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            return Task.FromResult(new ScopeValidationResult(request.ProgramId, request.Target, false, null, "Program was not found."));
        }

        return Task.FromResult(ScopeMatching.Validate(request, program.Scopes));
    }

    public Task<IReadOnlyCollection<ProgramRuleRevisionDto>> GetRuleRevisionsAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            return Task.FromResult<IReadOnlyCollection<ProgramRuleRevisionDto>>([]);
        }

        return Task.FromResult<IReadOnlyCollection<ProgramRuleRevisionDto>>(
            program.RuleRevisions
                .OrderByDescending(r => r.Version)
                .Select(r => new ProgramRuleRevisionDto(r.RevisionId, r.ProgramId, r.Version, r.ChangeType, r.OldValue, r.NewValue, r.ChangedBy, r.CreatedAt))
                .ToArray());
    }

    public Task<ScopeExclusionDto> CreateScopeExclusionAsync(Guid programId, string pattern, string? reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            throw new InvalidOperationException("Program not found.");
        }

        var exclusion = new ScopeExclusionRecord
        {
            ExclusionId = Guid.NewGuid(),
            ProgramId = programId,
            Pattern = pattern.Trim().ToLowerInvariant(),
            Reason = reason ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        };

        lock (program.ScopeExclusions)
        {
            program.ScopeExclusions.Add(exclusion);
        }

        return Task.FromResult(new ScopeExclusionDto(exclusion.ExclusionId, exclusion.ProgramId, exclusion.Pattern, exclusion.Reason, exclusion.CreatedAt, exclusion.ExpiresAt));
    }

    public Task<IReadOnlyCollection<RateLimitPolicyDto>> GetRateLimitPoliciesAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            return Task.FromResult<IReadOnlyCollection<RateLimitPolicyDto>>([]);
        }

        return Task.FromResult<IReadOnlyCollection<RateLimitPolicyDto>>(
            program.RateLimitPolicies
                .Select(p => new RateLimitPolicyDto(p.PolicyId, p.ProgramId, p.ScopeId, p.BucketKey, p.Capacity, p.RefillRate, p.Source))
                .ToArray());
    }

    public Task<RateLimitPolicyDto> CreateRateLimitPolicyAsync(Guid programId, Guid? scopeId, string bucketKey, int capacity, int refillRate, string source, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            throw new InvalidOperationException("Program not found.");
        }

        var policy = new RateLimitPolicyRecord
        {
            PolicyId = Guid.NewGuid(),
            ProgramId = programId,
            ScopeId = scopeId,
            BucketKey = bucketKey,
            Capacity = capacity,
            RefillRate = refillRate,
            Source = source
        };

        lock (program.RateLimitPolicies)
        {
            program.RateLimitPolicies.Add(policy);
        }

        return Task.FromResult(new RateLimitPolicyDto(policy.PolicyId, policy.ProgramId, policy.ScopeId, policy.BucketKey, policy.Capacity, policy.RefillRate, policy.Source));
    }

public Task<ScopeSnapshot?> GetSnapshotAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            return Task.FromResult<ScopeSnapshot?>(null);
        }

        var snapshot = new ScopeSnapshot(
            Guid.NewGuid(),
            programId,
            DateTimeOffset.UtcNow,
            program.Scopes,
            program.ScopeExclusions.Select(e => new ScopeExclusionDto(e.ExclusionId, e.ProgramId, e.Pattern, e.Reason, e.CreatedAt, e.ExpiresAt)).ToList(),
            string.Empty);

        return Task.FromResult<ScopeSnapshot?>(snapshot);
    }

    public Task<ProgramExportDto?> ExportProgramAsync(Guid programId, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(programId, out var program))
        {
            return Task.FromResult<ProgramExportDto?>(null);
        }

        var export = new ProgramExportDto(
            program.ProgramId,
            program.Name,
            program.Source,
            program.ExternalUrl,
            program.CreatedAt,
            program.UpdatedAt,
            program.Scopes,
            program.ScopeExclusions.Select(e => new ScopeExclusionDto(e.ExclusionId, e.ProgramId, e.Pattern, e.Reason, e.CreatedAt, e.ExpiresAt)).ToList(),
            program.RuleRevisions.Select(r => new ProgramRuleRevisionDto(r.RevisionId, r.ProgramId, r.Version, r.ChangeType, r.OldValue, r.NewValue, r.ChangedBy, r.CreatedAt)).ToList(),
            program.RateLimitPolicies.Select(p => new RateLimitPolicyDto(p.PolicyId, p.ProgramId, p.ScopeId, p.BucketKey, p.Capacity, p.RefillRate, p.Source)).ToList(),
            DateTimeOffset.UtcNow.ToString("O"),
            null);

        return Task.FromResult<ProgramExportDto?>(export);
    }

    public Task<ProgramDto> ImportProgramAsync(ProgramImportRequest request, CancellationToken cancellationToken)
    {
        var export = request.Export;
        var existingProgramId = request.ForceOverwrite && export.ProgramId != Guid.Empty ? export.ProgramId : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var record = new InMemoryProgramRecord
        {
            ProgramId = existingProgramId,
            Name = export.Name,
            Source = export.Source,
            ExternalUrl = export.ExternalUrl,
            CreatedAt = export.CreatedAt,
            UpdatedAt = now,
            Scopes = export.Scopes.ToList(),
            ScopeExclusions = export.Exclusions.Select(e => new ScopeExclusionRecord
            {
                ExclusionId = e.ExclusionId != Guid.Empty ? e.ExclusionId : Guid.NewGuid(),
                ProgramId = existingProgramId,
                Pattern = e.Pattern,
                Reason = e.Reason,
                CreatedAt = e.CreatedAt,
                ExpiresAt = e.ExpiresAt
            }).ToList(),
            RateLimitPolicies = export.RateLimitPolicies.Select(p => new RateLimitPolicyRecord
            {
                PolicyId = p.PolicyId != Guid.Empty ? p.PolicyId : Guid.NewGuid(),
                ProgramId = existingProgramId,
                ScopeId = p.ScopeId,
                BucketKey = p.BucketKey,
                Capacity = p.Capacity,
                RefillRate = p.RefillRate,
                Source = p.Source
            }).ToList()
        };

        foreach (var revision in export.RuleRevisions)
        {
            record.RuleRevisions.Add(new ProgramRuleRevisionRecord
            {
                RevisionId = revision.RevisionId != Guid.Empty ? revision.RevisionId : Guid.NewGuid(),
                ProgramId = existingProgramId,
                Version = revision.Version,
                ChangeType = revision.ChangeType,
                OldValue = revision.OldValue,
                NewValue = revision.NewValue,
                ChangedBy = revision.ChangedBy,
                CreatedAt = revision.CreatedAt
            });
        }

        _programs[existingProgramId] = record;
        return Task.FromResult(record.ToDto());
    }

    private static string NormalizeScopeType(string scopeType) =>
        string.IsNullOrWhiteSpace(scopeType) ? "domain" : scopeType.Trim();

    private static string NormalizePattern(string pattern) => pattern.Trim().ToLowerInvariant();

    private static ProgramDto ToDto(InMemoryProgramRecord record) => record.ToDto();
}

internal sealed class InMemoryProgramRecord
{
    public Guid ProgramId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "custom";
    public string? ExternalUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProgramScopeDto> Scopes { get; } = [];
    public List<ProgramRuleRevisionDto> RuleRevisions { get; } = [];
    public List<ScopeExclusionRecord> ScopeExclusions { get; } = [];
    public List<RateLimitPolicyRecord> RateLimitPolicies { get; } = [];

    public ProgramDto ToDto() =>
        new(ProgramId, Name, Source, ExternalUrl, CreatedAt, UpdatedAt, Scopes.ToArray());
}

internal sealed class EfProgramScopeStore(ProgramScopeDbContext dbContext) : IProgramScopeStore
{
    public async Task<IReadOnlyCollection<ProgramDto>> GetProgramsAsync(CancellationToken cancellationToken)
    {
        var programs = await dbContext.Programs
            .AsNoTracking()
            .Include(program => program.Scopes)
            .OrderBy(program => program.Name)
            .ToArrayAsync(cancellationToken);

        return programs.Select(program => program.ToDto()).ToArray();
    }

    public async Task<ProgramDto?> FindProgramAsync(Guid programId, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs
            .AsNoTracking()
            .Include(program => program.Scopes)
            .FirstOrDefaultAsync(program => program.ProgramId == programId, cancellationToken);

        return program?.ToDto();
    }

    public async Task<ProgramDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord
        {
            ProgramId = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Source = string.IsNullOrWhiteSpace(request.Source) ? "custom" : request.Source.Trim(),
            ExternalUrl = request.ExternalUrl,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.Programs.Add(program);
        await dbContext.SaveChangesAsync(cancellationToken);

        return program.ToDto();
    }

    public async Task<IReadOnlyCollection<ProgramScopeDto>> GetScopesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Scopes
            .AsNoTracking()
            .OrderBy(scope => scope.Pattern)
            .Select(scope => scope.ToDto())
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ProgramScopeDto?> CreateScopeAsync(CreateProgramScopeRequest request, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs.FirstOrDefaultAsync(program => program.ProgramId == request.ProgramId, cancellationToken);

        if (program is null)
        {
            return null;
        }

        var scope = new ProgramScopeRecord
        {
            ScopeId = Guid.NewGuid(),
            ProgramId = request.ProgramId,
            ScopeType = string.IsNullOrWhiteSpace(request.ScopeType) ? "domain" : request.ScopeType.Trim(),
            Pattern = request.Pattern.Trim().ToLowerInvariant(),
            Action = request.Action,
            Notes = request.Notes,
            CreatedAt = DateTimeOffset.UtcNow
        };

        program.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.Scopes.Add(scope);
        await dbContext.SaveChangesAsync(cancellationToken);

        return scope.ToDto();
    }

    public async Task<bool> DeleteScopeAsync(Guid programId, Guid scopeId, CancellationToken cancellationToken)
    {
        var scope = await dbContext.Scopes.FirstOrDefaultAsync(s => s.ProgramId == programId && s.ScopeId == scopeId, cancellationToken);
        if (scope is null)
        {
            return false;
        }

        var program = await dbContext.Programs.FirstOrDefaultAsync(p => p.ProgramId == programId, cancellationToken);
        if (program is null)
        {
            return false;
        }

        dbContext.Scopes.Remove(scope);
        program.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ScopeValidationResult> ValidateAsync(ScopeValidationRequest request, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Programs.AnyAsync(program => program.ProgramId == request.ProgramId, cancellationToken);

        if (!exists)
        {
            return new ScopeValidationResult(request.ProgramId, request.Target, false, null, "Program was not found.");
        }

        var scopes = await dbContext.Scopes
            .AsNoTracking()
            .Where(scope => scope.ProgramId == request.ProgramId)
            .Select(scope => scope.ToDto())
            .ToArrayAsync(cancellationToken);

        return ScopeMatching.Validate(request, scopes);
    }

    public async Task<IReadOnlyCollection<ProgramRuleRevisionDto>> GetRuleRevisionsAsync(Guid programId, CancellationToken cancellationToken)
    {
        return await dbContext.RuleRevisions
            .AsNoTracking()
            .Where(r => r.ProgramId == programId)
            .OrderByDescending(r => r.Version)
            .Select(r => new ProgramRuleRevisionDto(r.RevisionId, r.ProgramId, r.Version, r.ChangeType, r.OldValue, r.NewValue, r.ChangedBy, r.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ScopeExclusionDto> CreateScopeExclusionAsync(Guid programId, string pattern, string? reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs.FirstOrDefaultAsync(p => p.ProgramId == programId, cancellationToken);
        if (program is null)
        {
            throw new InvalidOperationException("Program not found.");
        }

        var exclusion = new ScopeExclusionRecord
        {
            ExclusionId = Guid.NewGuid(),
            ProgramId = programId,
            Pattern = pattern.Trim().ToLowerInvariant(),
            Reason = reason ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        };

        dbContext.ScopeExclusions.Add(exclusion);
        program.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ScopeExclusionDto(exclusion.ExclusionId, exclusion.ProgramId, exclusion.Pattern, exclusion.Reason, exclusion.CreatedAt, exclusion.ExpiresAt);
    }

    public async Task<IReadOnlyCollection<RateLimitPolicyDto>> GetRateLimitPoliciesAsync(Guid programId, CancellationToken cancellationToken)
    {
        return await dbContext.RateLimitPolicies
            .AsNoTracking()
            .Where(p => p.ProgramId == programId)
            .Select(p => new RateLimitPolicyDto(p.PolicyId, p.ProgramId, p.ScopeId, p.BucketKey, p.Capacity, p.RefillRate, p.Source))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<RateLimitPolicyDto> CreateRateLimitPolicyAsync(Guid programId, Guid? scopeId, string bucketKey, int capacity, int refillRate, string source, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs.FirstOrDefaultAsync(p => p.ProgramId == programId, cancellationToken);
        if (program is null)
        {
            throw new InvalidOperationException("Program not found.");
        }

        var policy = new RateLimitPolicyRecord
        {
            PolicyId = Guid.NewGuid(),
            ProgramId = programId,
            ScopeId = scopeId,
            BucketKey = bucketKey,
            Capacity = capacity,
            RefillRate = refillRate,
            Source = source
        };

        dbContext.RateLimitPolicies.Add(policy);
        program.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new RateLimitPolicyDto(policy.PolicyId, policy.ProgramId, policy.ScopeId, policy.BucketKey, policy.Capacity, policy.RefillRate, policy.Source);
    }

    public async Task<ScopeSnapshot?> GetSnapshotAsync(Guid programId, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs
            .AsNoTracking()
            .Include(p => p.Scopes)
            .Include(p => p.ScopeExclusions)
            .FirstOrDefaultAsync(p => p.ProgramId == programId, cancellationToken);

        if (program is null)
        {
            return null;
        }

        var snapshot = new ScopeSnapshot(
            Guid.NewGuid(),
            programId,
            DateTimeOffset.UtcNow,
            program.Scopes.Select(s => s.ToDto()).ToList(),
            program.ScopeExclusions.Select(e => new ScopeExclusionDto(e.ExclusionId, e.ProgramId, e.Pattern, e.Reason, e.CreatedAt, e.ExpiresAt)).ToList(),
            string.Empty);

        return snapshot;
    }
}

internal sealed class ProgramScopeDbContext(DbContextOptions<ProgramScopeDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<ProgramScopeRecord> Scopes => Set<ProgramScopeRecord>();
    public DbSet<ProgramRuleRevisionRecord> RuleRevisions => Set<ProgramRuleRevisionRecord>();
    public DbSet<ScopeExclusionRecord> ScopeExclusions => Set<ScopeExclusionRecord>();
    public DbSet<RateLimitPolicyRecord> RateLimitPolicies => Set<RateLimitPolicyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var program = modelBuilder.Entity<ProgramRecord>();
        program.ToTable("programs");
        program.HasKey(record => record.ProgramId);
        program.HasIndex(record => record.Name);
        program.Property(record => record.Name).HasMaxLength(256);
        program.Property(record => record.Source).HasMaxLength(128);
        program.Property(record => record.ExternalUrl).HasMaxLength(2048);
        program.HasMany(record => record.Scopes)
            .WithOne()
            .HasForeignKey(record => record.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        var scope = modelBuilder.Entity<ProgramScopeRecord>();
        scope.ToTable("program_scopes");
        scope.HasKey(record => record.ScopeId);
        scope.HasIndex(record => new { record.ProgramId, record.Pattern, record.Action });
        scope.Property(record => record.ScopeType).HasMaxLength(128);
        scope.Property(record => record.Pattern).HasMaxLength(2048);
        scope.Property(record => record.Action).HasConversion<string>().HasMaxLength(64);

        var revision = modelBuilder.Entity<ProgramRuleRevisionRecord>();
        revision.ToTable("program_rule_revisions");
        revision.HasKey(record => record.RevisionId);
        revision.HasIndex(record => record.ProgramId);
        revision.Property(record => record.ChangeType).HasMaxLength(64);
        revision.Property(record => record.ChangedBy).HasMaxLength(256);

        var exclusion = modelBuilder.Entity<ScopeExclusionRecord>();
        exclusion.ToTable("scope_exclusions");
        exclusion.HasKey(record => record.ExclusionId);
        exclusion.HasIndex(record => record.ProgramId);
        exclusion.Property(record => record.Pattern).HasMaxLength(2048);
        exclusion.Property(record => record.Reason).HasMaxLength(1024);

        var policy = modelBuilder.Entity<RateLimitPolicyRecord>();
        policy.ToTable("rate_limit_policies");
        policy.HasKey(record => record.PolicyId);
        policy.HasIndex(record => new { record.ProgramId, record.ScopeId });
        policy.Property(record => record.BucketKey).HasMaxLength(512);
        policy.Property(record => record.Source).HasMaxLength(256);

        modelBuilder.ConfigureArgusOutbox();
    }
}

internal sealed class ProgramRecord
{
    public Guid ProgramId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "custom";
    public string? ExternalUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProgramScopeRecord> Scopes { get; set; } = [];
    public List<ProgramRuleRevisionRecord> RuleRevisions { get; set; } = [];
    public List<ScopeExclusionRecord> ScopeExclusions { get; set; } = [];
    public List<RateLimitPolicyRecord> RateLimitPolicies { get; set; } = [];

    public ProgramDto ToDto() =>
        new(ProgramId, Name, Source, ExternalUrl, CreatedAt, UpdatedAt, Scopes.Select(scope => scope.ToDto()).ToArray());
}

internal sealed class ProgramScopeRecord
{
    public Guid ScopeId { get; set; }
    public Guid ProgramId { get; set; }
    public string ScopeType { get; set; } = "domain";
    public string Pattern { get; set; } = string.Empty;
    public ScopeRuleAction Action { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ProgramScopeDto ToDto() =>
        new(ScopeId, ProgramId, ScopeType, Pattern, Action, Notes, CreatedAt);
}

internal sealed class ProgramRuleRevisionRecord
{
    public Guid RevisionId { get; set; }
    public Guid ProgramId { get; set; }
    public int Version { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ScopeExclusionRecord
{
    public Guid ExclusionId { get; set; }
    public Guid ProgramId { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

internal sealed class RateLimitPolicyRecord
{
    public Guid PolicyId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public string BucketKey { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public int RefillRate { get; set; }
    public string Source { get; set; } = string.Empty;
}

internal static class ScopeMatching
{
    public static ScopeValidationResult Validate(ScopeValidationRequest request, IEnumerable<ProgramScopeDto> scopes)
    {
        var target = request.Target.Trim().ToLowerInvariant();
        ProgramScopeDto? includeMatch = null;

        foreach (var scope in scopes)
        {
            if (!Matches(scope.Pattern, target))
            {
                continue;
            }

            if (scope.Action == ScopeRuleAction.Exclude)
            {
                return new ScopeValidationResult(request.ProgramId, request.Target, false, scope.ScopeId, "Target matched an exclusion.");
            }

            includeMatch ??= scope;
        }

        return includeMatch is null
            ? new ScopeValidationResult(request.ProgramId, request.Target, false, null, "Target did not match an in-scope rule.")
            : new ScopeValidationResult(request.ProgramId, request.Target, true, includeMatch.ScopeId, "Target matched an in-scope rule.");
    }

    private static bool Matches(string pattern, string target)
    {
        var normalizedPattern = pattern.Trim().ToLowerInvariant();

        if (normalizedPattern.StartsWith("*."))
        {
            var suffix = normalizedPattern[1..];
            return target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && target.Length > suffix.Length;
        }

        return string.Equals(normalizedPattern, target, StringComparison.OrdinalIgnoreCase)
            || target.EndsWith($".{normalizedPattern}", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ProgramScopeStoreInitialization
{
    public static async Task InitializeProgramScopeStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<ProgramScopeDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}
