using Argus.Contracts.Programs;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ProgramScopeStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/programs", (ProgramScopeStore store) => store.GetPrograms());

app.MapPost("/programs", (CreateProgramRequest request, ProgramScopeStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Program name is required.");
    }

    var program = store.CreateProgram(request);
    return Results.Created($"/programs/{program.ProgramId}", program);
});

app.MapGet("/programs/{programId:guid}", (Guid programId, ProgramScopeStore store) =>
    store.TryGetProgram(programId, out var program) ? Results.Ok(program) : Results.NotFound());

app.MapGet("/scopes", (ProgramScopeStore store) => store.GetScopes());

app.MapPost("/programs/{programId:guid}/scopes", (
    Guid programId,
    CreateProgramScopeRequest request,
    ProgramScopeStore store) =>
{
    if (programId != request.ProgramId)
    {
        return Results.BadRequest("Route program id must match request program id.");
    }

    if (string.IsNullOrWhiteSpace(request.Pattern))
    {
        return Results.BadRequest("Scope pattern is required.");
    }

    return store.TryCreateScope(request, out var scope)
        ? Results.Created($"/programs/{programId}/scopes/{scope.ScopeId}", scope)
        : Results.NotFound();
});

app.MapPost("/scope-validation/check", (ScopeValidationRequest request, ProgramScopeStore store) =>
    store.Validate(request));

app.Run();

internal sealed class ProgramScopeStore
{
    private readonly ConcurrentDictionary<Guid, ProgramRecord> _programs = new();

    public IReadOnlyCollection<ProgramDto> GetPrograms() =>
        _programs.Values
            .OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToArray();

    public IReadOnlyCollection<ProgramScopeDto> GetScopes() =>
        _programs.Values
            .SelectMany(program => program.Scopes)
            .OrderBy(scope => scope.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryGetProgram(Guid programId, out ProgramDto? program)
    {
        if (_programs.TryGetValue(programId, out var record))
        {
            program = ToDto(record);
            return true;
        }

        program = null;
        return false;
    }

    public ProgramDto CreateProgram(CreateProgramRequest request)
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

        return ToDto(record);
    }

    public bool TryCreateScope(CreateProgramScopeRequest request, out ProgramScopeDto scope)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            scope = default!;
            return false;
        }

        scope = new ProgramScopeDto(
            Guid.NewGuid(),
            request.ProgramId,
            request.ScopeType.Trim(),
            request.Pattern.Trim().ToLowerInvariant(),
            request.Action,
            request.Notes,
            DateTimeOffset.UtcNow);

        lock (program.Scopes)
        {
            program.Scopes.Add(scope);
            program.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return true;
    }

    public ScopeValidationResult Validate(ScopeValidationRequest request)
    {
        if (!_programs.TryGetValue(request.ProgramId, out var program))
        {
            return new ScopeValidationResult(request.ProgramId, request.Target, false, null, "Program was not found.");
        }

        var target = request.Target.Trim().ToLowerInvariant();
        ProgramScopeDto? includeMatch = null;

        foreach (var scope in program.Scopes)
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
}
