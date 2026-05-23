namespace Argus.Contracts.RateLimits;

public sealed record RateLimitCheckRequest(
    Guid ProgramId,
    Guid? ScopeId,
    string? Host,
    string? RegisteredDomain,
    string? Ip,
    string WorkerType,
    string? ProxyId,
    int PermitCount = 1);

public sealed record RateLimitDecision(
    bool IsAllowed,
    Guid? TokenId,
    DateTimeOffset? ExpiresAt,
    TimeSpan? RetryAfter,
    IReadOnlyCollection<RateLimitBucketDecision> Buckets);

public sealed record RateLimitBucketDecision(
    string BucketKey,
    bool IsAllowed,
    int Remaining,
    TimeSpan? RetryAfter);

public sealed record RateLimitBucketDto(
    string BucketKey,
    int Capacity,
    int Remaining,
    DateTimeOffset ResetsAt);

public sealed record RateLimitBackpressureRequest(
    string Host,
    string BucketKey,
    TimeSpan RetryAfter,
    int ObservedStatusCode);
