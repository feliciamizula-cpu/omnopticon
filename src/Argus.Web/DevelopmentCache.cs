namespace Argus.Web;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Distributed;

public static class DevelopmentCache
{
    public const string Agents = "development:agents";
    public const string AgentTasks = "development:agent-tasks";
    public const string Todos = "development:todos";
    public const string ProviderUsage = "development:provider-usage";
    public const string ProviderRouting = "development:provider-usage:routing-preview";
    public const string CodeReviews = "development:code-reviews";
    public const string SystemReports = "development:system-reports";

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string QueryKey(string key, params (string Name, string? Value)[] values)
    {
        var query = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .Select(v => $"{v.Name}={v.Value}")
            .ToArray();

        return query.Length == 0 ? key : $"{key}:{string.Join(":", query)}";
    }

    public static async Task<JsonNode?> GetOrCreateJsonAsync(
        IDistributedCache cache,
        string key,
        Func<Task<JsonNode?>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await cache.GetStringAsync(key, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return JsonNode.Parse(cached);
            }
        }
        catch
        {
            // The UI still works if Redis is temporarily unavailable.
        }

        var value = await factory();
        if (value is null)
        {
            return null;
        }

        try
        {
            await cache.SetStringAsync(key, value.ToJsonString(JsonOptions), CacheOptions, cancellationToken);
        }
        catch
        {
            // Cache failures should not block lazy page hydration.
        }

        return value;
    }

    public static async Task<JsonNode?> GetJsonAsync(
        IDistributedCache cache,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await cache.GetStringAsync(key, cancellationToken);
            return string.IsNullOrWhiteSpace(cached) ? null : JsonNode.Parse(cached);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SetJsonAsync(
        IDistributedCache cache,
        string key,
        JsonNode value,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetStringAsync(key, value.ToJsonString(JsonOptions), CacheOptions, cancellationToken);
        }
        catch
        {
            // Cache failures should not block lazy page hydration.
        }
    }

    public static async Task RemoveAsync(IDistributedCache cache, CancellationToken cancellationToken, params string[] keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await cache.RemoveAsync(key, cancellationToken);
            }
            catch
            {
                // Best-effort invalidation; SignalR still tells clients to refetch.
            }
        }
    }
}
