using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Argus.BuildingBlocks.EventBus;

public static class ServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddArgusIntegrationEvents(
        this IHostApplicationBuilder builder,
        Action<ArgusEventBusOptions>? configure = null,
        string rabbitMqConnectionName = "eventbus")
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

                if (builder.Configuration.GetConnectionString(rabbitMqConnectionName) is { } rabbitMqConn)
                {
                    options.RabbitMqConnectionString = rabbitMqConn;
                }

                configure?.Invoke(options);
            });

        builder.Services.AddSingleton<RealtimeIntegrationEventPublisher>();

        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(rabbitMqConnectionName)))
        {
            builder.AddRabbitMQClient(rabbitMqConnectionName);
            builder.Services.AddSingleton<RabbitMqIntegrationEventPublisher>();
        }

        builder.Services.AddSingleton<IIntegrationEventPublisher, CompositeIntegrationEventPublisher>();

        return builder;
    }

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
                if (builder.Configuration.GetConnectionString(connectionName) is { } conn)
                {
                    options.RabbitMqConnectionString = conn;
                }
                configure?.Invoke(options);
            });

        builder.Services.AddSingleton<RabbitMqIntegrationEventPublisher>();
        builder.Services.AddSingleton<IIntegrationEventPublisher>(provider =>
            provider.GetRequiredService<RabbitMqIntegrationEventPublisher>());

        return builder;
    }

    public static IServiceCollection AddArgusEfCoreOutbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();
        services.AddScoped<IIntegrationEventPublisher, DurableIntegrationEventPublisher>();
        services.AddHostedService<OutboxDispatcher>();

        return services;
    }

    public static IServiceCollection AddArgusInboxConsumer<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddHostedService<RabbitMqConsumerService<TDbContext>>();
        return services;
    }

    public static IServiceCollection AddArgusPoisonMessageStore(this IServiceCollection services)
    {
        services.AddSingleton<IPoisonMessageStore, InMemoryPoisonMessageStore>();
        return services;
    }
}
