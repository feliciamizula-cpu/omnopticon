using Argus.AssetService.Data;
using Argus.AssetService.Search;
using Argus.AssetService.Stores;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Dapper;
using Microsoft.EntityFrameworkCore;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

var argusDbConnectionString = builder.Configuration.GetConnectionString("argusdb");

if (!string.IsNullOrWhiteSpace(argusDbConnectionString))
{
    builder.Services.AddDbContext<AssetDbContext>(options =>
        options.UseNpgsql(argusDbConnectionString));

    builder.Services.AddArgusEfCoreOutbox<AssetDbContext>();
    builder.Services.AddArgusInboxConsumer<AssetDbContext>();

    builder.Services.AddHealthChecks()
        .AddNpgSql(argusDbConnectionString, name: "argusdb", tags: ["db", "sql", "postgres"]);

    builder.Services.AddSingleton<AssetSearchService>();
    builder.Services.AddScoped<IAssetStore, EfAssetStore>();
    builder.Services.AddScoped<TaskCompletedConsumer>();
    builder.Services.AddScoped<IIntegrationEventConsumer<TaskCompleted>>(provider => provider.GetRequiredService<TaskCompletedConsumer>());
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Missing required connection string 'argusdb'. In-memory asset storage is allowed only in Development.");
    }

    builder.Services.AddSingleton<IAssetStore, InMemoryAssetStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeAssetStoreAsync();

app.MapDefaultEndpoints();

Argus.AssetService.Endpoints.AssetEndpoints.MapRoutes(app);

app.Run();

internal static class ServiceUriHelper
{
    public static string GetServiceUri(string configKey, string fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(configKey);

        if (!string.IsNullOrWhiteSpace(envValue) && Uri.TryCreate(envValue, UriKind.Absolute, out var uri))
        {
            return uri.ToString();
        }

        return fallback;
    }
}

internal sealed class TaskCompletedConsumer : IIntegrationEventConsumer<TaskCompleted>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TaskCompletedConsumer> _logger;

    public TaskCompletedConsumer(IHttpClientFactory httpClientFactory, ILogger<TaskCompletedConsumer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task HandleAsync(IntegrationEventEnvelope<TaskCompleted> envelope, CancellationToken cancellationToken)
    {
        if (!envelope.Payload.InputAssetId.HasValue)
        {
            return;
        }

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(ServiceUriHelper.GetServiceUri("ARGUS_ASSET_SERVICE", "http://asset-service"));

        try
        {
            using var response = await client.PatchAsync($"/assets/{envelope.Payload.InputAssetId}/last-scanned", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Updated LastScannedAt for asset {AssetId} after task {TaskId} completed",
                    envelope.Payload.InputAssetId,
                    envelope.Payload.TaskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastScannedAt for asset {AssetId}", envelope.Payload.InputAssetId);
        }
    }
}

internal static class AssetStoreInitialization
{
    public static async Task InitializeAssetStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<AssetDbContext>();

        if (dbContext is not null)
        {
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.EnsureArgusOutboxCreatedAsync();
            await dbContext.Database.EnsureArgusInboxCreatedAsync();
        }
    }
}

public sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);


