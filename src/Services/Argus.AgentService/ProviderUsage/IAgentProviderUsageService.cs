namespace Argus.AgentService.ProviderUsage;

using Argus.Contracts.Agents;

internal interface IAgentProviderUsageService
{
    Task<ProviderUsageOverviewDto> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<ProviderUsageDto?> GetProviderAsync(string providerId, bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<ProviderLoginResponseDto?> LoginAsync(string providerId, CancellationToken cancellationToken = default);
    Task<ProviderRouteSelectionDto> SelectRouteAsync(IReadOnlyList<AgentDto> agents, CancellationToken cancellationToken = default);
}
