using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.RateLimits;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRealtimeIntegrationEvents(options => options.SourceService = "Argus.RateLimitService");
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<RateLimitStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/rate-limits", (RateLimitStore store) => store.GetBuckets());

app.MapPost("/rate-limits/check", async (
    RateLimitCheckRequest request,
    RateLimitStore store,
    IIntegrationEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var decision = store.Check(request);
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

internal sealed class RateLimitStore
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, BucketState> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyCollection<RateLimitBucketDto> GetBuckets()
    {
        lock (_gate)
        {
            RefreshExpiredBuckets(DateTimeOffset.UtcNow);

            return _buckets
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new RateLimitBucketDto(pair.Key, pair.Value.Capacity, pair.Value.Remaining, pair.Value.ResetsAt))
                .ToArray();
        }
    }

    public RateLimitDecision Check(RateLimitCheckRequest request)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            RefreshExpiredBuckets(now);

            var bucketKeys = BuildBucketKeys(request).ToArray();
            var decisions = new List<RateLimitBucketDecision>(bucketKeys.Length);

            foreach (var bucketKey in bucketKeys)
            {
                var bucket = _buckets.GetOrAdd(bucketKey, key => new BucketState(GetCapacity(key), GetCapacity(key), now.Add(Window)));
                var allowed = bucket.Remaining >= request.PermitCount;
                decisions.Add(new RateLimitBucketDecision(
                    bucketKey,
                    allowed,
                    bucket.Remaining,
                    allowed ? null : bucket.ResetsAt - now));
            }

            if (decisions.Any(decision => !decision.IsAllowed))
            {
                return new RateLimitDecision(
                    false,
                    null,
                    null,
                    decisions.Where(decision => !decision.IsAllowed).Max(decision => decision.RetryAfter),
                    decisions);
            }

            foreach (var bucketKey in bucketKeys)
            {
                _buckets[bucketKey].Remaining -= request.PermitCount;
            }

            var tokenId = Guid.NewGuid();

            return new RateLimitDecision(
                true,
                tokenId,
                now.AddSeconds(30),
                null,
                bucketKeys.Select(bucketKey =>
                {
                    var bucket = _buckets[bucketKey];
                    return new RateLimitBucketDecision(bucketKey, true, bucket.Remaining, null);
                }).ToArray());
        }
    }

    private static IEnumerable<string> BuildBucketKeys(RateLimitCheckRequest request)
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

    private static int GetCapacity(string bucketKey) =>
        bucketKey switch
        {
            var key when key.StartsWith("host:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("registered-domain:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("ip:", StringComparison.OrdinalIgnoreCase) => 1,
            var key when key.StartsWith("worker-type:", StringComparison.OrdinalIgnoreCase) => 25,
            _ => 100
        };

    private sealed class BucketState(int capacity, int remaining, DateTimeOffset resetsAt)
    {
        public int Capacity { get; } = capacity;
        public int Remaining { get; set; } = remaining;
        public DateTimeOffset ResetsAt { get; set; } = resetsAt;
    }
}
