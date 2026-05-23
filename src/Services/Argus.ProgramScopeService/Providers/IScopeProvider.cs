using Argus.Contracts.Programs;

namespace Argus.ProgramScopeService.Providers;

public interface IScopeProvider
{
    string ProviderName { get; }
    Task<IReadOnlyCollection<ProviderScopeItem>> FetchAsync(string programHandle, CancellationToken cancellationToken = default);
}

public sealed record ProviderScopeItem(string Pattern, string ScopeType, ScopeRuleAction Action);
