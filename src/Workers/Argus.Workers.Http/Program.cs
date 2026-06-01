using Argus.BuildingBlocks.Workers;
using Argus.ServiceDefaults;
using Argus.Workers.Http;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HttpWorker>();

await builder.Build().RunAsync();
