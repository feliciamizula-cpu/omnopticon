var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var rabbitMq = builder.AddRabbitMQ("eventbus")
    .WithLifetime(ContainerLifetime.Persistent);

var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16")
    .WithLifetime(ContainerLifetime.Persistent);

var argusDb = postgres.AddDatabase("argusdb");

var programScope = builder.AddProject<Projects.Argus_ProgramScopeService>("program-scope-service")
    .PublishAsArgusImage("src/Services/Argus.ProgramScopeService/Argus.ProgramScopeService.csproj", "Argus.ProgramScopeService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var asset = builder.AddProject<Projects.Argus_AssetService>("asset-service")
    .PublishAsArgusImage("src/Services/Argus.AssetService/Argus.AssetService.csproj", "Argus.AssetService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

programScope
    .WithReference(asset)
    .WaitFor(asset);

var artifact = builder.AddProject<Projects.Argus_ArtifactService>("artifact-service")
    .PublishAsArgusImage("src/Services/Argus.ArtifactService/Argus.ArtifactService.csproj", "Argus.ArtifactService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var finding = builder.AddProject<Projects.Argus_FindingService>("finding-service")
    .PublishAsArgusImage("src/Services/Argus.FindingService/Argus.FindingService.csproj", "Argus.FindingService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var task = builder.AddProject<Projects.Argus_TaskService>("task-service")
    .PublishAsArgusImage("src/Services/Argus.TaskService/Argus.TaskService.csproj", "Argus.TaskService")
    .WithReference(argusDb)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var rateLimit = builder.AddProject<Projects.Argus_RateLimitService>("rate-limit-service")
    .PublishAsArgusImage("src/Services/Argus.RateLimitService/Argus.RateLimitService.csproj", "Argus.RateLimitService")
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithReference(programScope)
    .WaitFor(rabbitMq)
    .WaitFor(programScope);

var orchestrator = builder.AddProject<Projects.Argus_ScanOrchestratorService>("scan-orchestrator-service")
    .PublishAsArgusImage("src/Services/Argus.ScanOrchestratorService/Argus.ScanOrchestratorService.csproj", "Argus.ScanOrchestratorService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WithReference(programScope)
    .WaitFor(rabbitMq)
    .WaitFor(programScope)
    .WaitFor(asset)
    .WaitFor(task);

var realtime = builder.AddProject<Projects.Argus_RealtimeService>("realtime-service")
    .PublishAsArgusImage("src/Services/Argus.RealtimeService/Argus.RealtimeService.csproj", "Argus.RealtimeService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var eventRouter = builder.AddProject<Projects.Argus_EventRouterService>("event-router-service")
    .PublishAsArgusImage("src/Services/Argus.EventRouterService/Argus.EventRouterService.csproj", "Argus.EventRouterService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var proxyRegistry = builder.AddProject<Projects.Argus_ProxyRegistryService>("proxy-registry-service")
    .PublishAsArgusImage("src/Services/Argus.ProxyRegistryService/Argus.ProxyRegistryService.csproj", "Argus.ProxyRegistryService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq);

var agentService = builder.AddProject<Projects.Argus_AgentService>("agent-service")
    .PublishAsArgusImage("src/Services/Argus.AgentService/Argus.AgentService.csproj", "Argus.AgentService")
    .WithReference(argusDb)
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithEnvironment("OPENCODE_AUTH_TOKEN", Environment.GetEnvironmentVariable("OPENCODE_AUTH_TOKEN") ?? "")
    .WithEnvironment("OPENROUTER_API_KEY", Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "")
    .WithEnvironment("OPENAI_API_KEY", Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "")
    .WithEnvironment("ANTHROPIC_API_KEY", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "")
    .WithEnvironment("FIREWORKS_API_KEY", Environment.GetEnvironmentVariable("FIREWORKS_API_KEY") ?? "")
    .WithEnvironment("CODEX_FIVE_HOUR_LIMIT", Environment.GetEnvironmentVariable("CODEX_FIVE_HOUR_LIMIT") ?? "")
    .WithEnvironment("CODEX_TWENTY_FOUR_HOUR_LIMIT", Environment.GetEnvironmentVariable("CODEX_TWENTY_FOUR_HOUR_LIMIT") ?? "")
    .WithEnvironment("CODEX_WEEKLY_LIMIT", Environment.GetEnvironmentVariable("CODEX_WEEKLY_LIMIT") ?? "")
    .WithEnvironment("CODEX_MONTHLY_LIMIT", Environment.GetEnvironmentVariable("CODEX_MONTHLY_LIMIT") ?? "");

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
    .PublishAsArgusImage("src/Workers/Argus.Workers.Amass/Argus.Workers.Amass.csproj", "Argus.Workers.Amass")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Subfinder>("subfinder-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.Subfinder/Argus.Workers.Subfinder.csproj", "Argus.Workers.Subfinder")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_DnsResolver>("dns-resolver-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.DnsResolver/Argus.Workers.DnsResolver.csproj", "Argus.Workers.DnsResolver")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_HttpProbe>("http-probe-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.HttpProbe/Argus.Workers.HttpProbe.csproj", "Argus.Workers.HttpProbe")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.HtmlDomSpider/Argus.Workers.HtmlDomSpider.csproj", "Argus.Workers.HtmlDomSpider")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.JsExtractor/Argus.Workers.JsExtractor.csproj", "Argus.Workers.JsExtractor")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.RegexScanner/Argus.Workers.RegexScanner.csproj", "Argus.Workers.RegexScanner")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_WordlistDiscovery>("wordlist-discovery-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.WordlistDiscovery/Argus.Workers.WordlistDiscovery.csproj", "Argus.Workers.WordlistDiscovery")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.HeadlessSpider/Argus.Workers.HeadlessSpider.csproj", "Argus.Workers.HeadlessSpider")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.Fingerprint/Argus.Workers.Fingerprint.csproj", "Argus.Workers.Fingerprint")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
    .WithEnvironment("ARGUS_SCOPE_VALIDATION_REQUIRED", "true")
    .WithReference(asset)
    .WithReference(task)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(task)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_Validation>("validation-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.Validation/Argus.Workers.Validation.csproj", "Argus.Workers.Validation")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "validation-service")
    .WithReference(asset)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_AssetScoring>("asset-scoring-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.AssetScoring/Argus.Workers.AssetScoring.csproj", "Argus.Workers.AssetScoring")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.FindingDeduper/Argus.Workers.FindingDeduper.csproj", "Argus.Workers.FindingDeduper")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "continuous")
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
    .PublishAsArgusImage("src/Workers/Argus.Workers.Http/Argus.Workers.Http.csproj", "Argus.Workers.Http")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "ephemeral")
    .WithReference(asset)
    .WithReference(rateLimit)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(rateLimit)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_Workers_AssetStorage>("asset-storage-worker")
    .PublishAsArgusImage("src/Workers/Argus.Workers.AssetStorage/Argus.Workers.AssetStorage.csproj", "Argus.Workers.AssetStorage")
    .WithEnvironment("ARGUS_WORKER_RUNTIME", "ephemeral")
    .WithReference(asset)
    .WithReference(realtime)
    .WaitFor(asset)
    .WaitFor(realtime);

builder.AddProject<Projects.Argus_ApiGateway>("argus-api-gateway")
    .PublishAsArgusImage("src/Argus.ApiGateway/Argus.ApiGateway.csproj", "Argus.ApiGateway")
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
    .PublishAsArgusImage("src/Argus.Web/Argus.Web.csproj", "Argus.Web")
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
