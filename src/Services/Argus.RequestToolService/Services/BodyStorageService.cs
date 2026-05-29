using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Argus.RequestToolService.Http;
using Argus.RequestToolService.Options;

namespace Argus.RequestToolService.Services;

public interface IBodyStorageService
{
    Task<StoredBodyResult> StoreRequestBodyAsync(
        Guid programId,
        Guid assetId,
        string? contentType,
        string? body,
        CancellationToken ct);

    Task<StoredBodyResult> StoreResponseBodyAsync(
        Guid programId,
        Guid assetId,
        string? contentType,
        Stream bodyStream,
        CancellationToken ct);

    Task<string?> GetBodyPreviewAsync(
        Guid? artifactId,
        string? inlineBody,
        int previewLimitBytes,
        CancellationToken ct);

    Task<string?> GetFullBodyAsync(
        Guid? artifactId,
        string? inlineBody,
        CancellationToken ct);
}

public sealed record StoredBodyResult(
    string? InlineBody,
    Guid? ArtifactId,
    string? Sha256,
    long SizeBytes,
    bool IsTruncated);

public sealed class BodyStorageService : IBodyStorageService
{
    private readonly RequestToolOptions _options;
    private readonly IArtifactServiceClient _artifactService;
    private readonly ILogger<BodyStorageService> _logger;

    public BodyStorageService(
        IOptions<RequestToolOptions> options,
        IArtifactServiceClient artifactService,
        ILogger<BodyStorageService> logger)
    {
        _options = options.Value;
        _artifactService = artifactService;
        _logger = logger;
    }

    public Task<StoredBodyResult> StoreRequestBodyAsync(
        Guid programId,
        Guid assetId,
        string? contentType,
        string? body,
        CancellationToken ct)
    {
        return StoreBodyAsync(programId, assetId, contentType, body, ct);
    }

    public async Task<StoredBodyResult> StoreResponseBodyAsync(
        Guid programId,
        Guid assetId,
        string? contentType,
        Stream bodyStream,
        CancellationToken ct)
    {
        using var memoryStream = new MemoryStream();
        await bodyStream.CopyToAsync(memoryStream, ct);
        var bodyText = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());

        return await StoreBodyAsync(programId, assetId, contentType, bodyText, ct);
    }

    private Task<StoredBodyResult> StoreBodyAsync(
        Guid programId,
        Guid assetId,
        string? contentType,
        string? body,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Task.FromResult(new StoredBodyResult(null, null, null, 0, false));
        }

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
        var sizeBytes = bodyBytes.Length;
        var sha256 = ComputeSha256(bodyBytes);
        var isTruncated = sizeBytes > _options.MaxBodyBytes;

        if (sizeBytes > _options.MaxBodyBytes)
        {
            _logger.LogWarning("Body size {Size} exceeds max {Max}, truncating", sizeBytes, _options.MaxBodyBytes);
            body = body.Substring(0, (int)_options.MaxBodyBytes);
            bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            sizeBytes = bodyBytes.Length;
        }

        if (sizeBytes <= _options.InlineBodyThresholdBytes)
        {
            return Task.FromResult(new StoredBodyResult(body, null, sha256, sizeBytes, isTruncated));
        }

        return Task.FromResult(new StoredBodyResult(null, Guid.NewGuid(), sha256, sizeBytes, isTruncated));
    }

    public Task<string?> GetBodyPreviewAsync(
        Guid? artifactId,
        string? inlineBody,
        int previewLimitBytes,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(inlineBody))
        {
            if (inlineBody.Length <= previewLimitBytes)
                return Task.FromResult<string?>(inlineBody);

            return Task.FromResult<string?>(inlineBody.Substring(0, previewLimitBytes));
        }

        if (artifactId.HasValue)
        {
            return GetFullBodyAsync(artifactId, null, ct);
        }

        return Task.FromResult<string?>(null);
    }

    public async Task<string?> GetFullBodyAsync(
        Guid? artifactId,
        string? inlineBody,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(inlineBody))
        {
            return inlineBody;
        }

        if (artifactId.HasValue)
        {
            return await _artifactService.GetArtifactContentAsync(artifactId.Value, ct);
        }

        return null;
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}