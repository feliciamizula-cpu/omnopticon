using System.Net;
using Argus.RequestToolService.Data;
using Argus.RequestToolService.Endpoints;
using Argus.RequestToolService.Http;
using Argus.RequestToolService.Options;
using Argus.RequestToolService.Services;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

var argusDbConnectionString = builder.Configuration.GetConnectionString("argusdb");

builder.Services.AddProblemDetails();
builder.Services.Configure<RequestToolOptions>(
    builder.Configuration.GetSection(RequestToolOptions.SectionName));

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("request-tool-replay")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

if (!string.IsNullOrWhiteSpace(argusDbConnectionString))
{
    builder.Services.AddHealthChecks()
        .AddNpgSql(argusDbConnectionString, name: "argusdb", tags: ["db", "sql", "postgres"]);
}

builder.Services.AddScoped<IRequestToolRepository, RequestToolRepository>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IHttpReplayExecutor, HttpReplayExecutor>();
builder.Services.AddScoped<IBodyStorageService, BodyStorageService>();
builder.Services.AddScoped<IRedactionService, RedactionService>();
builder.Services.AddScoped<IRawHttpRenderer, RawHttpRenderer>();
builder.Services.AddScoped<IRequestToolDiffService, RequestToolDiffService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAssetEvidenceHydrator, AssetEvidenceHydrator>();
// Fuzz executor uses IDbContextFactory so it can create independent DB scopes from background tasks.
// Register the context only through the singleton factory (singleton options); scoped consumers get
// an instance created from that factory. Registering AddDbContext (scoped options) alongside a
// singleton factory makes the factory consume scoped DbContextOptions and fails DI validation.
builder.Services.AddDbContextFactory<RequestToolDbContext>(options =>
    options.UseNpgsql(argusDbConnectionString ?? ""), ServiceLifetime.Singleton);
builder.Services.AddScoped<RequestToolDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<RequestToolDbContext>>().CreateDbContext());
builder.Services.AddSingleton<IFuzzExecutor, FuzzExecutor>();
// These typed clients call downstream services with relative URIs, so each needs a BaseAddress.
// Without it every call throws "BaseAddress must be set" — which surfaced as the request-tool
// "Asset not found" / session-load failure (the service couldn't fetch the asset).
static string ServiceUri(IConfiguration cfg, string envKey, string fallbackHost)
{
    var value = cfg[envKey];
    if (string.IsNullOrWhiteSpace(value))
        return $"http://{fallbackHost}:8080";
    // Normalize Aspire-style scheme prefixes ("https+http://host") to a plain in-cluster address.
    if (value.StartsWith("https+http://", StringComparison.OrdinalIgnoreCase))
        return "http://" + value["https+http://".Length..];
    if (value.StartsWith("http+https://", StringComparison.OrdinalIgnoreCase))
        return "http://" + value["http+https://".Length..];
    return value;
}

builder.Services.AddHttpClient<IAssetServiceClient, AssetServiceClient>(c =>
    c.BaseAddress = new Uri(ServiceUri(builder.Configuration, "ARGUS_ASSET_SERVICE", "asset-service")));
builder.Services.AddHttpClient<IArtifactServiceClient, ArtifactServiceClient>(c =>
    c.BaseAddress = new Uri(ServiceUri(builder.Configuration, "ARGUS_ARTIFACT_SERVICE", "artifact-service")));
builder.Services.AddHttpClient<IProgramScopeServiceClient, ProgramScopeServiceClient>(c =>
    c.BaseAddress = new Uri(ServiceUri(builder.Configuration, "ARGUS_PROGRAM_SCOPE_SERVICE", "program-scope-service")));
builder.Services.AddHttpClient<IRateLimitServiceClient, RateLimitServiceClient>(c =>
    c.BaseAddress = new Uri(ServiceUri(builder.Configuration, "ARGUS_RATE_LIMIT_SERVICE", "rate-limit-service")));
builder.Services.AddHttpClient<IProxyRegistryServiceClient, ProxyRegistryServiceClient>(c =>
    c.BaseAddress = new Uri(ServiceUri(builder.Configuration, "ARGUS_PROXY_REGISTRY_SERVICE", "proxy-registry-service")));

var app = builder.Build();

app.MapDefaultEndpoints();

if (!string.IsNullOrWhiteSpace(argusDbConnectionString))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetService<RequestToolDbContext>();
    if (dbContext is not null)
    {
        await dbContext.EnsureRequestToolSchemaCreatedAsync();
    }
}

RequestToolEndpoints.MapRoutes(app);

app.Run();