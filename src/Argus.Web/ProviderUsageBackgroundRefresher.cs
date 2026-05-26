namespace Argus.Web;

using System.Net.Http.Json;
using System.Text.Json.Nodes;

public sealed class ProviderUsageBackgroundRefresher(
    ProviderUsageCacheWarmer warmer,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ProviderUsageBackgroundRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActiveCodexInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await warmer.WarmAsync(forceRefresh: false, stoppingToken);
        try
        {
            while (true)
            {
                var hasActiveCodexAgent = await HasActiveCodexAgentAsync(stoppingToken);
                await Task.Delay(hasActiveCodexAgent ? ActiveCodexInterval : IdleInterval, stoppingToken);
                await warmer.WarmAsync(forceRefresh: true, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ProviderUsageBackgroundRefresher stopped.");
        }
    }

    private async Task<bool> HasActiveCodexAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = ArgusServiceEndpoints.From(configuration);
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(endpoints.Agent);
            var payload = await client.GetFromJsonAsync<JsonNode>("/agents", cancellationToken);
            var agents = payload?["items"]?.AsArray();
            if (agents is null)
            {
                return false;
            }

            foreach (var agent in agents)
            {
                if (agent is not JsonObject item)
                {
                    continue;
                }

                var tool = item["tool"]?.ToString();
                var workStatus = item["workStatus"]?.ToString();
                var currentTaskId = item["currentTaskId"]?.ToString();
                if (IsCodexTool(tool)
                    && (string.Equals(workStatus, "working", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(currentTaskId)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Unable to determine whether Codex agents are active.");
        }

        return false;
    }

    private static bool IsCodexTool(string? tool) =>
        string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tool, "openai", StringComparison.OrdinalIgnoreCase);
}
