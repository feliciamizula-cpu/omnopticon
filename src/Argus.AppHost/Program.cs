var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");
var rabbitMq = builder.AddRabbitMQ("eventbus")
    .WithLifetime(ContainerLifetime.Persistent);
var postgres = builder.AddPostgres("postgres")
    .WithImage("ankane/pgvector")
    .WithImageTag("latest")
    .WithLifetime(ContainerLifetime.Persistent);

var argusDb = postgres.AddDatabase("argusdb");

var programScope = builder.AddProject<Projects.Argus_ProgramScopeService>("program-scope-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var asset = builder.AddProject<Projects.Argus_AssetService>("asset-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var task = builder.AddProject<Projects.Argus_TaskService>("task-service")
    .WithReference(argusDb)
    .WithReference(redis)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var rateLimit = builder.AddProject<Projects.Argus_RateLimitService>("rate-limit-service")
    .WithReference(redis)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var orchestrator = builder.AddProject<Projects.Argus_ScanOrchestratorService>("scan-orchestrator-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WaitFor(programScope)
    .WaitFor(asset)
    .WaitFor(task);

var realtime = builder.AddProject<Projects.Argus_RealtimeService>("realtime-service")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

programScope.WithReference(realtime);
asset.WithReference(realtime);
task.WithReference(realtime);
rateLimit.WithReference(realtime);
orchestrator.WithReference(realtime);

builder.AddProject<Projects.Argus_Workers_Amass>("amass-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Subfinder>("subfinder-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_DnsResolver>("dns-resolver-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HttpProbe>("http-probe-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HtmlDomSpider>("html-dom-spider-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_JsExtractor>("js-extractor-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_WordlistDiscovery>("wordlist-discovery-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HeadlessSpider>("headless-spider-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Fingerprint>("fingerprint-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_AssetScoring>("asset-scoring-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(programScope)
    .WithReference(task)
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Http>("http-worker")
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_AssetStorage>("asset-storage-worker")
    .WithReference(asset)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_ApiGateway>("argus-api-gateway")
    .WithExternalHttpEndpoints()
    .WithReference(programScope)
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime);

builder.AddProject<Projects.Argus_Web>("argus-web")
    .WithExternalHttpEndpoints()
    .WithReference(programScope)
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime);

redis.WithParentRelationship(rateLimit);

builder.Build().Run();
