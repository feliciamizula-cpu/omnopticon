using Argus.BuildingBlocks.EventDrivenWorkers;
using Argus.BuildingBlocks.RateLimiting;
using Argus.BuildingBlocks.WorkerDistribution;
using Argus.Workers.Http;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<TokenBucketRateLimiter>(sp =>
{
    var limiter = new TokenBucketRateLimiter(new TokenBucketOptions
    {
        Capacity = 10,
        RefillRate = 2,
        RefillInterval = TimeSpan.FromSeconds(1)
    });

    var config = sp.GetRequiredService<IConfiguration>();
    var rateLimitConfig = config.GetSection("HttpRateLimits");
    foreach (var section in rateLimitConfig.GetChildren())
    {
        var domain = section["Domain"];
        if (!string.IsNullOrEmpty(domain))
        {
            var options = new TokenBucketOptions();
            if (int.TryParse(section["Capacity"], out var capacity)) options.Capacity = capacity;
            if (double.TryParse(section["RefillRate"], out var refillRate)) options.RefillRate = refillRate;
            if (int.TryParse(section["RefillIntervalSeconds"], out var interval)) options.RefillInterval = TimeSpan.FromSeconds(interval);

            limiter.ConfigureBucket($"http:{domain.ToLowerInvariant()}", options);
        }
    }

    return limiter;
});

builder.Services.AddSingleton<RoundRobinWorkerDistributor>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var poolSize = int.TryParse(config["HttpWorkerPoolSize"], out var size) ? size : 10;
    return new RoundRobinWorkerDistributor(poolSize);
});

builder.Services.AddEphemeralWorkerRegistry();
builder.Services.AddEphemeralWorkerDispatcher(maxConcurrency: 50);

builder.AddEphemeralWorker<HttpWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var registry = scope.ServiceProvider.GetRequiredService<EphemeralWorkerRegistry>();
    registry.Register<HttpWorker>();
}

await app.RunAsync();
