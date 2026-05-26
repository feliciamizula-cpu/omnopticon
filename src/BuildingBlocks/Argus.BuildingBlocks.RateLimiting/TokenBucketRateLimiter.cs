using System.Collections.Concurrent;

namespace Argus.BuildingBlocks.RateLimiting;

public sealed class TokenBucketRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenBucketOptions _defaultOptions;

    public TokenBucketRateLimiter(TokenBucketOptions? defaultOptions = null)
    {
        _defaultOptions = defaultOptions ?? new TokenBucketOptions
        {
            Capacity = 10,
            RefillRate = 1,
            RefillInterval = TimeSpan.FromSeconds(1)
        };
    }

    public async Task<bool> TryConsumeAsync(
        string bucketKey,
        int tokens = 1,
        CancellationToken cancellationToken = default)
    {
        var bucket = _buckets.GetOrAdd(bucketKey, _ => CreateBucket(bucketKey));
        return bucket.TryConsume(tokens);
    }

    public async Task<TimeSpan?> WaitForTokenAsync(
        string bucketKey,
        int tokens = 1,
        TimeSpan? maxWait = null,
        CancellationToken cancellationToken = default)
    {
        var maxWaitTime = maxWait ?? TimeSpan.FromSeconds(30);
        var startTime = DateTimeOffset.UtcNow;

        while (true)
        {
            if (await TryConsumeAsync(bucketKey, tokens, cancellationToken))
            {
                return DateTimeOffset.UtcNow - startTime;
            }

            if (DateTimeOffset.UtcNow - startTime >= maxWaitTime)
            {
                return null;
            }

            var bucket = _buckets.GetOrAdd(bucketKey, _ => CreateBucket(bucketKey));
            var waitTime = bucket.TimeUntilRefill();

            if (waitTime > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(waitTime, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    public void ConfigureBucket(string bucketKey, TokenBucketOptions options)
    {
        _buckets.AddOrUpdate(
            bucketKey,
            _ => new TokenBucket(options),
            (_, _) => new TokenBucket(options));
    }

    public void RemoveBucket(string bucketKey)
    {
        _buckets.TryRemove(bucketKey, out _);
    }

    public BucketStatus GetStatus(string bucketKey)
    {
        if (_buckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket.Refill();
            return new BucketStatus(bucketKey, (int)bucket.Tokens, bucket.Capacity, bucket.RefillRate);
        }

        var defaultBucket = new TokenBucket(_defaultOptions);
        return new BucketStatus(bucketKey, defaultBucket.Capacity, defaultBucket.Capacity, defaultBucket.RefillRate);
    }

    private TokenBucket CreateBucket(string key)
    {
        return new TokenBucket(_defaultOptions);
    }
}

public sealed class TokenBucket
{
    private readonly double _refillRate;
    private readonly TimeSpan _refillInterval;
    private readonly object _lock = new();

    public int Capacity { get; }
    public double Tokens { get; private set; }
    public DateTimeOffset LastRefillAt { get; private set; }
    public double RefillRate => _refillRate;

    public TokenBucket(TokenBucketOptions options)
    {
        Capacity = options.Capacity;
        Tokens = options.Capacity;
        _refillRate = options.RefillRate;
        _refillInterval = options.RefillInterval;
        LastRefillAt = DateTimeOffset.UtcNow;
    }

    public bool TryConsume(int tokens = 1)
    {
        lock (_lock)
        {
            Refill();
            if (Tokens >= tokens)
            {
                Tokens -= tokens;
                return true;
            }
            return false;
        }
    }

    public void Refill()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - LastRefillAt;

        if (elapsed >= _refillInterval)
        {
            var tokensToAdd = (elapsed.TotalSeconds / _refillInterval.TotalSeconds) * _refillRate;
            Tokens = Math.Min(Capacity, Tokens + tokensToAdd);
            LastRefillAt = now;
        }
    }

    public TimeSpan TimeUntilRefill()
    {
        var timeSinceLastRefill = DateTimeOffset.UtcNow - LastRefillAt;
        var remaining = _refillInterval - timeSinceLastRefill;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

public sealed record TokenBucketOptions
{
    public int Capacity { get; set; } = 10;
    public double RefillRate { get; set; } = 1;
    public TimeSpan RefillInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed record BucketStatus(
    string BucketKey,
    double Remaining,
    int Capacity,
    double RefillRate);
