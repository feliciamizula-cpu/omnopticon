# MiniMax Context - Argus Recon Platform

A comprehensive guide for AI agents working on the Omnopticon project (also known as the Argus Recon Platform).

---

## 1. Project Overview

**Argus** is a distributed bug-bounty reconnaissance platform built on Microsoft .NET Aspire. It discovers, processes, classifies, and visualizes assets across authorized bug-bounty targets at scale.

**Key URLs:**
- Web UI: `http://localhost:8080` (local dev) or staging URL from CI/CD
- API Gateway: `http://localhost:8081`
- Realtime SSE: `http://localhost:8082`
- Aspire Dashboard: `http://localhost:8088`

**Tech Stack:**
- **.NET 10** with Aspire 13.2 for orchestration
- **PostgreSQL + pgvector** for normalized metadata and vector-ready enrichment
- **Redis** for leases, rate limits, and caching
- **RabbitMQ** for event bus messaging
- **YARP** for API gateway service discovery
- **Blazor** for the web UI
- **Terraform** for GCP/GKE infrastructure
- **Aspirate** for Kubernetes manifest generation

---

## 2. Architecture

### 2.1 High-Level Structure

```
┌─────────────────────────────────────────────────────────────────┐
│                         Argus.AppHost                           │
│                    (Aspire Orchestrator)                        │
└─────────────────────────────────────────────────────────────────┘
         │                 │                  │
         ▼                 ▼                  ▼
┌────────────────┐ ┌────────────────┐ ┌──────────────────────────┐
│   Argus.Web     │ │ Argus.ApiGateway│ │ Workers                  │
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

### 2.2 Core Entities

**Assets** are the fundamental entity. Every discovered host, domain, URL, endpoint, artifact, or finding is modeled as an asset with metadata, relationships, timestamps, and scoring.

**Workers** are long-running services that subscribe to asset and task events and perform reconnaissance work.

**Tasks** provide durable, resumable work tracking with leasing and checkpoints.

**Rate limiting** is distributed and tiered by program, scope, and host.

### 2.3 Asset Types

Domain, Subdomain, Ip, Cidr, Url, HttpResponse, HtmlPage, JavaScriptFile, ApiEndpoint, Technology, FindingCandidate, Port, DnsRecord, Program, Scope, CssFile, JsonDocument.

---

## 3. Project Structure

```
src/
├── Argus.AppHost/                  # Aspire orchestrator (entry point)
├── Argus.ApiGateway/               # YARP API gateway
├── Argus.Web/                      # Blazor web UI
│   ├── Pages/                      # Route pages (Agents, Assets, Operations, etc.)
│   ├── Components/                  # Reusable Blazor components (ArgusGrid, etc.)
│   ├── Layout/                      # Layout components (AppShell, SidebarNav)
│   └── wwwroot/                    # Static assets (CSS, JS)
├── Argus.ServiceDefaults/          # Shared telemetry, health, resilience
├── Contracts/                      # DTOs and event contracts
├── BuildingBlocks/
│   ├── Argus.BuildingBlocks.EventBus/
│   └── Argus.BuildingBlocks.Workers/
├── Services/                       # Backend microservices
│   ├── Argus.AgentService/         # AI agent configuration & execution
│   ├── Argus.ProgramScopeService/  # Programs, scopes, targets
│   ├── Argus.AssetService/         # Asset storage and queries
│   ├── Argus.ArtifactService/      # Scan artifacts
│   ├── Argus.FindingService/       # Vulnerability findings
│   ├── Argus.TaskService/          # Durable task orchestration
│   ├── Argus.RateLimitService/     # Distributed rate limiting
│   ├── Argus.RealtimeService/      # Server-sent events
│   ├── Argus.EventRouterService/   # Event routing
│   └── ...
└── Workers/                        # Background processing workers
    ├── Argus.Workers.Amass/         # Subdomain enumeration
    ├── Argus.Workers.Subfinder/    # Passive subdomain discovery
    ├── Argus.Workers.DnsResolver/  # DNS resolution
    ├── Argus.Workers.HttpProbe/    # HTTP probing
    ├── Argus.Workers.HtmlDomSpider/# HTML DOM crawling
    ├── Argus.Workers.HeadlessSpider/# Browser-based crawling
    ├── Argus.Workers.JsExtractor/  # JavaScript analysis
    ├── Argus.Workers.Fingerprint/   # Technology fingerprinting
    ├── Argus.Workers.AssetScoring/  # Asset risk scoring
    ├── Argus.Workers.FindingDeduper/# Finding deduplication
    └── ...
```

---

## 4. Agent System (Argus.AgentService)

The project includes an AI agent system that can autonomously perform development tasks.

### 4.1 Agent Roles

| Role | Description | Priority |
|------|-------------|----------|
| `junior_developer` | Small bug fixes, unit tests, well-scoped tasks | low |
| `developer` | Moderate feature work, bug fixes, integration tests | standard |
| `senior_developer` | Complex implementation, architecture-sensitive fixes | standard |
| `junior_system_architect` | System design support, cross-service review | standard |
| `senior_system_architect` | System design, technical strategy, security review | high |
| `junior_devops` | Infrastructure automation, health checks | low |
| `senior_devops` | Infrastructure strategy, complex deployment automation | standard |
| `devops_architect` | Infrastructure architecture, platform strategy | high |

### 4.2 Supported CLIs/Tools

- **claude** - Anthropic Claude CLI
- **codex** - OpenAI Codex
- **gemini** - Google Gemini CLI
- **opencode** - OpenCode CLI (preferred for NVIDIA models)

### 4.3 Model Routing

NVIDIA free models are used for low-priority tasks:
- `nvidia/minimaxai/minimax-m2.7` - Junior developer, low priority
- `nvidia/qwen/qwen3-coder-480b-a35b-instruct` - DevOps, infrastructure
- `nvidia/mistralai/mistral-large-3-675b-instruct-2512` - Senior DevOps

Premium models for high-priority/architect work:
- `claude-opus-4-7` / `claude-sonnet-4-6` - Senior System Architect
- `gpt-5.5` - Senior Developer, Senior System Architect, DevOps Architect

### 4.4 Agent Configuration Storage

Agents are stored in **PostgreSQL** via `AgentDbContext`. The `AgentDevelopmentSeedData` class seeds agents on first startup.

**Key Entity: `AgentRecord`**
```csharp
public sealed class AgentRecord
{
    public Guid AgentId { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }              // e.g., "junior_developer"
    public string? RoleDescription { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; }              // "active" or "disabled"
    public string WorkStatus { get; set; }          // "idle" or "working"
    public string Tool { get; set; }                 // "claude", "codex", "opencode", etc.
    public string Model { get; set; }               // Full model identifier
    public string? Provider { get; set; }           // "NVIDIA", "Claude", "OpenAI", etc.
    public string Priority { get; set; }            // "low", "standard", "high"
    public string ResponsibilitiesJson { get; set; } // JSON array of responsibilities
    // ... timestamps and tracking fields
}
```

### 4.5 Agent Task Records

Tasks for agents are stored in `AgentTaskRecord` with fields like:
- `TaskId`, `Description`, `Priority` (critical/high/normal/medium/low), `Status`
- `AssignedTo` (agent GUID), `TargetRole`, `TaskType`, `ScheduleExpression`
- `Attempts`, `RecoveryContext`, timestamps

### 4.6 Agent Selection

The `AgentSelectionService` selects agents by:
1. Filtering by `Role` (exact match)
2. Filtering by `Status` = "active"
3. Ordering by `SortOrder` ascending
4. Returning first available agent with configured tool/model

### 4.7 Agent UI

The web UI at `/agents` displays all agent configurations with:
- Table columns: Name, Role, CLI, Provider, Model, Priority
- Inspector panel showing full configuration
- Create/Edit modal for agent configuration
- Enable/Disable controls

---

## 5. Event-Driven Workflow

```
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

---

## 6. Database & Persistence

### 6.1 AgentService Database (PostgreSQL)

`AgentDbContext` manages:
- `Agents` - AgentRecord table
- `AgentTasks` - AgentTaskRecord table
- `ChatMessages` - Agent conversation history
- `ProviderAccounts` - AI provider configuration
- `ProviderUsageSnapshots` - Usage tracking
- `CodeReviews` - Generated code reviews
- `SystemReports` - System health reports

### 6.2 Other Services

Each service connects to PostgreSQL via Entity Framework Core. Infrastructure services use:
- **Redis** - Rate limits, leases, caching
- **RabbitMQ** - Event bus (using `ArgusOutbox` pattern for reliable messaging)

---

## 7. Frontend Architecture

### 7.1 Tech Stack

- **Blazor WebAssembly** with InteractiveServer rendering
- **Custom components**: ArgusGrid (virtualized grid), ArgusGridColumn
- **Real-time updates**: DevelopmentRealtimeClient for SSE

### 7.2 Key Pages

| Page | Route | Purpose |
|------|-------|---------|
| Agents | `/agents` | AI agent configuration management |
| AgentTasks | `/agent-tasks` | Agent task queue and assignment |
| AgentSchedules | `/agent-schedules` | Scheduled/recurring agent tasks |
| Operations | `/operations` | Program/asset management, spidering, enumeration |
| Assets | `/assets` | Asset browsing and search |
| Environments | `/environments` | Environment configuration |
| ProviderUsage | `/provider-usage` | AI provider usage monitoring |
| CodeReviews | `/code-reviews` | Code review outputs |
| SystemReports | `/system-reports` | System health reports |
| Todos | `/todos` | TODO items from agents |

### 7.3 API Integration

Frontend uses `HttpClient` to communicate with backend via `/ui/*` endpoints (gateway-routed to appropriate services).

---

## 8. CI/CD & Deployment

### 8.1 GitHub Actions Workflow (`.github/workflows/cd-gcp.yml`)

Pushes to `main` trigger automatic deployment:

1. **Provision** - Terraform creates GKE cluster, node pools, namespace, KEDA
2. **Build** - 28 container images built in parallel and pushed to Artifact Registry
3. **Deploy** - Aspirate generates K8s manifests, Terraform applies workloads

### 8.2 Infrastructure (Terraform)

Location: `deploy/terraform/`

Key resources:
- GKE cluster with workload identity
- Artifact Registry repository
- Service accounts with IAM roles
- Kubernetes namespace `argus`
- KEDA for worker autoscaling
- HPAs for continuous workers

### 8.3 Aspirate

Generates Kubernetes manifests from Aspire's AppHost topology:
```bash
cd src/Argus.AppHost
aspirate generate --non-interactive --disable-secrets
```

Output: `src/Argus.AppHost/aspirate-output/`

### 8.4 Required GitHub Secrets

- `WIF_PROVIDER` - Workload Identity Federation provider
- `WIF_SERVICE_ACCOUNT` - Service account email for WIF

---

## 9. Development Workflow

### 9.1 Local Setup

```bash
# Prerequisites: .NET SDK from global.json and Docker Desktop
dotnet restore eShop.slnx
dotnet build eShop.slnx
dotnet run --project src/Argus.AppHost/Argus.AppHost.csproj
```

### 9.2 Multi-Agent Collaboration

When multiple AI agents work simultaneously:

1. **File Locking** - Use `.agents/locks/{agent-id}_{filename}.lock` before editing
2. **Activity Logging** - Append to `ai_logs.txt` with format:
   ```
   [TIMESTAMP] | AGENT_ID: {id} | TASK: {task} | STATUS: {status} | MESSAGE: {msg}
   ```
3. **Lock Timeout** - Locks older than 30 minutes are stale

See `AI_START_HERE.md` for full collaboration protocol.

### 9.3 Local Docker Deployment

```bash
cp deploy/argus.env.example deploy/argus.env
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

---

## 10. Key Technologies & Dependencies

### 10.1 .NET SDK

Version pinned in `global.json`: **.NET 10.0.100**

### 10.2 Notable NuGet Packages

- **Aspire** - Distributed application orchestration
- **Npgsql.EntityFrameworkCore.PostgreSQL** - PostgreSQL with EF Core
- **StackExchange.Redis** - Redis client
- **RabbitMQ.Client** - RabbitMQ integration
- **YamlDotNet** - YAML parsing for K8s manifests
- **Playwright** - Browser automation for tests

### 10.3 External Tools

- **Amass** - Subdomain enumeration
- **Subfinder** - Passive subdomain discovery
- **KEDA** - Kubernetes Event-Driven Autoscaling

---

## 11. Important File Locations

| Purpose | Path |
|---------|------|
| Agent seed data | `src/Services/Argus.AgentService/Stores/AgentDevelopmentSeedData.cs` |
| Agent DbContext | `src/Services/Argus.AgentService/Data/AgentDbContext.cs` |
| Agent contracts (DTOs) | `src/Contracts/Argus.Contracts/Agents/AgentContracts.cs` |
| ArgusGrid component | `src/Argus.Web/Components/ArgusGrid.razor` |
| Operations page | `src/Argus.Web/Pages/Operations.razor` |
| Deployment CSS | `src/Argus.Web/wwwroot/css/development.css` |
| Terraform configs | `deploy/terraform/` |
| Container Dockerfile | `deploy/Dockerfile.service` |
| Aspire AppHost | `src/Argus.AppHost/Program.cs` |
| GitHub Actions workflow | `.github/workflows/cd-gcp.yml` |
| AI collaboration protocol | `AI_START_HERE.md` |

---

## 12. Current Priority Field

Agents have a **Priority** field with three values:
- `low` - For junior agents handling simple tasks (uses NVIDIA free models)
- `standard` - For regular development tasks
- `high` - For senior system architects and critical tasks (uses Claude Opus, GPT-5.5)

The Priority column is displayed in the Agents UI table and inspector panel, and can be set when creating/editing agent configurations.

---

## 13. Common Patterns

### 13.1 Adding a New Service

1. Create project in `src/Services/Argus.ServiceName/`
2. Add to `src/Argus.AppHost/Program.cs` with `builder.AddProject<Projects.Argus_ServiceName>()`
3. Add to CI/CD matrix in `.github/workflows/cd-gcp.yml`
4. Add connection strings to `deploy/argus.env`

### 13.2 Adding a New Worker

1. Create project in `src/Workers/Argus.Workers.WorkerName/`
2. Register in AppHost with `.PublishAsArgusImage()`
3. Add to CI/CD build matrix
4. Configure subscriptions in `ScanOrchestratorService`

### 13.3 Database Migrations

```bash
cd src/Services/Argus.AgentService
dotnet ef migrations add AddNewField --context AgentDbContext
```

Migration files live in `src/Services/Argus.AgentService/Migrations/`.

---

*Last updated: 2026-05-26*