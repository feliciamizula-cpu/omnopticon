using Argus.BuildingBlocks.EventDrivenWorkers;
using Argus.Workers.AssetStorage;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient();

builder.Services.AddEphemeralWorkerRegistry();
builder.Services.AddEphemeralWorkerDispatcher(maxConcurrency: 50);

builder.AddEphemeralWorker<AssetStorageWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var registry = scope.ServiceProvider.GetRequiredService<EphemeralWorkerRegistry>();
    registry.Register<AssetStorageWorker>();
}

await app.RunAsync();
