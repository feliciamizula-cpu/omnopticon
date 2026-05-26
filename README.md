# Argus Recon Platform

A distributed bug-bounty reconnaissance platform built on Microsoft .NET Aspire. Argus discovers, processes, classifies, and visualizes assets across authorized bug-bounty targets at scale.

> **SDK note:** this repository currently targets .NET 10 / Aspire 13.2 preview-era packages. Build agents and contractor machines must use the SDK pinned in `global.json`.

## Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                         Argus.AppHost                           │
│                    (Aspire Orchestrator)                        │
└─────────────────────────────────────────────────────────────────┘
         │                 │                  │
         ▼                 ▼                  ▼
┌────────────────┐ ┌────────────────┐ ┌──────────────────────────┐
│   Argus.Web    │ │ Argus.ApiGateway│ │ Workers                  │
│   Command UI   │ │ YARP Gateway    │ │ Discovery + processing   │
└────────────────┘ └────────────────┘ └──────────────────────────┘
         │                 │                  │
         └─────────────────┴──────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│ Services: ProgramScope, Asset, Artifact, Finding, Task,          │
│ RateLimit, Realtime, EventRouter, ProxyRegistry, Orchestrator    │
└─────────────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│ Infrastructure: PostgreSQL + pgvector, Redis, RabbitMQ           │
└─────────────────────────────────────────────────────────────────┘
```

## Core Concepts

**Assets** are the fundamental entity. Every discovered host, domain, URL, endpoint, artifact, or finding is modeled as an asset with metadata, relationships, timestamps, and scoring.

**Workers** are long-running services that subscribe to asset and task events and perform reconnaissance work such as subdomain enumeration, DNS resolution, HTTP probing, crawling, fingerprinting, validation, scoring, and finding deduplication.

**Tasks** provide durable, resumable work tracking. Workers lease tasks, report progress, and complete or fail so work can be recovered after interruption.

**Rate limiting** is distributed and tiered by program, scope, and host to respect bug-bounty rules while maximizing throughput.

## Services

| Service | Started by AppHost | Routed by Gateway | Purpose |
|---|---:|---:|---|
| `ProgramScopeService` | Yes | Yes | Bug-bounty programs, scopes, targets, and settings |
| `AssetService` | Yes | Yes | Asset storage, query, relationships, tagging, bulk enqueue |
| `ArtifactService` | Yes | Yes | Scan artifacts and evidence |
| `FindingService` | Yes | Yes | Vulnerability finding records |
| `TaskService` | Yes | Yes | Durable task orchestration with leasing and checkpoints |
| `RateLimitService` | Yes | Yes | Distributed multi-tier rate limiting |
| `RealtimeService` | Yes | Yes | Server-sent events and worker status |
| `EventRouterService` | Yes | Yes | Event routing configuration |
| `ProxyRegistryService` | Yes | No | Proxy registry support |
| `ScanOrchestratorService` | Yes | No | Workflow orchestration for discovery pipelines |

## Workers

| Worker | Input | Output |
|---|---|---|
| Amass | Domain | Subdomain, DnsRecord |
| Subfinder | Domain | Subdomain |
| DnsResolver | Domain, Subdomain | Ip, DnsRecord |
| HttpProbe | Subdomain, Ip | Url, HttpResponse |
| HtmlDomSpider | Url, HtmlPage | Url, ApiEndpoint, JavaScriptFile |
| JsExtractor | JavaScriptFile | ApiEndpoint, Url, FindingCandidate |
| WordlistDiscovery | Url | Url |
| HeadlessSpider | Url | Url, ApiEndpoint, JavaScriptFile |
| Fingerprint | Url, HttpResponse, HtmlPage | Technology |
| Validation | Asset | Validation status |
| AssetScoring | Asset/FindingCandidate | Scores and finding candidates |
| FindingDeduper | FindingCandidate | Deduplicated findings |

## Event-Driven Workflow

```text
AssetDiscovered (Domain)
    │
    ├──► AmassWorker ──────► SubdomainAssetDiscovered
    ├──► SubfinderWorker ──► SubdomainAssetDiscovered
    │
SubdomainAssetDiscovered ──► DnsResolverWorker ──► IpAssetDiscovered
                                │
                                ▼
                         HttpProbeWorker ──► UrlAssetDiscovered
                                                │
                                                ▼
                         HtmlDomSpider ──► Url, ApiEndpoint, JS
                                                │
                                                ▼
                         JsExtractor ──► ApiEndpoint, FindingCandidate
                                                │
                                                ▼
                         FingerprintWorker ──► Technology
                                                │
                                                ▼
                         AssetScoringWorker ──► FindingCandidate
                                                │
                                                ▼
                         FindingDeduperWorker ──► Finding
```

## Asset Types

Domain, Subdomain, Ip, Cidr, Url, HttpResponse, HtmlPage, JavaScriptFile, ApiEndpoint, Technology, FindingCandidate, Port, DnsRecord, Program, Scope, CssFile, JsonDocument.

## Technology Stack

- **.NET 10** with Aspire for orchestration
- **PostgreSQL + pgvector** for normalized metadata and vector-ready enrichment
- **Redis** for leases, rate limits, and caching
- **RabbitMQ** for event bus messaging
- **YARP service discovery** for API gateway forwarding
- **Playwright** for browser-based end-to-end tests

## Local Development

```bash
# Prerequisites: .NET SDK from global.json and Docker Desktop
dotnet restore eShop.slnx
dotnet build eShop.slnx
dotnet run --project src/Argus.AppHost/Argus.AppHost.csproj
```

The Aspire dashboard provides service URLs, logs, traces, metrics, and health status.

## Validation

```bash
dotnet restore eShop.slnx
dotnet build eShop.slnx --configuration Release
dotnet test eShop.slnx --configuration Release
docker compose --env-file deploy/argus.env.example -f deploy/compose.yaml config --quiet
```

## Container Deployment

```bash
cp deploy/argus.env.example deploy/argus.env
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

| Endpoint | URL |
|---|---|
| Web UI | http://localhost:8080 |
| API Gateway | http://localhost:8081 |
| Realtime SSE | http://localhost:8082 |

## Seed Demo Data

```bash
tools/seed-demo-data.sh
```

## Project Structure

```text
src/
├── Argus.AppHost/                  Aspire orchestrator
├── Argus.ApiGateway/               API gateway
├── Argus.Web/                      Web UI command center
├── Argus.ServiceDefaults/          Shared telemetry, health, resilience
├── Contracts/                      DTOs and event contracts
├── BuildingBlocks/
│   ├── Argus.BuildingBlocks.EventBus/
│   └── Argus.BuildingBlocks.Workers/
├── Services/
│   ├── Argus.ArtifactService/
│   ├── Argus.AssetService/
│   ├── Argus.EventRouterService/
│   ├── Argus.FindingService/
│   ├── Argus.ProgramScopeService/
│   ├── Argus.ProxyRegistryService/
│   ├── Argus.RateLimitService/
│   ├── Argus.RealtimeService/
│   ├── Argus.ScanOrchestratorService/
│   └── Argus.TaskService/
└── Workers/
    ├── Argus.Workers.Amass/
    ├── Argus.Workers.AssetScoring/
    ├── Argus.Workers.DnsResolver/
    ├── Argus.Workers.FindingDeduper/
    ├── Argus.Workers.Fingerprint/
    ├── Argus.Workers.HeadlessSpider/
    ├── Argus.Workers.HtmlDomSpider/
    ├── Argus.Workers.HttpProbe/
    ├── Argus.Workers.JsExtractor/
    ├── Argus.Workers.Subfinder/
    ├── Argus.Workers.Validation/
    └── Argus.Workers.WordlistDiscovery/
```
