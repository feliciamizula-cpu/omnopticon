using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Programs;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.ProgramScopeService");
builder.Services.AddProblemDetails();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<ProgramScopeDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<ProgramScopeDbContext>();
    builder.Services.AddScoped<IProgramScopeStore, EfProgramScopeStore>();
}
else
{
    builder.Services.AddSingleton<IProgramScopeStore, InMemoryProgramScopeStore>();
}

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

app.MapGet("/scopes", (IProgramScopeStore store, CancellationToken cancellationToken) =>
    store.GetScopesAsync(cancellationToken));

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

    return Results.Created($"/programs/{programId}/scopes/{scope.ScopeId}", scope);
});

app.MapPost("/scope-validation/check", (ScopeValidationRequest request, IProgramScopeStore store, CancellationToken cancellationToken) =>
    store.ValidateAsync(request, cancellationToken));

app.Run();

internal interface IProgramScopeStore
{
    Task<IReadOnlyCollection<ProgramDto>> GetProgramsAsync(CancellationToken cancellationToken);
    Task<ProgramDto?> FindProgramAsync(Guid programId, CancellationToken cancellationToken);
    Task<ProgramDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProgramScopeDto>> GetScopesAsync(CancellationToken cancellationToken);
    Task<ProgramScopeDto?> CreateScopeAsync(CreateProgramScopeRequest request, CancellationToken cancellationToken);
    Task<ScopeValidationResult> ValidateAsync(ScopeValidationRequest request, CancellationToken cancellationToken);
}

internal sealed class InMemoryProgramScopeStore : IProgramScopeStore
{
    private readonly ConcurrentDictionary<Guid, ProgramRecord> _programs = new();

    public Task<IReadOnlyCollection<ProgramDto>> GetProgramsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ProgramDto> programs = _programs.Values
            .OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToArray();

        return Task.FromResult(programs);
    }

    public Task<ProgramDto?> FindProgramAsync(Guid programId, CancellationToken cancellationToken)
    {
        var program = _programs.TryGetValue(programId, out var record) ? ToDto(record) : null;
        return Task.FromResult(program);
    }

    public Task<ProgramDto> CreateProgramAsync(CreateProgramRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProgramRecord(
            Guid.NewGuid(),
            request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.Source) ? "custom" : request.Source.Trim(),
            request.ExternalUrl,
            now,
            now,
            []);

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

    public Task<ScopeValidationResult> ValidateAsync(ScopeValidationRequest request, CancellationToken cancellationToken)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            return Task.FromResult(new ScopeValidationResult(request.ProgramId, request.Target, false, null, "Program was not found."));
        }

        return Task.FromResult(ScopeMatching.Validate(request, program.Scopes));
    }

    private static ProgramDto ToDto(ProgramRecord record) =>
        new(
            record.ProgramId,
            record.Name,
            record.Source,
            record.ExternalUrl,
            record.CreatedAt,
            record.UpdatedAt,
            record.Scopes.ToArray());

    private sealed record ProgramRecord(
        Guid ProgramId,
        string Name,
        string Source,
        string? ExternalUrl,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        List<ProgramScopeDto> Scopes)
    {
        public DateTimeOffset UpdatedAt { get; set; } = UpdatedAt;
    }

    private static string NormalizeScopeType(string scopeType) =>
        string.IsNullOrWhiteSpace(scopeType) ? "domain" : scopeType.Trim();

    private static string NormalizePattern(string pattern) => pattern.Trim().ToLowerInvariant();
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
}

internal sealed class ProgramScopeDbContext(DbContextOptions<ProgramScopeDbContext> options) : DbContext(options)
{
    public DbSet<ProgramRecord> Programs => Set<ProgramRecord>();
    public DbSet<ProgramScopeRecord> Scopes => Set<ProgramScopeRecord>();

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
        }
    }
}
