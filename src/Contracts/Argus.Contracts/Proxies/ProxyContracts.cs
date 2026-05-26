namespace Argus.Contracts.Proxies;

public sealed record ProxyDto(
    Guid ProxyId,
    string Url,
    string Protocol,
    string? Username,
    string? Country,
    string? City,
    bool IsActive,
    bool IsOnline,
    ProxyRateLimitDto RateLimit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateProxyRequest(
    string Url,
    string Protocol,
    string? Username,
    string? Password,
    string? Country,
    string? City,
    int MaxRequestsPerSecond = 10,
    int MaxConcurrentRequests = 5);

public sealed record UpdateProxyRequest(
    string? Url,
    string? Protocol,
    string? Username,
    string? Password,
    string? Country,
    string? City,
    bool? IsActive,
    int? MaxRequestsPerSecond,
    int? MaxConcurrentRequests);

public sealed record ProxyRateLimitDto(
    int MaxRequestsPerSecond,
    int MaxConcurrentRequests,
    int CurrentRequestsPerSecond,
    int CurrentConcurrentRequests,
    DateTimeOffset? ResetsAt);

public sealed record ProxySelectionRequest(
    Guid? ProgramId,
    string? WorkerType,
    string? Country,
    string? Protocol);

public sealed record ProxySelectionResult(
    ProxyDto? Proxy,
    IReadOnlyCollection<ProxyDto> AllProxies);

public sealed record ProxyStatusRequest(
    bool IsOnline);
