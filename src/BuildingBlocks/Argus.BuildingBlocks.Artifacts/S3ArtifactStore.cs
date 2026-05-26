using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.Artifacts;

public sealed class S3ArtifactStore : IArtifactStore, IAsyncDisposable
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3ArtifactStore> _logger;
    private bool _disposed;

    public S3ArtifactStore(IAmazonS3 s3Client, string bucketName, ILogger<S3ArtifactStore> logger)
    {
        _s3Client = s3Client;
        _bucketName = bucketName;
        _logger = logger;
    }

    public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var actualKey = await ComputeKeyAsync(content, key, cancellationToken);

        try
        {
            var exists = await ExistsAsync(actualKey, cancellationToken);
            if (exists)
            {
                _logger.LogDebug("Artifact already exists at s3://{Bucket}/{Key}", _bucketName, actualKey);
                return actualKey;
            }

            content.Position = 0;
            var putRequest = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = actualKey,
                InputStream = content,
                ContentType = contentType
            };

            await _s3Client.PutObjectAsync(putRequest, cancellationToken);
            _logger.LogInformation("Stored artifact at s3://{Bucket}/{Key}", _bucketName, actualKey);
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to store artifact at s3://{Bucket}/{Key}", _bucketName, actualKey);
            throw;
        }

        return actualKey;
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await _s3Client.GetObjectAsync(getRequest, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var deleteRequest = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            await _s3Client.DeleteObjectAsync(deleteRequest, cancellationToken);
            _logger.LogInformation("Deleted artifact at s3://{Bucket}/{Key}", _bucketName, key);
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to delete artifact at s3://{Bucket}/{Key}", _bucketName, key);
            throw;
        }
    }

    public async Task<string?> GetDownloadUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await ExistsAsync(key, cancellationToken);
            if (!exists)
            {
                return null;
            }

            var urlRequest = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromHours(1))
            };

            return _s3Client.GetPreSignedURL(urlRequest);
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to generate pre-signed URL for s3://{Bucket}/{Key}", _bucketName, key);
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var metaRequest = new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            await _s3Client.GetObjectMetadataAsync(metaRequest, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<string> ComputeKeyAsync(Stream content, string key, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        content.Position = 0;
        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_s3Client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (_s3Client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
