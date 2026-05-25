namespace Argus.Web;

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Distributed;

public sealed class ProviderUsageCacheWarmer(
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    IConfiguration configuration,
    DevelopmentRealtimeNotifier notifier,
    ILogger<ProviderUsageCacheWarmer> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;

    public void QueueWarm(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTimeOffset.UtcNow - _lastStarted < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _ = Task.Run(() => WarmAsync(forceRefresh, CancellationToken.None));
    }

    public async Task WarmAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            _lastStarted = DateTimeOffset.UtcNow;
            var gateway = new ArgusUiGateway(httpClientFactory);
            var endpoints = ArgusServiceEndpoints.From(configuration);
            var usagePath = forceRefresh ? "/provider-usage/refresh" : "/provider-usage";

            JsonNode? usage;
            if (forceRefresh)
            {
                usage = await PostJsonNodeAsync(endpoints.Agent, usagePath, new JsonObject(), cancellationToken);
            }
            else
            {
                usage = await gateway.GetJsonAsync(endpoints.Agent, usagePath, cancellationToken);
            }

            if (!ProviderUsageDefaults.IsEmptyOverview(usage))
            {
                await DevelopmentCache.SetJsonAsync(cache, DevelopmentCache.ProviderUsage, usage!, cancellationToken);
            }

            var routing = await gateway.GetJsonAsync(endpoints.Agent, "/provider-usage/routing-preview", cancellationToken);
            if (routing is not null)
            {
                await DevelopmentCache.SetJsonAsync(cache, DevelopmentCache.ProviderRouting, routing, cancellationToken);
            }

            if (!ProviderUsageDefaults.IsEmptyOverview(usage) || routing is not null)
            {
                await notifier.NotifyAsync("provider-usage", forceRefresh ? "refreshed" : "warmed", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Provider usage cache warm failed.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<JsonNode?> PostJsonNodeAsync(
        string baseAddress,
        string path,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseAddress);
        using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken)
            : null;
    }
}
