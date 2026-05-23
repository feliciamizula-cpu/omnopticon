using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Argus.BuildingBlocks.Artifacts;

public static class ServiceCollectionExtensions
{
    public const string DefaultArtifactRoot = "/var/lib/argus/artifacts";

    public static IServiceCollection AddLocalFileArtifactStore(
        this IServiceCollection services,
        string? rootPath = null)
    {
        var resolved = rootPath
            ?? Environment.GetEnvironmentVariable("ARGUS_ARTIFACT_ROOT")
            ?? DefaultArtifactRoot;

        services.AddSingleton<IArtifactStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LocalFileArtifactStore>>();
            return new LocalFileArtifactStore(resolved, logger);
        });

        return services;
    }

    public static IServiceCollection AddS3ArtifactStore(
        this IServiceCollection services,
        string? bucketName = null,
        string? endpoint = null,
        string? accessKey = null,
        string? secretKey = null,
        string? region = null)
    {
        var resolvedBucket = bucketName
            ?? Environment.GetEnvironmentVariable("ARGUS_S3_BUCKET")
            ?? "argus-artifacts";

        var resolvedEndpoint = endpoint
            ?? Environment.GetEnvironmentVariable("ARGUS_S3_ENDPOINT");

        var resolvedAccessKey = accessKey
            ?? Environment.GetEnvironmentVariable("ARGUS_S3_ACCESS_KEY");

        var resolvedSecretKey = secretKey
            ?? Environment.GetEnvironmentVariable("ARGUS_S3_SECRET_KEY");

        var resolvedRegion = region
            ?? Environment.GetEnvironmentVariable("ARGUS_S3_REGION")
            ?? RegionEndpoint.USEast1.SystemName;

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(resolvedRegion)
            };

            if (!string.IsNullOrWhiteSpace(resolvedEndpoint))
            {
                config.ServiceURL = resolvedEndpoint;
                config.ForcePathStyle = true;
            }

            if (!string.IsNullOrWhiteSpace(resolvedAccessKey) && !string.IsNullOrWhiteSpace(resolvedSecretKey))
            {
                var credentials = new BasicAWSCredentials(resolvedAccessKey, resolvedSecretKey);
                return new AmazonS3Client(credentials, config);
            }

            return new AmazonS3Client(config);
        });

        services.AddSingleton<IArtifactStore>(sp =>
        {
            var s3Client = sp.GetRequiredService<IAmazonS3>();
            var logger = sp.GetRequiredService<ILogger<S3ArtifactStore>>();
            var store = new S3ArtifactStore(s3Client, resolvedBucket, logger);
            return store;
        });

        return services;
    }
}
