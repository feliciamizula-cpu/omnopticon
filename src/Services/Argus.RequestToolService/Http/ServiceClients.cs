using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Contracts.Assets;

namespace Argus.RequestToolService.Http;

/// <summary>
/// JSON options for cross-service deserialization. Services serialize enums as strings
/// (e.g. AssetDto.Type = "HttpResponse"), so the reader needs <see cref="JsonStringEnumConverter"/>
/// or every asset fetch throws and the request-tool reports "Asset not found".
/// </summary>
internal static class ServiceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record AssetReference(Guid ArtifactId, string ArtifactType, string? Name);

public interface IAssetServiceClient
{
    Task<AssetDto?> GetAssetAsync(Guid assetId, CancellationToken ct);
    Task<IReadOnlyList<AssetReference>> GetAssetArtifactsAsync(Guid assetId, CancellationToken ct);
}

public sealed class AssetServiceClient : IAssetServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AssetServiceClient> _logger;

    public AssetServiceClient(HttpClient httpClient, ILogger<AssetServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AssetDto?> GetAssetAsync(Guid assetId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/assets/{assetId}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<AssetDto>(ServiceJson.Options, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get asset {AssetId}", assetId);
            return null;
        }
    }

    public async Task<IReadOnlyList<AssetReference>> GetAssetArtifactsAsync(Guid assetId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/assets/{assetId}/artifacts", ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var artifacts = await response.Content.ReadFromJsonAsync<List<AssetReference>>(ServiceJson.Options, cancellationToken: ct);
            return artifacts ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get artifacts for asset {AssetId}", assetId);
            return [];
        }
    }
}

public interface IArtifactServiceClient
{
    Task<ArtifactDto?> GetArtifactAsync(Guid artifactId, CancellationToken ct);
    Task<string?> GetArtifactContentAsync(Guid artifactId, CancellationToken ct);
}

public sealed record ArtifactDto(Guid ArtifactId, string ArtifactType, string? Name, long SizeBytes);

public sealed class ArtifactServiceClient : IArtifactServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ArtifactServiceClient> _logger;

    public ArtifactServiceClient(HttpClient httpClient, ILogger<ArtifactServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ArtifactDto?> GetArtifactAsync(Guid artifactId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/artifacts/{artifactId}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<ArtifactDto>(ServiceJson.Options, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get artifact {ArtifactId}", artifactId);
            return null;
        }
    }

    public async Task<string?> GetArtifactContentAsync(Guid artifactId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/artifacts/{artifactId}/content", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get artifact content {ArtifactId}", artifactId);
            return null;
        }
    }
}

public interface IProgramScopeServiceClient
{
    Task<ScopeValidationResult> ValidateTargetAsync(Guid programId, string targetUrl, CancellationToken ct);
}

public sealed record ScopeValidationResult(string Status);

public sealed class ProgramScopeServiceClient : IProgramScopeServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProgramScopeServiceClient> _logger;

    public ProgramScopeServiceClient(HttpClient httpClient, ILogger<ProgramScopeServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ScopeValidationResult> ValidateTargetAsync(Guid programId, string targetUrl, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/scope-validation/validate",
                new { ProgramId = programId, TargetUrl = targetUrl }, ct);

            if (!response.IsSuccessStatusCode)
                return new ScopeValidationResult("Unknown");

            return await response.Content.ReadFromJsonAsync<ScopeValidationResult>(cancellationToken: ct)
                ?? new ScopeValidationResult("Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate target {TargetUrl} for program {ProgramId}", targetUrl, programId);
            return new ScopeValidationResult("Unknown");
        }
    }
}

public interface IRateLimitServiceClient
{
    Task<RateLimitResult> CheckRateLimitAsync(string key, CancellationToken ct);
}

public sealed record RateLimitResult(bool IsAllowed, int? RetryAfterSeconds);

public sealed class RateLimitServiceClient : IRateLimitServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RateLimitServiceClient> _logger;

    public RateLimitServiceClient(HttpClient httpClient, ILogger<RateLimitServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckRateLimitAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/rate-limits/check",
                new { Key = key }, ct);

            if (!response.IsSuccessStatusCode)
                return new RateLimitResult(true, null);

            return await response.Content.ReadFromJsonAsync<RateLimitResult>(cancellationToken: ct)
                ?? new RateLimitResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check rate limit for key {Key}", key);
            return new RateLimitResult(true, null);
        }
    }
}

public interface IProxyRegistryServiceClient
{
    Task<ProxyDto?> GetProxyAsync(string proxyId, CancellationToken ct);
}

public sealed record ProxyDto(string ProxyId, string Name, string Type, string Host, int Port);

public sealed class ProxyRegistryServiceClient : IProxyRegistryServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProxyRegistryServiceClient> _logger;

    public ProxyRegistryServiceClient(HttpClient httpClient, ILogger<ProxyRegistryServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ProxyDto?> GetProxyAsync(string proxyId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/proxies/{proxyId}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<ProxyDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get proxy {ProxyId}", proxyId);
            return null;
        }
    }
}