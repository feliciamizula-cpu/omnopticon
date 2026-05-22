using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Argus.BuildingBlocks.EventBus;

public static class ServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddRealtimeIntegrationEvents(
        this IHostApplicationBuilder builder,
        Action<ArgusEventBusOptions>? configure = null)
    {
        builder.Services.AddHttpClient();
        builder.Services.AddOptions<ArgusEventBusOptions>()
            .Configure(options =>
            {
                options.SourceService = builder.Configuration["ARGUS_SOURCE_SERVICE"] ?? options.SourceService;

                if (Uri.TryCreate(builder.Configuration["ARGUS_REALTIME_SERVICE"], UriKind.Absolute, out var realtimeService))
                {
                    options.RealtimeServiceBaseAddress = realtimeService;
                }

                configure?.Invoke(options);
            });

        builder.Services.AddSingleton<IIntegrationEventPublisher, RealtimeIntegrationEventPublisher>();

        return builder;
    }

    public static IHostApplicationBuilder AddRabbitMqIntegrationEvents(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ArgusEventBusOptions>? configure = null)
    {
        builder.AddRabbitMQClient(connectionName);
        builder.Services.AddOptions<ArgusEventBusOptions>()
            .Configure(options =>
            {
                options.SourceService = builder.Configuration["ARGUS_SOURCE_SERVICE"] ?? options.SourceService;
                configure?.Invoke(options);
            });

        builder.Services.AddSingleton<RabbitMqIntegrationEventPublisher>();
        builder.Services.AddSingleton<IIntegrationEventPublisher>(provider =>
            provider.GetRequiredService<RabbitMqIntegrationEventPublisher>());

        return builder;
    }
}
