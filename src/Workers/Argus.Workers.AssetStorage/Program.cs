using Argus.BuildingBlocks.Workers;
using Argus.ServiceDefaults;
using Argus.Workers.AssetStorage;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AssetStorageWorker>();

await builder.Build().RunAsync();
