using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

                if (!string.IsNullOrEmpty(builder.Configuration["ARGUS_EVENT_DRIVEN_MODE"]))
                {
                    options.EventDrivenMode = bool.TryParse(builder.Configuration["ARGUS_EVENT_DRIVEN_MODE"], out var eventDriven) && eventDriven;
                }

                if (int.TryParse(builder.Configuration["ARGUS_DRAIN_TIMEOUT_SECONDS"], out var drainTimeoutSeconds))
                {
                    options.DrainTimeout = TimeSpan.FromSeconds(drainTimeoutSeconds);
                }

                if (!string.IsNullOrEmpty(builder.Configuration["ARGUS_SAVE_CHECKPOINT_ON_SHUTDOWN"]))
                {
                    options.SaveCheckpointOnShutdown = bool.TryParse(builder.Configuration["ARGUS_SAVE_CHECKPOINT_ON_SHUTDOWN"], out var save) && save;
                }

                configure?.Invoke(options);
            });

        builder.Services.AddSingleton<TaskNotificationChannel>();

        builder.Services.AddHostedService(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArgusWorkerOptions>>().Value;
            return opts.EventDrivenMode
                ? new ArgusEventDrivenWorkerService(
                    sp.GetRequiredService<IReconWorker>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArgusWorkerOptions>>(),
                    sp.GetRequiredService<ILogger<ArgusEventDrivenWorkerService>>(),
                    sp.GetRequiredService<ArgusMetrics>(),
                    sp.GetRequiredService<TaskNotificationChannel>().Reader)
                : new ArgusWorkerBackgroundService(
                    sp.GetRequiredService<IReconWorker>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArgusWorkerOptions>>(),
                    sp.GetRequiredService<ILogger<ArgusWorkerBackgroundService>>(),
                    sp.GetRequiredService<ArgusMetrics>());
        });

        return builder;
    }
}