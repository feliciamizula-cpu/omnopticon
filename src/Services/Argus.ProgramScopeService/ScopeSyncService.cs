using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Programs;
using Argus.ProgramScopeService.Providers;

namespace Argus.ProgramScopeService;

internal sealed class ScopeSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScopeSyncService> _logger;

    public ScopeSyncService(IServiceScopeFactory scopeFactory, ILogger<ScopeSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromSeconds(30);
        _logger.LogInformation("ScopeSyncService will start in {Delay}", initialDelay);
        await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncScopesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing scopes from providers");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task SyncScopesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProgramScopeStore>();
        var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IScopeProvider>>();
        var events = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

        var programs = await store.GetProgramsAsync(ct);

        foreach (var program in programs)
        {
            var provider = ResolveProvider(providers, program.Source);
            if (provider is null)
                continue;

            var handle = ExtractHandle(program);
            if (string.IsNullOrWhiteSpace(handle))
            {
                _logger.LogWarning("Could not determine handle for program {ProgramId} ({Name})", program.ProgramId, program.Name);
                continue;
            }

            _logger.LogInformation("Syncing scopes for program {Name} via {Provider} (handle: {Handle})",
                program.Name, provider.ProviderName, handle);

            IReadOnlyCollection<ProviderScopeItem> remoteScopes;
            try
            {
                remoteScopes = await provider.FetchAsync(handle, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch scopes from {Provider} for program {Name}",
                    provider.ProviderName, program.Name);
                continue;
            }

            var currentScopes = program.Scopes;
            var currentByPattern = currentScopes
                .GroupBy(s => s.Pattern)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var remoteByPattern = remoteScopes
                .GroupBy(s => s.Pattern)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var allPatterns = new HashSet<string>(currentByPattern.Keys, StringComparer.OrdinalIgnoreCase);
            allPatterns.UnionWith(remoteByPattern.Keys);

            foreach (var pattern in allPatterns)
            {
                var hasCurrent = currentByPattern.TryGetValue(pattern, out var current);
                var hasRemote = remoteByPattern.TryGetValue(pattern, out var remote);

                if (hasCurrent && !hasRemote)
                {
                    await events.PublishAsync(
                        new ProgramScopeChanged(program.ProgramId, current!.ScopeId, "deleted"),
                        nameof(ProgramScopeChanged),
                        "Argus.ProgramScopeService",
                        cancellationToken: ct);
                    await store.DeleteScopeAsync(program.ProgramId, current.ScopeId, ct);
                    _logger.LogInformation("Removed scope {Pattern} from program {Name}", pattern, program.Name);
                }
                else if (!hasCurrent && hasRemote)
                {
                    var request = new CreateProgramScopeRequest(
                        program.ProgramId, remote!.ScopeType, remote.Pattern, remote.Action, null);
                    var created = await store.CreateScopeAsync(request, ct);
                    if (created is not null)
                    {
                        await events.PublishAsync(
                            new ProgramScopeChanged(program.ProgramId, created.ScopeId, "created"),
                            nameof(ProgramScopeChanged),
                            "Argus.ProgramScopeService",
                            cancellationToken: ct);
                        _logger.LogInformation("Added scope {Pattern} to program {Name}", pattern, program.Name);
                    }
                }
                else if (hasCurrent && hasRemote &&
                         (current!.Action != remote!.Action || !string.Equals(current.ScopeType, remote.ScopeType, StringComparison.OrdinalIgnoreCase)))
                {
                    await events.PublishAsync(
                        new ProgramScopeChanged(program.ProgramId, current.ScopeId, "deleted"),
                        nameof(ProgramScopeChanged),
                        "Argus.ProgramScopeService",
                        cancellationToken: ct);
                    await store.DeleteScopeAsync(program.ProgramId, current.ScopeId, ct);

                    var request = new CreateProgramScopeRequest(
                        program.ProgramId, remote.ScopeType, remote.Pattern, remote.Action, null);
                    var created = await store.CreateScopeAsync(request, ct);
                    if (created is not null)
                    {
                        await events.PublishAsync(
                            new ProgramScopeChanged(program.ProgramId, created.ScopeId, "created"),
                            nameof(ProgramScopeChanged),
                            "Argus.ProgramScopeService",
                            cancellationToken: ct);
                        _logger.LogInformation("Updated scope {Pattern} for program {Name}", pattern, program.Name);
                    }
                }
            }
        }
    }

    private static IScopeProvider? ResolveProvider(IEnumerable<IScopeProvider> providers, string source)
    {
        foreach (var provider in providers)
        {
            if (provider.ProviderName.Equals(source, StringComparison.OrdinalIgnoreCase))
                return provider;
        }
        return null;
    }

    private static string? ExtractHandle(ProgramDto program)
    {
        if (!string.IsNullOrWhiteSpace(program.ExternalUrl))
        {
            try
            {
                var uri = new Uri(program.ExternalUrl);
                var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrWhiteSpace(lastSegment))
                    return lastSegment;
            }
            catch
            {
            }
        }

        return program.Name;
    }
}
