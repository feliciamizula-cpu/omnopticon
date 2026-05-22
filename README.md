# Argus Recon Platform

A distributed bug-bounty reconnaissance platform built on Microsoft .NET Aspire. Argus discovers, processes, classifies, and visualizes assets across authorized bug-bounty targets at scale.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         Argus.AppHost                           │
│                    (Aspire Orchestrator)                        │
└─────────────────────────────────────────────────────────────────┘
         │              │              │              │
         ▼              ▼              ▼              ▼
┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│  Argus.Web   │ │ Argus.Api   │ │   Workers   │ │  Services   │
│   (UI)       │ │  Gateway    │ │  (10 types) │ │  (6 types)  │
└─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘
                                           │              │
                                    ┌──────┴──────────────┴──────┐
                                    │     Infrastructure        │
                                    │  PostgreSQL / Redis /     │
                                    │  RabbitMQ                 │
                                    └───────────────────────────┘
```

## Core Concepts

**Assets** are the fundamental entity. Every discovered host, domain, URL, endpoint, or finding is an asset with metadata, relationships, timestamps, and scoring.

**Workers** are long-running services that subscribe to asset events and perform reconnaissance tasks (subdomain enumeration, HTTP probing, spidering, fingerprinting, etc.).

**Tasks** provide durable, resumable work tracking. Workers lease tasks, report progress, and complete or fail — ensuring no work is lost on interruption.

**Rate Limiting** is distributed and tiered (per-program, per-scope, per-host) to respect bug-bounty rules while maximizing throughput.

## Services

| Service | Purpose |
|---------|---------|
| `ProgramScopeService` | Manage bug-bounty programs and their in-scope/excluded domains |
| `AssetService` | Store, query, and relate discovered assets |
| `TaskService` | Durable task orchestration with leasing and checkpoints |
| `RateLimitService` | Distributed multi-tier rate limiting |
| `RealtimeService` | Server-sent events for live UI updates |
| `ScanOrchestratorService` | Workflow orchestration for discovery pipelines |

## Workers

| Worker | Input | Output |
|--------|-------|--------|
| Amass | Domain | Subdomain, DnsRecord |
| Subfinder | Domain | Subdomain |
| DnsResolver | Domain, Subdomain | Ip, DnsRecord |
| HttpProbe | Subdomain, Ip | Url, HttpResponse |
| HtmlDomSpider | Url, HtmlPage | Url, ApiEndpoint, JavaScriptFile |
| JsExtractor | JavaScriptFile | ApiEndpoint, Url, FindingCandidate |
| WordlistDiscovery | Url | Url |
| HeadlessSpider | Url | Url, ApiEndpoint, JavaScriptFile |
| Fingerprint | Url, HttpResponse, HtmlPage | Technology |
| AssetScoring | * | FindingCandidate |

## Event-Driven Workflow

```
AssetDiscovered (Domain)
    │
    ├──► AmassWorker ──────► SubdomainAssetDiscovered
    ├──► SubfinderWorker ──► SubdomainAssetDiscovered
    │
SubdomainAssetDiscovered ──► DnsResolverWorker ──► IpAssetDiscovered
    │                           │
    │                           ▼
    │                      HttpProbeWorker ──► UrlAssetDiscovered
    │                                              │
    │                                              ▼
    │                      HtmlDomSpider ──► (more Url, ApiEndpoint, JS)
    │                           │
    │                           ▼
    │                      JsExtractor ──► ApiEndpoint, FindingCandidate
    │                           │
    │                           ▼
    │                      FingerprintWorker ──► Technology
    │                           │
    │                           ▼
    │                      AssetScoringWorker ──► FindingCandidate
```

## Asset Types

Domain, Subdomain, Ip, Cidr, Url, HttpResponse, HtmlPage, JavaScriptFile, ApiEndpoint, Technology, FindingCandidate, Port, DnsRecord, Program, Scope, CssFile, JsonDocument

## Technology Stack

- **.NET 10** with Aspire for orchestration
- **PostgreSQL** (via EF Core) for normalized asset metadata
- **Redis** for leases, rate limits, and caching
- **RabbitMQ** for event bus messaging

## Local Development

```bash
# Prerequisites: .NET 10 SDK, Docker Desktop
dotnet run --project src/Argus.AppHost/Argus.AppHost.csproj
```

The Aspire dashboard provides service URLs, logs, traces, metrics, and health status.

## Container Deployment

```bash
cp deploy/argus.env.example deploy/argus.env
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

| Endpoint | URL |
|----------|-----|
| Web UI | http://localhost:8080 |
| API Gateway | http://localhost:8081 |
| Realtime SSE | http://localhost:8082 |

## Seed Demo Data

```bash
tools/seed-demo-data.sh
```

## Project Structure

```
src/
├── Argus.AppHost/          Aspire orchestrator
├── Argus.ApiGateway/       API gateway
├── Argus.Web/              Web UI command center
├── Argus.ServiceDefaults/  Shared telemetry, health, resilience
├── Contracts/              DTOs and event contracts
├── BuildingBlocks/
│   ├── Argus.BuildingBlocks.EventBus/   Event publishing
│   └── Argus.BuildingBlocks.Workers/   Worker host infra
├── Services/
│   ├── Argus.AssetService/
│   ├── Argus.TaskService/
│   ├── Argus.RateLimitService/
│   ├── Argus.ProgramScopeService/
│   ├── Argus.RealtimeService/
│   └── Argus.ScanOrchestratorService/
└── Workers/                (10 worker projects)
```