namespace Argus.BuildingBlocks.Artifacts;

public interface IArtifactStore
{
    Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task<Stream?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetDownloadUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
