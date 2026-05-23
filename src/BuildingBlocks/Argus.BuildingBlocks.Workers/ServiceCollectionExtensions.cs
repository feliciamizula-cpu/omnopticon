using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Argus.BuildingBlocks.Workers;

public static class ServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddArgusWorker<TWorker>(
        this IHostApplicationBuilder builder,
        Action<ArgusWorkerOptions>? configure = null)
        where TWorker : class, IReconWorker
    {
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TWorker>();
        builder.Services.AddSingleton<IReconWorker>(provider => provider.GetRequiredService<TWorker>());
        builder.Services.AddOptions<ArgusWorkerOptions>()
            .Configure(options =>
            {
                options.WorkerId = builder.Configuration["ARGUS_WORKER_ID"] ?? options.WorkerId;

                if (Uri.TryCreate(builder.Configuration["ARGUS_TASK_SERVICE"], UriKind.Absolute, out var taskService))
                {
                    options.TaskServiceBaseAddress = taskService;
                }

                if (Uri.TryCreate(builder.Configuration["ARGUS_ASSET_SERVICE"], UriKind.Absolute, out var assetService))
                {
                    options.AssetServiceBaseAddress = assetService;
                }

                if (Uri.TryCreate(builder.Configuration["ARGUS_PROGRAM_SCOPE_SERVICE"], UriKind.Absolute, out var scopeService))
                {
                    options.ScopeServiceBaseAddress = scopeService;
                }

                if (Uri.TryCreate(builder.Configuration["ARGUS_RATE_LIMIT_SERVICE"], UriKind.Absolute, out var rateLimitService))
                {
                    options.RateLimitServiceBaseAddress = rateLimitService;
                }

                if (Uri.TryCreate(builder.Configuration["ARGUS_REALTIME_SERVICE"], UriKind.Absolute, out var realtimeService))
                {
                    options.RealtimeServiceBaseAddress = realtimeService;
                }

                if (bool.TryParse(builder.Configuration["ARGUS_SCOPE_VALIDATION_REQUIRED"], out var scopeValidationRequired))
                {
                    options.ScopeValidationRequired = scopeValidationRequired;
                }

                if (!string.IsNullOrEmpty(builder.Configuration["ARGUS_SNAPSHOT_SECRET_KEY"]))
                {
                    options.SnapshotSecretKey = builder.Configuration["ARGUS_SNAPSHOT_SECRET_KEY"]!;
                }

                configure?.Invoke(options);
            });

        builder.Services.AddHostedService<ArgusWorkerBackgroundService>();

        return builder;
    }
}