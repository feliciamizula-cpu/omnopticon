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
    .WaitFor(rabbitMq);

var asset = builder.AddProject<Projects.Argus_AssetService>("asset-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

programScope
    .WithReference(asset)
    .WaitFor(asset);

var artifact = builder.AddProject<Projects.Argus_ArtifactService>("artifact-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var finding = builder.AddProject<Projects.Argus_FindingService>("finding-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var task = builder.AddProject<Projects.Argus_TaskService>("task-service")
    .WithReference(argusDb)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var rateLimit = builder.AddProject<Projects.Argus_RateLimitService>("rate-limit-service")
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithReference(programScope)
    .WaitFor(rabbitMq)
    .WaitFor(programScope);

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
    .WaitFor(rabbitMq);

var eventRouter = builder.AddProject<Projects.Argus_EventRouterService>("event-router-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var proxyRegistry = builder.AddProject<Projects.Argus_ProxyRegistryService>("proxy-registry-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var agentService = builder.AddProject<Projects.Argus_AgentService>("agent-service")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithEnvironment("OPENCODE_AUTH_TOKEN", "<OPENCODE_AUTH_TOKEN>")
    .WithEnvironment("OPENROUTER_API_KEY", "<OPENROUTER_API_KEY>")
    .WithEnvironment("OPENAI_API_KEY", "<OPENAI_API_KEY>")
    .WithEnvironment("ANTHROPIC_API_KEY", "<ANTHROPIC_API_KEY>")
    .WithEnvironment("FIREWORKS_API_KEY", "<FIREWORKS_API_KEY>")
    .WithEnvironment("CODEX_FIVE_HOUR_LIMIT", "<CODEX_FIVE_HOUR_LIMIT>")
    .WithEnvironment("CODEX_TWENTY_FOUR_HOUR_LIMIT", "<CODEX_TWENTY_FOUR_HOUR_LIMIT>")
    .WithEnvironment("CODEX_WEEKLY_LIMIT", "<CODEX_WEEKLY_LIMIT>")
    .WithEnvironment("CODEX_MONTHLY_LIMIT", "<CODEX_MONTHLY_LIMIT>");

asset.WithReference(realtime);
task.WithReference(realtime);
rateLimit.WithReference(realtime);
orchestrator.WithReference(realtime);
proxyRegistry.WithReference(realtime);
agentService.WithReference(realtime);
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

builder.AddProject<Projects.Argus_Workers_RegexScanner>("regex-scanner-worker")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
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
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime)
    .WithReference(agentService)
    .WithReference(eventRouter);

builder.AddProject<Projects.Argus_Web>("argus-web")
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:8082")
    .WithReference(programScope)
    .WithReference(asset)
    .WithReference(artifact)
    .WithReference(finding)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(orchestrator)
    .WithReference(realtime)
    .WithReference(agentService)
    .WithReference(eventRouter);

redis.WithParentRelationship(rateLimit);

builder.Build().Run();
