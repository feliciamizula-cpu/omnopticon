using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.Contracts.Events;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

var JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<AssetDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<AssetDbContext>();
    builder.Services.AddArgusInboxConsumer<AssetDbContext>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
    builder.Services.AddScoped<IAssetStore, EfAssetStore>();
    builder.Services.AddScoped<TaskCompletedConsumer>();
}
else
{
    builder.Services.AddSingleton<IAssetStore, InMemoryAssetStore>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.AssetService");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeAssetStoreAsync();
app.MapDefaultEndpoints();

AssetEndpoints.MapRoutes(app);

app.Run();

internal static class TaskDedupeHash
{
    public static string Compute(Guid programId, Guid? scopeId, string taskType, Guid inputAssetId, string workerCapability)
    {
        var input = $"{programId:N}:{scopeId:N}:{taskType}:{inputAssetId:N}:{workerCapability}";
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

internal static class ServiceUriHelper
{
    public static string GetServiceUri(string configKey, string fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(configKey);
        if (!string.IsNullOrWhiteSpace(envValue) && Uri.TryCreate(envValue, UriKind.Absolute, out var uri))
            return uri.ToString();
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
                _logger.LogDebug("Updated LastScannedAt for asset {AssetId} after task {TaskId} completed",
                    envelope.Payload.InputAssetId, envelope.Payload.TaskId);
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
            await SeedAssetTypeDefinitionsAsync(dbContext);
        }
    }

    private static async Task SeedAssetTypeDefinitionsAsync(AssetDbContext dbContext)
    {
        if (await dbContext.AssetTypeDefinitions.AnyAsync())
            return;

        var definitions = new AssetTypeDefinitionRecord[]
        {
            new() { TypeKey = "domain", DisplayName = "Domain", Category = AssetCategory.Domain, IsEnabled = true, ConfidenceWeight = 1.0m, InterestingScoreBase = 5 },
            new() { TypeKey = "subdomain", DisplayName = "Subdomain", Category = AssetCategory.Subdomain, IsEnabled = true, ConfidenceWeight = 0.9m, InterestingScoreBase = 10 },
            new() { TypeKey = "ip", DisplayName = "IP Address", Category = AssetCategory.Ip, IsEnabled = true, ConfidenceWeight = 1.0m, InterestingScoreBase = 5 },
            new() { TypeKey = "cidr", DisplayName = "CIDR Block", Category = AssetCategory.Network, IsEnabled = true, ConfidenceWeight = 0.8m, InterestingScoreBase = 3 },
            new() { TypeKey = "url", DisplayName = "URL", Category = AssetCategory.Url, IsEnabled = true, ConfidenceWeight = 0.95m, InterestingScoreBase = 15 },
            new() { TypeKey = "http_response", DisplayName = "HTTP Response", Category = AssetCategory.HttpResponse, IsEnabled = true, ConfidenceWeight = 0.85m, InterestingScoreBase = 20 },
            new() { TypeKey = "html_page", DisplayName = "HTML Page", Category = AssetCategory.Document, IsEnabled = true, ConfidenceWeight = 0.8m, InterestingScoreBase = 10 },
            new() { TypeKey = "javascript", DisplayName = "JavaScript File", Category = AssetCategory.Script, IsEnabled = true, ConfidenceWeight = 0.75m, InterestingScoreBase = 20 },
            new() { TypeKey = "css", DisplayName = "CSS File", Category = AssetCategory.Style, IsEnabled = true, ConfidenceWeight = 0.7m, InterestingScoreBase = 5 },
            new() { TypeKey = "json", DisplayName = "JSON Document", Category = AssetCategory.Document, IsEnabled = true, ConfidenceWeight = 0.8m, InterestingScoreBase = 15 },
            new() { TypeKey = "api_endpoint", DisplayName = "API Endpoint", Category = AssetCategory.Api, IsEnabled = true, ConfidenceWeight = 0.9m, InterestingScoreBase = 40 },
            new() { TypeKey = "technology", DisplayName = "Technology", Category = AssetCategory.Technology, IsEnabled = true, ConfidenceWeight = 0.75m, InterestingScoreBase = 25 },
            new() { TypeKey = "port", DisplayName = "Port", Category = AssetCategory.Port, IsEnabled = true, ConfidenceWeight = 0.85m, InterestingScoreBase = 15 },
            new() { TypeKey = "dns_record", DisplayName = "DNS Record", Category = AssetCategory.Network, IsEnabled = true, ConfidenceWeight = 0.8m, InterestingScoreBase = 10 },
            new() { TypeKey = "finding", DisplayName = "Finding", Category = AssetCategory.Finding, IsEnabled = true, ConfidenceWeight = 1.0m, InterestingScoreBase = 85 },
            new() { TypeKey = "finding_candidate", DisplayName = "Finding Candidate", Category = AssetCategory.Finding, IsEnabled = true, ConfidenceWeight = 0.9m, InterestingScoreBase = 70 },
        };

        dbContext.AssetTypeDefinitions.AddRange(definitions);
        await dbContext.SaveChangesAsync();
    }
}

internal sealed record AssetUpsertResult(AssetDto Asset, bool WasCreated);