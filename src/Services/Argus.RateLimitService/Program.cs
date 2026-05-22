using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.RateLimits;
using Argus.ServiceDefaults;
using StackExchange.Redis;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.RateLimitService");
builder.Services.AddProblemDetails();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("redis")))
{
    builder.AddRedisClient("redis");
    builder.Services.AddSingleton<IRateLimitStore, RedisRateLimitStore>();
}
else
{
    builder.Services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
}

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/rate-limits", (IRateLimitStore store, CancellationToken cancellationToken) =>
    store.GetBucketsAsync(cancellationToken));

app.MapPost("/rate-limits/check", async (
    RateLimitCheckRequest request,
    IRateLimitStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var decision = await store.CheckAsync(request, cancellationToken);
    var tightestBucket = decision.Buckets.OrderBy(bucket => bucket.Remaining).FirstOrDefault();

    if (decision.IsAllowed && decision.TokenId is not null && decision.ExpiresAt is not null && tightestBucket is not null)
    {
        await events.PublishAsync(
            new RateLimitTokenGranted(tightestBucket.BucketKey, decision.TokenId.Value, decision.ExpiresAt.Value),
            nameof(RateLimitTokenGranted),
            "Argus.RateLimitService",
            cancellationToken: cancellationToken);
    }
    else if (!decision.IsAllowed && tightestBucket is not null)
    {
        await events.PublishAsync(
            new RateLimitDelayed(tightestBucket.BucketKey, decision.RetryAfter ?? TimeSpan.FromSeconds(1)),
            nameof(RateLimitDelayed),
            "Argus.RateLimitService",
            cancellationToken: cancellationToken);
    }

    return decision;
});

app.Run();

internal interface IRateLimitStore
{
    Task<IReadOnlyCollection<RateLimitBucketDto>> GetBucketsAsync(CancellationToken cancellationToken);
    Task<RateLimitDecision> CheckAsync(RateLimitCheckRequest request, CancellationToken cancellationToken);
}

internal sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, BucketState> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<IReadOnlyCollection<RateLimitBucketDto>> GetBucketsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RefreshExpiredBuckets(DateTimeOffset.UtcNow);

            IReadOnlyCollection<RateLimitBucketDto> buckets = _buckets
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new RateLimitBucketDto(pair.Key, pair.Value.Capacity, pair.Value.Remaining, pair.Value.ResetsAt))
                .ToArray();

            return Task.FromResult(buckets);
        }
    }

    public Task<RateLimitDecision> CheckAsync(RateLimitCheckRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            RefreshExpiredBuckets(now);

            var bucketKeys = RateLimitBuckets.BuildBucketKeys(request).ToArray();
            var decisions = new List<RateLimitBucketDecision>(bucketKeys.Length);

            foreach (var bucketKey in bucketKeys)
            {
                var bucket = _buckets.GetOrAdd(bucketKey, key => new BucketState(RateLimitBuckets.GetCapacity(key), RateLimitBuckets.GetCapacity(key), now.Add(Window)));
                var allowed = bucket.Remaining >= request.PermitCount;
                decisions.Add(new RateLimitBucketDecision(
                    bucketKey,
                    allowed,
                    bucket.Remaining,
                    allowed ? null : bucket.ResetsAt - now));
            }

            if (decisions.Any(decision => !decision.IsAllowed))
            {
                return Task.FromResult(new RateLimitDecision(
                    false,
                    null,
                    null,
                    decisions.Where(decision => !decision.IsAllowed).Max(decision => decision.RetryAfter),
                    decisions));
            }

            foreach (var bucketKey in bucketKeys)
            {
                _buckets[bucketKey].Remaining -= request.PermitCount;
            }

            var tokenId = Guid.NewGuid();
            var finalDecisions = bucketKeys.Select(bucketKey =>
            {
                var bucket = _buckets[bucketKey];
                return new RateLimitBucketDecision(bucketKey, true, bucket.Remaining, null);
            }).ToArray();

            return Task.FromResult(new RateLimitDecision(true, tokenId, now.AddSeconds(30), null, finalDecisions));
        }
    }

    private void RefreshExpiredBuckets(DateTimeOffset now)
    {
        foreach (var bucket in _buckets.Values)
        {
            if (bucket.ResetsAt > now)
            {
                continue;
            }

            bucket.Remaining = bucket.Capacity;
            bucket.ResetsAt = now.Add(Window);
        }
    }

    private sealed class BucketState(int capacity, int remaining, DateTimeOffset resetsAt)
    {
        public int Capacity { get; } = capacity;
        public int Remaining { get; set; } = remaining;
        public DateTimeOffset ResetsAt { get; set; } = resetsAt;
    }
}

internal sealed class RedisRateLimitStore(IConnectionMultiplexer redis) : IRateLimitStore
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const string BucketIndexKey = "argus:rate-limit:buckets";
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<IReadOnlyCollection<RateLimitBucketDto>> GetBucketsAsync(CancellationToken cancellationToken)
    {
        var bucketKeys = await _database.SetMembersAsync(BucketIndexKey);
        var results = new List<RateLimitBucketDto>(bucketKeys.Length);

        foreach (var bucketKeyValue in bucketKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var redisKey = (RedisKey)bucketKeyValue.ToString();
            var values = await _database.HashGetAsync(redisKey, ["capacity", "remaining", "reset"]);

            if (values.Length != 3
                || !values[0].TryParse(out int capacity)
                || !values[1].TryParse(out int remaining)
                || !values[2].TryParse(out long resetUnixMilliseconds))
            {
                continue;
            }

            results.Add(new RateLimitBucketDto(
                FromRedisKey(redisKey),
                capacity,
                remaining,
                DateTimeOffset.FromUnixTimeMilliseconds(resetUnixMilliseconds)));
        }

        return results
            .OrderBy(bucket => bucket.BucketKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RateLimitDecision> CheckAsync(RateLimitCheckRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var bucketKeys = RateLimitBuckets.BuildBucketKeys(request).ToArray();
        var redisKeys = bucketKeys.Select(ToRedisKey).ToArray();
        var arguments = BuildArguments(request, bucketKeys, now);

        var scriptResult = await _database.ScriptEvaluateAsync(CheckAndConsumeScript, redisKeys, arguments);
        var values = (RedisResult[])scriptResult!;
        var allowed = (int)values[0] == 1;
        var retryAfterMilliseconds = (long)values[1];
        var bucketResults = new List<RateLimitBucketDecision>(bucketKeys.Length);

        var offset = 2;
        for (var i = 0; i < bucketKeys.Length; i++)
        {
            var bucketKey = (string)values[offset++]!;
            var remaining = (int)values[offset++];
            var resetUnixMilliseconds = (long)values[offset++];
            _ = (int)values[offset++];

            TimeSpan? retryAfter = allowed
                ? null
                : TimeSpan.FromMilliseconds(Math.Max(0, resetUnixMilliseconds - now.ToUnixTimeMilliseconds()));

            bucketResults.Add(new RateLimitBucketDecision(bucketKey, allowed || remaining >= request.PermitCount, remaining, retryAfter));
        }

        return allowed
            ? new RateLimitDecision(true, Guid.NewGuid(), now.AddSeconds(30), null, bucketResults)
            : new RateLimitDecision(false, null, null, TimeSpan.FromMilliseconds(Math.Max(0, retryAfterMilliseconds)), bucketResults);
    }

    private static RedisValue[] BuildArguments(RateLimitCheckRequest request, IReadOnlyCollection<string> bucketKeys, DateTimeOffset now)
    {
        var values = new List<RedisValue>
        {
            Math.Max(1, request.PermitCount),
            now.ToUnixTimeMilliseconds(),
            (long)Window.TotalMilliseconds,
            BucketIndexKey
        };

        foreach (var bucketKey in bucketKeys)
        {
            values.Add(bucketKey);
            values.Add(RateLimitBuckets.GetCapacity(bucketKey));
        }

        return values.ToArray();
    }

    private static RedisKey ToRedisKey(string bucketKey) => $"argus:rate-limit:bucket:{bucketKey}";

    private static string FromRedisKey(RedisKey redisKey)
    {
        const string Prefix = "argus:rate-limit:bucket:";
        var value = redisKey.ToString();
        return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? value[Prefix.Length..] : value;
    }

    private const string CheckAndConsumeScript = """
local permit = tonumber(ARGV[1])
local now = tonumber(ARGV[2])
local window = tonumber(ARGV[3])
local indexKey = ARGV[4]
local allowed = 1
local retryAfter = 0
local results = {}

for i = 1, #KEYS do
    local bucketName = ARGV[3 + (i * 2)]
    local capacity = tonumber(ARGV[4 + (i * 2)])
    local remaining = tonumber(redis.call('HGET', KEYS[i], 'remaining'))
    local reset = tonumber(redis.call('HGET', KEYS[i], 'reset'))

    if remaining == nil or reset == nil or reset <= now then
        remaining = capacity
        reset = now + window
    end

    if remaining < permit then
        allowed = 0
        local wait = reset - now
        if wait > retryAfter then
            retryAfter = wait
        end
    end

    results[(i - 1) * 4 + 1] = bucketName
    results[(i - 1) * 4 + 2] = remaining
    results[(i - 1) * 4 + 3] = reset
    results[(i - 1) * 4 + 4] = capacity
end

for i = 1, #KEYS do
    local remainingIndex = (i - 1) * 4 + 2
    local resetIndex = (i - 1) * 4 + 3
    local capacityIndex = (i - 1) * 4 + 4
    local remaining = tonumber(results[remainingIndex])
    local reset = tonumber(results[resetIndex])
    local capacity = tonumber(results[capacityIndex])

    if allowed == 1 then
        remaining = remaining - permit
        results[remainingIndex] = remaining
    end

    redis.call('HSET', KEYS[i], 'capacity', capacity, 'remaining', remaining, 'reset', reset)
    redis.call('PEXPIRE', KEYS[i], math.max(window, reset - now + window))
    redis.call('SADD', indexKey, KEYS[i])
end

local response = { allowed, retryAfter }
for i = 1, #results do
    response[#response + 1] = results[i]
end

return response
""";
}

internal static class RateLimitBuckets
{
    public static IEnumerable<string> BuildBucketKeys(RateLimitCheckRequest request)
    {
        yield return $"program:{request.ProgramId:N}";
        yield return $"worker-type:{request.WorkerType.Trim().ToLowerInvariant()}";

        if (request.ScopeId is not null)
        {
            yield return $"scope:{request.ScopeId:N}";
        }

        if (!string.IsNullOrWhiteSpace(request.Host))
        {
            yield return $"host:{request.Host.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(request.RegisteredDomain))
        {
            yield return $"registered-domain:{request.RegisteredDomain.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(request.Ip))
        {
            yield return $"ip:{request.Ip.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(request.ProxyId))
        {
            yield return $"proxy:{request.ProxyId.Trim().ToLowerInvariant()}";
        }
    }

    public static int GetCapacity(string bucketKey) =>
        bucketKey switch
        {
            var key when key.StartsWith("host:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("registered-domain:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("ip:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("worker-type:", StringComparison.OrdinalIgnoreCase) => 25,
            _ => 100
        };
}
