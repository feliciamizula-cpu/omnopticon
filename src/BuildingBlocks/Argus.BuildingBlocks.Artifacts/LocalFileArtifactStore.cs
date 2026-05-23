using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.Artifacts;

public sealed class LocalFileArtifactStore : IArtifactStore
{
    private readonly string _rootPath;
    private readonly ILogger<LocalFileArtifactStore> _logger;

    public LocalFileArtifactStore(string rootPath, ILogger<LocalFileArtifactStore> logger)
    {
        _rootPath = rootPath;
        _logger = logger;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var (hash, actualKey) = await ComputeKeyAsync(content, key, cancellationToken);
        var filePath = GetFilePath(actualKey);
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(filePath))
        {
            _logger.LogDebug("Artifact already exists at {Path} (key={Key})", filePath, actualKey);
            return actualKey;
        }

        content.Position = 0;
        await using var fileStream = File.Create(filePath);
        await content.CopyToAsync(fileStream, cancellationToken);

        _logger.LogInformation("Stored artifact at {Path} ({Size} bytes, key={Key})", filePath, fileStream.Length, actualKey);
        return actualKey;
    }

    public Task<Stream?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(File.OpenRead(filePath));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(key);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted artifact at {Path} (key={Key})", filePath, key);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetDownloadUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(key);
        return Task.FromResult(File.Exists(filePath) ? new Uri(filePath).AbsoluteUri : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(key);
        return Task.FromResult(File.Exists(filePath));
    }

    private string GetFilePath(string key)
    {
        if (key.Length < 4)
        {
            return Path.Combine(_rootPath, key);
        }

        return Path.Combine(_rootPath, key[..2], key[2..4], key);
    }

    private static async Task<(string Hash, string Key)> ComputeKeyAsync(Stream content, string key, CancellationToken cancellationToken)
    {
        content.Position = 0;
        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        var hexHash = Convert.ToHexString(hash).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(key) ? (hexHash, hexHash) : (hexHash, key);
    }
}
