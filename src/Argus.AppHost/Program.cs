var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var rabbitMq = builder.AddRabbitMQ("eventbus")
    .WithLifetime(ContainerLifetime.Persistent);

var postgres = builder.AddPostgres("postgres")
    .WithImage("ankane/pgvector")
    .WithImageTag("pg16-v0.8.0")
    .WithLifetime(ContainerLifetime.Persistent);

var argusDb = postgres.AddDatabase("argusdb");

var programScope = builder.AddProject<Projects.Argus_ProgramScopeService>("program-scope-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var asset = builder.AddProject<Projects.Argus_AssetService>("asset-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var artifact = builder.AddProject<Projects.Argus_ArtifactService>("artifact-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var finding = builder.AddProject<Projects.Argus_FindingService>("finding-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var task = builder.AddProject<Projects.Argus_TaskService>("task-service")
    .WithReference(argusDb)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var rateLimit = builder.AddProject<Projects.Argus_RateLimitService>("rate-limit-service")
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithReference(programScope)
    .WaitFor(rabbitMq)
    .WaitFor(programScope)
    .WithHttpHealthCheck("/health");

var orchestrator = builder.AddProject<Projects.Argus_ScanOrchestratorService>("scan-orchestrator-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WithReference(programScope)
    .WaitFor(rabbitMq)
    .WaitFor(programScope)
    .WaitFor(asset)
    .WaitFor(task);

var realtime = builder.AddProject<Projects.Argus_RealtimeService>("realtime-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var eventRouter = builder.AddProject<Projects.Argus_EventRouterService>("event-router-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

var proxyRegistry = builder.AddProject<Projects.Argus_ProxyRegistryService>("proxy-registry-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

asset.WithReference(realtime);
task.WithReference(realtime);
rateLimit.WithReference(realtime);
orchestrator.WithReference(realtime);
proxyRegistry.WithReference(realtime);
artifact.WithReference(realtime);
finding.WithReference(realtime);
eventRouter.WithReference(realtime);
programScope.WithReference(realtime);

builder.AddProject<Projects.Argus_Workers_Amass>("amass-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Subfinder>("subfinder-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_DnsResolver>("dns-resolver-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HttpProbe>("http-probe-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HtmlDomSpider>("html-dom-spider-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_JsExtractor>("js-extractor-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_WordlistDiscovery>("wordlist-discovery-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HeadlessSpider>("headless-spider-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Fingerprint>("fingerprint-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Validation>("validation-worker")
    .WithReference(asset)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_AssetScoring>("asset-scoring-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(artifact)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_FindingDeduper>("finding-deduper-worker")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(artifact)
    .WaitFor(finding)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_ApiGateway>("argus-api-gateway")
    .WithExternalHttpEndpoints()
    .WithReference(programScope)
    .WithReference(asset)
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime)
    .WithReference(eventRouter);

builder.AddProject<Projects.Argus_Web>("argus-web")
    .WithExternalHttpEndpoints()
    .WithReference(programScope)
    .WithReference(asset)
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime)
    .WithReference(eventRouter);

redis.WithParentRelationship(rateLimit);

builder.Build().Run();
