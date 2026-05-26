using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Argus.BuildingBlocks.EventDrivenWorkers;

public static class ServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddEphemeralWorker<TWorker>(
        this IHostApplicationBuilder builder,
        Action<EphemeralWorkerOptions>? configure = null)
        where TWorker : class, IEphemeralWorker
    {
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TWorker>();
        builder.Services.AddSingleton<IEphemeralWorker>(provider => provider.GetRequiredService<TWorker>());

        builder.Services.AddSingleton<EphemeralWorkerRegistry>(sp =>
            sp.GetRequiredService<EphemeralWorkerRegistry>());

        builder.Services.Configure<EphemeralWorkerOptions>(options =>
        {
            if (Uri.TryCreate(builder.Configuration["ARGUS_ASSET_SERVICE"], UriKind.Absolute, out var assetService))
            {
                options.AssetServiceBaseAddress = assetService;
            }

            if (Uri.TryCreate(builder.Configuration["ARGUS_RATE_LIMIT_SERVICE"], UriKind.Absolute, out var rateLimitService))
            {
                options.RateLimitServiceBaseAddress = rateLimitService;
            }

            if (Uri.TryCreate(builder.Configuration["ARGUS_REALTIME_SERVICE"], UriKind.Absolute, out var realtimeService))
            {
                options.RealtimeServiceBaseAddress = realtimeService;
            }

            configure?.Invoke(options);
        });

        return builder;
    }

    public static IServiceCollection AddEphemeralWorkerDispatcher(
        this IServiceCollection services,
        int maxConcurrency = 50)
    {
        services.AddSingleton<EphemeralWorkerRegistry>();
        services.AddHostedService<EphemeralWorkerDispatcher>(sp =>
            new EphemeralWorkerDispatcher(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<EphemeralWorkerRegistry>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EphemeralWorkerDispatcher>>(),
                maxConcurrency));

        return services;
    }

    public static IServiceCollection AddEphemeralWorkerRegistry(this IServiceCollection services)
    {
        services.AddSingleton<EphemeralWorkerRegistry>();
        return services;
    }
}

public sealed class EphemeralWorkerOptions
{
    public Uri AssetServiceBaseAddress { get; set; } = new("http://asset-service");
    public Uri RateLimitServiceBaseAddress { get; set; } = new("http://rate-limit-service");
    public Uri RealtimeServiceBaseAddress { get; set; } = new("http://realtime-service");
}
