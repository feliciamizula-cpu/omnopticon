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
    builder.Services.AddDbContext<RequestToolDbContext>(options =>
        options.UseNpgsql(argusDbConnectionString));

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
builder.Services.AddHttpClient<IAssetServiceClient, AssetServiceClient>();
builder.Services.AddHttpClient<IArtifactServiceClient, ArtifactServiceClient>();
builder.Services.AddHttpClient<IProgramScopeServiceClient, ProgramScopeServiceClient>();
builder.Services.AddHttpClient<IRateLimitServiceClient, RateLimitServiceClient>();
builder.Services.AddHttpClient<IProxyRegistryServiceClient, ProxyRegistryServiceClient>();

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