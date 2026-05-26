using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Argus.BuildingBlocks.EventBus;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Programs;
using Argus.ProgramScopeService;
using Argus.ProgramScopeService.Providers;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
<<<<<<< HEAD
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
=======
>>>>>>> 0877696314ffa86963c7ca31e842b6e53ad31f15

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

app.MapGet("/targets", (
    Guid? programId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
    store.GetTargetsAsync(programId, cancellationToken));

app.MapGet("/targets/{targetId:guid}", async (
    Guid targetId,
    IProgramScopeStore store,
    CancellationToken cancellationToken) =>
{
    var target = await store.FindTargetAsync(targetId, cancellationToken);
    return target is not null ? Results.Ok(target) : Results.NotFound();
});

app.MapPost("/targets", async (
    CreateTargetRequest request,
    IProgramScopeStore store,
    IHttpClientFactory httpClientFactory,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (request.ProgramId == Guid.Empty)
    {
        return Results.BadRequest("ProgramId is required.");
    }

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Target name is required.");
    }

    var rootDomains = TargetRequestHelpers.NormalizeDomains(request.RootDomains);
    if (rootDomains.Count == 0)
    {
        return Results.BadRequest("At least one root domain is required.");
    }

    if (await store.FindProgramAsync(request.ProgramId, cancellationToken) is null)
    {
        return Results.NotFound("Program not found.");
    }

    var target = await store.CreateTargetAsync(request with { RootDomains = rootDomains }, cancellationToken);
    await TargetAssetSeeder.SeedAsync(target, store, httpClientFactory, events, cancellationToken);

    return Results.Created($"/targets/{target.TargetId}", target);
});

app.MapPatch("/targets/{targetId:guid}", async (
    Guid targetId,
    UpdateTargetRequest request,
    IProgramScopeStore store,
    IHttpClientFactory httpClientFactory,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var target = await store.UpdateTargetAsync(targetId, request, cancellationToken);
    if (target is null)
    {
        return Results.NotFound();
    }

    await TargetAssetSeeder.SeedAsync(target, store, httpClientFactory, events, cancellationToken);
    return Results.Ok(target);
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
    var exported = await store.ExportProgramAsync(programId, cancellationToken);
    if (exported is null)
    {
        return Results.NotFound();
    }
    if (!string.IsNullOrEmpty(snapshotSigningKey))
    {
        var signature = SnapshotSigner.ComputeSignature(exported, snapshotSigningKey);
        var signedExport = exported with { Signature = signature };
        return Results.Ok(signedExport);
    }
    return Results.Ok(exported);
});

app.MapPost("/programs/import", async (
    ProgramImportRequest request,
    IProgramScopeStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    try
    {
        var program = await store.ImportProgramAsync(request, cancellationToken);
        await events.PublishAsync(
            new ProgramCreated(program.ProgramId, program.Name),
            nameof(ProgramCreated),
            "Argus.ProgramScopeService",
            cancellationToken: cancellationToken);
        return Results.Created($"/programs/{program.ProgramId}", program);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
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
    Task<IReadOnlyCollection<TargetDto>> GetTargetsAsync(Guid? programId, CancellationToken cancellationToken);
    Task<TargetDto?> FindTargetAsync(Guid targetId, CancellationToken cancellationToken);
    Task<TargetDto> CreateTargetAsync(CreateTargetRequest request, CancellationToken cancellationToken);
    Task<TargetDto?> UpdateTargetAsync(Guid targetId, UpdateTargetRequest request, CancellationToken cancellationToken);
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

    public Task<IReadOnlyCollection<TargetDto>> GetTargetsAsync(Guid? programId, CancellationToken cancellationToken)
    {
        var targets = _programs.Values
            .Where(program => !programId.HasValue || program.ProgramId == programId.Value)
            .SelectMany(program => program.Targets)
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<TargetDto>>(targets);
    }

    public Task<TargetDto?> FindTargetAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var target = _programs.Values
            .SelectMany(program => program.Targets)
            .FirstOrDefault(target => target.TargetId == targetId);

        return Task.FromResult(target);
    }

    public Task<TargetDto> CreateTargetAsync(CreateTargetRequest request, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            throw new InvalidOperationException("Program not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var target = new TargetDto(
            Guid.NewGuid(),
            request.ProgramId,
            request.Name.Trim(),
            TargetRequestHelpers.CreateSlug(request.Name),
            request.Description,
            TargetStatus.Active,
            TargetRequestHelpers.NormalizeDomains(request.RootDomains),
            TargetRequestHelpers.NormalizeProtocols(request.AllowedProtocols),
            request.DefaultRateLimitPolicyId,
            request.ProxyProfileId,
            request.ReconProfileId,
            now,
            now);

        lock (program.Targets)
        {
            program.Targets.Add(target);
            program.UpdatedAt = now;
        }

        return Task.FromResult(target);
    }

    public Task<TargetDto?> UpdateTargetAsync(Guid targetId, UpdateTargetRequest request, CancellationToken cancellationToken)
    {
        foreach (var program in _programs.Values)
        {
            lock (program.Targets)
            {
                var index = program.Targets.FindIndex(target => target.TargetId == targetId);
                if (index < 0)
                {
                    continue;
                }

                var existing = program.Targets[index];
                var updated = existing with
                {
                    Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                    Slug = string.IsNullOrWhiteSpace(request.Name) ? existing.Slug : TargetRequestHelpers.CreateSlug(request.Name),
                    Description = request.Description ?? existing.Description,
                    Status = request.Status ?? existing.Status,
                    RootDomains = request.RootDomains is null ? existing.RootDomains : TargetRequestHelpers.NormalizeDomains(request.RootDomains),
                    AllowedProtocols = request.AllowedProtocols is null ? existing.AllowedProtocols : TargetRequestHelpers.NormalizeProtocols(request.AllowedProtocols),
                    DefaultRateLimitPolicyId = request.DefaultRateLimitPolicyId ?? existing.DefaultRateLimitPolicyId,
                    ProxyProfileId = request.ProxyProfileId ?? existing.ProxyProfileId,
                    ReconProfileId = request.ReconProfileId ?? existing.ReconProfileId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                program.Targets[index] = updated;
                program.UpdatedAt = updated.UpdatedAt;
                return Task.FromResult<TargetDto?>(updated);
            }
        }

        return Task.FromResult<TargetDto?>(null);
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
            return Task.FromResult(new ScopeValidationResult(request.ProgramId, request.Target, false, null, ScopeValidationReason.NoMatchingInclude, RulePattern: null));
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
            record.RuleRevisions.Add(revision);
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
    public List<ProgramScopeDto> Scopes { get; init; } = [];
    public List<TargetDto> Targets { get; init; } = [];
    public List<ProgramRuleRevisionDto> RuleRevisions { get; init; } = [];
    public List<ScopeExclusionRecord> ScopeExclusions { get; init; } = [];
    public List<RateLimitPolicyRecord> RateLimitPolicies { get; init; } = [];

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

    public async Task<IReadOnlyCollection<TargetDto>> GetTargetsAsync(Guid? programId, CancellationToken cancellationToken)
    {
        var query = dbContext.Targets.AsNoTracking();

        if (programId.HasValue)
        {
            query = query.Where(target => target.ProgramId == programId.Value);
        }

        var targets = await query
            .OrderBy(target => target.Name)
            .ToArrayAsync(cancellationToken);

        return targets.Select(target => target.ToDto()).ToArray();
    }

    public async Task<TargetDto?> FindTargetAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var target = await dbContext.Targets
            .AsNoTracking()
            .FirstOrDefaultAsync(target => target.TargetId == targetId, cancellationToken);

        return target?.ToDto();
    }

    public async Task<TargetDto> CreateTargetAsync(CreateTargetRequest request, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs.FirstOrDefaultAsync(program => program.ProgramId == request.ProgramId, cancellationToken)
            ?? throw new InvalidOperationException("Program not found.");

        var now = DateTimeOffset.UtcNow;
        var target = TargetRecord.From(request, now);
        dbContext.Targets.Add(target);
        program.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        return target.ToDto();
    }

    public async Task<TargetDto?> UpdateTargetAsync(Guid targetId, UpdateTargetRequest request, CancellationToken cancellationToken)
    {
        var target = await dbContext.Targets.FirstOrDefaultAsync(target => target.TargetId == targetId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        target.Apply(request);
        var program = await dbContext.Programs.FirstOrDefaultAsync(program => program.ProgramId == target.ProgramId, cancellationToken);
        if (program is not null)
        {
            program.UpdatedAt = target.UpdatedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return target.ToDto();
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
            return new ScopeValidationResult(request.ProgramId, request.Target, false, null, ScopeValidationReason.NoMatchingInclude, RulePattern: null);
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

    public async Task<ProgramExportDto?> ExportProgramAsync(Guid programId, CancellationToken cancellationToken)
    {
        var program = await dbContext.Programs
            .AsNoTracking()
            .Include(p => p.Scopes)
            .Include(p => p.ScopeExclusions)
            .Include(p => p.RuleRevisions)
            .Include(p => p.RateLimitPolicies)
            .FirstOrDefaultAsync(p => p.ProgramId == programId, cancellationToken);

        if (program is null)
        {
            return null;
        }

        return new ProgramExportDto(
            program.ProgramId,
            program.Name,
            program.Source,
            program.ExternalUrl,
            program.CreatedAt,
            program.UpdatedAt,
            program.Scopes.Select(s => s.ToDto()).ToArray(),
            program.ScopeExclusions.Select(e => new ScopeExclusionDto(e.ExclusionId, e.ProgramId, e.Pattern, e.Reason, e.CreatedAt, e.ExpiresAt)).ToArray(),
            program.RuleRevisions.Select(r => new ProgramRuleRevisionDto(r.RevisionId, r.ProgramId, r.Version, r.ChangeType, r.OldValue, r.NewValue, r.ChangedBy, r.CreatedAt)).ToArray(),
            program.RateLimitPolicies.Select(p => new RateLimitPolicyDto(p.PolicyId, p.ProgramId, p.ScopeId, p.BucketKey, p.Capacity, p.RefillRate, p.Source)).ToArray(),
            DateTimeOffset.UtcNow.ToString("O"),
            null);
    }

    public async Task<ProgramDto> ImportProgramAsync(ProgramImportRequest request, CancellationToken cancellationToken)
    {
        var export = request.Export;
        var existingProgram = await dbContext.Programs.FirstOrDefaultAsync(p => p.ProgramId == export.ProgramId, cancellationToken);

        if (existingProgram is not null)
        {
            if (!request.ForceOverwrite)
            {
                throw new InvalidOperationException("Program already exists. Use ForceOverwrite to replace.");
            }

            dbContext.Scopes.RemoveRange(dbContext.Scopes.Where(s => s.ProgramId == export.ProgramId));
            dbContext.ScopeExclusions.RemoveRange(dbContext.ScopeExclusions.Where(e => e.ProgramId == export.ProgramId));
            dbContext.RuleRevisions.RemoveRange(dbContext.RuleRevisions.Where(r => r.ProgramId == export.ProgramId));
            dbContext.RateLimitPolicies.RemoveRange(dbContext.RateLimitPolicies.Where(p => p.ProgramId == export.ProgramId));

            existingProgram.Name = export.Name;
            existingProgram.Source = export.Source;
            existingProgram.ExternalUrl = export.ExternalUrl;
            existingProgram.UpdatedAt = DateTimeOffset.UtcNow;

            foreach (var scope in export.Scopes)
            {
                dbContext.Scopes.Add(new ProgramScopeRecord
                {
                    ScopeId = scope.ScopeId,
                    ProgramId = scope.ProgramId,
                    ScopeType = scope.ScopeType,
                    Pattern = scope.Pattern,
                    Action = scope.Action,
                    Notes = scope.Notes,
                    CreatedAt = scope.CreatedAt
                });
            }

            foreach (var exclusion in export.Exclusions)
            {
                dbContext.ScopeExclusions.Add(new ScopeExclusionRecord
                {
                    ExclusionId = exclusion.ExclusionId,
                    ProgramId = exclusion.ProgramId,
                    Pattern = exclusion.Pattern,
                    Reason = exclusion.Reason,
                    CreatedAt = exclusion.CreatedAt,
                    ExpiresAt = exclusion.ExpiresAt
                });
            }

            foreach (var revision in export.RuleRevisions)
            {
                dbContext.RuleRevisions.Add(new ProgramRuleRevisionRecord
                {
                    RevisionId = revision.RevisionId,
                    ProgramId = revision.ProgramId,
                    Version = revision.Version,
                    ChangeType = revision.ChangeType,
                    OldValue = revision.OldValue,
                    NewValue = revision.NewValue,
                    ChangedBy = revision.ChangedBy,
                    CreatedAt = revision.CreatedAt
                });
            }

            foreach (var policy in export.RateLimitPolicies)
            {
                dbContext.RateLimitPolicies.Add(new RateLimitPolicyRecord
                {
                    PolicyId = policy.PolicyId,
                    ProgramId = policy.ProgramId,
                    ScopeId = policy.ScopeId,
                    BucketKey = policy.BucketKey,
                    Capacity = policy.Capacity,
                    RefillRate = policy.RefillRate,
                    Source = policy.Source
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return existingProgram.ToDto();
        }

        var newProgram = new ProgramRecord
        {
            ProgramId = export.ProgramId != Guid.Empty ? export.ProgramId : Guid.NewGuid(),
            Name = export.Name,
            Source = export.Source,
            ExternalUrl = export.ExternalUrl,
            CreatedAt = DateTimeOffset.TryParse(export.ExportedAt, out var parsed) ? parsed : DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.Programs.Add(newProgram);

        foreach (var scope in export.Scopes)
        {
            dbContext.Scopes.Add(new ProgramScopeRecord
            {
                ScopeId = scope.ScopeId,
                ProgramId = newProgram.ProgramId,
                ScopeType = scope.ScopeType,
                Pattern = scope.Pattern,
                Action = scope.Action,
                Notes = scope.Notes,
                CreatedAt = scope.CreatedAt
            });
        }

        foreach (var exclusion in export.Exclusions)
        {
            dbContext.ScopeExclusions.Add(new ScopeExclusionRecord
            {
                ExclusionId = exclusion.ExclusionId,
                ProgramId = newProgram.ProgramId,
                Pattern = exclusion.Pattern,
                Reason = exclusion.Reason,
                CreatedAt = exclusion.CreatedAt,
                ExpiresAt = exclusion.ExpiresAt
            });
        }

        foreach (var revision in export.RuleRevisions)
        {
            dbContext.RuleRevisions.Add(new ProgramRuleRevisionRecord
            {
                RevisionId = revision.RevisionId,
                ProgramId = newProgram.ProgramId,
                Version = revision.Version,
                ChangeType = revision.ChangeType,
                OldValue = revision.OldValue,
                NewValue = revision.NewValue,
                ChangedBy = revision.ChangedBy,
                CreatedAt = revision.CreatedAt
            });
        }

        foreach (var policy in export.RateLimitPolicies)
        {
            dbContext.RateLimitPolicies.Add(new RateLimitPolicyRecord
            {
                PolicyId = policy.PolicyId,
                ProgramId = newProgram.ProgramId,
                ScopeId = policy.ScopeId,
                BucketKey = policy.BucketKey,
                Capacity = policy.Capacity,
                RefillRate = policy.RefillRate,
                Source = policy.Source
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return newProgram.ToDto();
    }
}

internal sealed class ProgramScopeDbContext(DbContextOptions<ProgramScopeDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<TargetRecord> Targets => Set<TargetRecord>();
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
        program.HasMany(record => record.Targets)
            .WithOne()
            .HasForeignKey(record => record.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        var target = modelBuilder.Entity<TargetRecord>();
        target.ToTable("targets");
        target.HasKey(record => record.TargetId);
        target.HasIndex(record => new { record.ProgramId, record.Slug }).IsUnique();
        target.Property(record => record.Name).HasMaxLength(256);
        target.Property(record => record.Slug).HasMaxLength(256);
        target.Property(record => record.Description).HasMaxLength(2048);
        target.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
        target.Property(record => record.RootDomainsJson).HasColumnType("jsonb");
        target.Property(record => record.AllowedProtocolsJson).HasColumnType("jsonb");

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
    public List<TargetRecord> Targets { get; set; } = [];
    public List<ProgramRuleRevisionRecord> RuleRevisions { get; set; } = [];
    public List<ScopeExclusionRecord> ScopeExclusions { get; set; } = [];
    public List<RateLimitPolicyRecord> RateLimitPolicies { get; set; } = [];

    public ProgramDto ToDto() =>
        new(ProgramId, Name, Source, ExternalUrl, CreatedAt, UpdatedAt, Scopes.Select(scope => scope.ToDto()).ToArray());
}

internal sealed class TargetRecord
{
    public Guid TargetId { get; set; }
    public Guid ProgramId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TargetStatus Status { get; set; } = TargetStatus.Active;
    public string RootDomainsJson { get; set; } = "[]";
    public string AllowedProtocolsJson { get; set; } = "[]";
    public Guid? DefaultRateLimitPolicyId { get; set; }
    public Guid? ProxyProfileId { get; set; }
    public Guid? ReconProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyCollection<string> RootDomains =>
        JsonSerializer.Deserialize<string[]>(RootDomainsJson) ?? Array.Empty<string>();

    public IReadOnlyCollection<string> AllowedProtocols =>
        JsonSerializer.Deserialize<string[]>(AllowedProtocolsJson) ?? Array.Empty<string>();

    public static TargetRecord From(CreateTargetRequest request, DateTimeOffset now) => new()
    {
        TargetId = Guid.NewGuid(),
        ProgramId = request.ProgramId,
        Name = request.Name.Trim(),
        Slug = TargetRequestHelpers.CreateSlug(request.Name),
        Description = request.Description,
        Status = TargetStatus.Active,
        RootDomainsJson = JsonSerializer.Serialize(TargetRequestHelpers.NormalizeDomains(request.RootDomains)),
        AllowedProtocolsJson = JsonSerializer.Serialize(TargetRequestHelpers.NormalizeProtocols(request.AllowedProtocols)),
        DefaultRateLimitPolicyId = request.DefaultRateLimitPolicyId,
        ProxyProfileId = request.ProxyProfileId,
        ReconProfileId = request.ReconProfileId,
        CreatedAt = now,
        UpdatedAt = now
    };

    public void Apply(UpdateTargetRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            Name = request.Name.Trim();
            Slug = TargetRequestHelpers.CreateSlug(request.Name);
        }

        if (request.Description is not null) Description = request.Description;
        if (request.Status.HasValue) Status = request.Status.Value;
        if (request.RootDomains is not null) RootDomainsJson = JsonSerializer.Serialize(TargetRequestHelpers.NormalizeDomains(request.RootDomains));
        if (request.AllowedProtocols is not null) AllowedProtocolsJson = JsonSerializer.Serialize(TargetRequestHelpers.NormalizeProtocols(request.AllowedProtocols));
        if (request.DefaultRateLimitPolicyId.HasValue) DefaultRateLimitPolicyId = request.DefaultRateLimitPolicyId;
        if (request.ProxyProfileId.HasValue) ProxyProfileId = request.ProxyProfileId;
        if (request.ReconProfileId.HasValue) ReconProfileId = request.ReconProfileId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public TargetDto ToDto() => new(
        TargetId,
        ProgramId,
        Name,
        Slug,
        Description,
        Status,
        RootDomains,
        AllowedProtocols,
        DefaultRateLimitPolicyId,
        ProxyProfileId,
        ReconProfileId,
        CreatedAt,
        UpdatedAt);
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

internal static class TargetRequestHelpers
{
    public static IReadOnlyCollection<string> NormalizeDomains(IReadOnlyCollection<string>? domains) =>
        (domains ?? Array.Empty<string>())
            .Select(NormalizeDomain)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyCollection<string> NormalizeProtocols(IReadOnlyCollection<string>? protocols)
    {
        var normalized = (protocols ?? new[] { "https", "http" })
            .Select(protocol => protocol.Trim().TrimEnd(':', '/').ToLowerInvariant())
            .Where(protocol => protocol is "http" or "https")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? new[] { "https", "http" } : normalized;
    }

    public static string CreateSlug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();

        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? Guid.NewGuid().ToString("N") : slug;
    }

    private static string NormalizeDomain(string domain)
    {
        var value = domain.Trim().ToLowerInvariant();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.Host;
        }

        value = value.Trim().TrimStart('*').TrimStart('.').TrimEnd('.');
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? value[..slash] : value;
    }
}

internal static class TargetAssetSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> SeedAsync(
        TargetDto target,
        IProgramScopeStore store,
        IHttpClientFactory httpClientFactory,
        IIntegrationEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (target.Status != TargetStatus.Active)
        {
            return 0;
        }

        var existingScopes = await store.GetScopesAsync(cancellationToken);
        var assetClient = httpClientFactory.CreateClient();
        assetClient.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

        var seeded = 0;
        foreach (var domain in target.RootDomains)
        {
            var scope = existingScopes.FirstOrDefault(existing =>
                existing.ProgramId == target.ProgramId
                && existing.Action == ScopeRuleAction.Include
                && string.Equals(existing.Pattern, domain, StringComparison.OrdinalIgnoreCase));

            if (scope is null)
            {
                scope = await store.CreateScopeAsync(
                    new CreateProgramScopeRequest(target.ProgramId, "domain", domain, ScopeRuleAction.Include, $"Root domain for target {target.Name}"),
                    cancellationToken);

                if (scope is not null)
                {
                    await events.PublishAsync(
                        new ScopeCreated(scope.ProgramId, scope.ScopeId, scope.Pattern, scope.ScopeType),
                        nameof(ScopeCreated),
                        "Argus.ProgramScopeService",
                        cancellationToken: cancellationToken);

                    await events.PublishAsync(
                        new ProgramScopeChanged(scope.ProgramId, scope.ScopeId, "created"),
                        nameof(ProgramScopeChanged),
                        "Argus.ProgramScopeService",
                        cancellationToken: cancellationToken);
                }
            }

            var createAsset = new CreateAssetRequest(
                target.ProgramId,
                scope?.ScopeId,
                AssetType.Domain,
                domain,
                "TargetRootDomain",
                1.0m,
                $"target:{target.TargetId:N}",
                new Dictionary<string, string>
                {
                    ["target_id"] = target.TargetId.ToString("N"),
                    ["target_slug"] = target.Slug,
                    ["target_name"] = target.Name
                },
                ["target-root", "seed", "domain"]);

            using var response = await assetClient.PostAsJsonAsync("/assets", createAsset, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                seeded++;
            }
        }

        return seeded;
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
                return new ScopeValidationResult(request.ProgramId, request.Target, false, scope.ScopeId, ScopeValidationReason.ExcludedByRule, RulePattern: scope.Pattern);
            }

            includeMatch ??= scope;
        }

        return includeMatch is null
            ? new ScopeValidationResult(request.ProgramId, request.Target, false, null, ScopeValidationReason.NoMatchingInclude, RulePattern: null)
            : new ScopeValidationResult(request.ProgramId, request.Target, true, includeMatch.ScopeId, ScopeValidationReason.IncludedByRule, RulePattern: includeMatch.Pattern);
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
<<<<<<< HEAD
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.ExecuteSqlRawAsync("""
=======
            // EnsureCreatedAsync is a no-op when the DB already exists (e.g. created by another service).
            // Create all tables explicitly with IF NOT EXISTS so initialization is idempotent.
            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS programs (
                    "ProgramId" uuid PRIMARY KEY,
                    "Name" character varying(256) NOT NULL,
                    "Source" character varying(128) NOT NULL,
                    "ExternalUrl" character varying(2048) NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_programs_Name" ON programs ("Name");

                CREATE TABLE IF NOT EXISTS program_scopes (
                    "ScopeId" uuid PRIMARY KEY,
                    "ProgramId" uuid NOT NULL REFERENCES programs("ProgramId") ON DELETE CASCADE,
                    "ScopeType" character varying(128) NOT NULL,
                    "Pattern" character varying(2048) NOT NULL,
                    "Action" character varying(64) NOT NULL,
                    "Notes" text NULL,
                    "CreatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_program_scopes_ProgramId_Pattern_Action"
                    ON program_scopes ("ProgramId", "Pattern", "Action");

                CREATE TABLE IF NOT EXISTS program_rule_revisions (
                    "RevisionId" uuid PRIMARY KEY,
                    "ProgramId" uuid NOT NULL,
                    "Version" integer NOT NULL,
                    "ChangeType" character varying(64) NOT NULL,
                    "OldValue" text NULL,
                    "NewValue" text NULL,
                    "ChangedBy" character varying(256) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_program_rule_revisions_ProgramId"
                    ON program_rule_revisions ("ProgramId");

                CREATE TABLE IF NOT EXISTS scope_exclusions (
                    "ExclusionId" uuid PRIMARY KEY,
                    "ProgramId" uuid NOT NULL,
                    "Pattern" character varying(2048) NOT NULL,
                    "Reason" character varying(1024) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_scope_exclusions_ProgramId"
                    ON scope_exclusions ("ProgramId");

                CREATE TABLE IF NOT EXISTS rate_limit_policies (
                    "PolicyId" uuid PRIMARY KEY,
                    "ProgramId" uuid NOT NULL,
                    "ScopeId" uuid NULL,
                    "BucketKey" character varying(512) NOT NULL,
                    "Capacity" integer NOT NULL,
                    "RefillRate" integer NOT NULL,
                    "Source" character varying(256) NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_rate_limit_policies_ProgramId_ScopeId"
                    ON rate_limit_policies ("ProgramId", "ScopeId");

>>>>>>> 0877696314ffa86963c7ca31e842b6e53ad31f15
                CREATE TABLE IF NOT EXISTS targets (
                    "TargetId" uuid PRIMARY KEY,
                    "ProgramId" uuid NOT NULL REFERENCES programs("ProgramId") ON DELETE CASCADE,
                    "Name" character varying(256) NOT NULL,
                    "Slug" character varying(256) NOT NULL,
                    "Description" character varying(2048) NULL,
                    "Status" character varying(64) NOT NULL,
                    "RootDomainsJson" jsonb NOT NULL DEFAULT '[]'::jsonb,
                    "AllowedProtocolsJson" jsonb NOT NULL DEFAULT '[]'::jsonb,
                    "DefaultRateLimitPolicyId" uuid NULL,
                    "ProxyProfileId" uuid NULL,
                    "ReconProfileId" uuid NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_targets_ProgramId_Slug"
                    ON targets ("ProgramId", "Slug");
                """);
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}
