# AI Agent Context Document: Argus Recon Platform & AutoCollab

**File:** `mistral_context.md`

---

## **1. Project Overview**
### **Purpose**
- **Argus Recon Platform** (`omnopticon`): Distributed recon and asset discovery system for attack surface monitoring, event-driven automation, and AI-driven task execution.
- **AutoCollab**: Multi-agent collaboration framework using AutoGen to simulate AI teams (e.g., Developer, QA, Security) for improving Argus.

### **Tech Stack**
| Category               | Technologies                                                                                     |
|------------------------|--------------------------------------------------------------------------------------------------|
| **Languages**          | C# (.NET 8), Python, TypeScript/JavaScript                                                      |
| **Frameworks**         | ASP.NET Core, Blazor, Entity Framework Core, AutoGen, Playwright                                |
| **Databases**          | PostgreSQL, Redis                                                                               |
| **Messaging**          | RabbitMQ (integration events), SignalR (real-time updates)                                     |
| **Infra & DevOps**     | Docker, Kubernetes, Aspire, GitHub Actions                                                      |
| **Observability**      | OpenTelemetry (metrics, traces, logs)                                                           |
| **AI/ML**              | AutoGen, NVIDIA API (LLM inference), Custom AI agents                                           |
| **Testing**            | xUnit, Playwright, pytest                                                                        |

### **Entry Points**
| Project                     | Entry Point                                                                 | Description                                                                                     |
|----------------------------|-----------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------|
| **Argus.Web**             | `src/Argus.Web/Program.cs`                                                                    | Blazor UI for asset tracking, task management, and real-time updates.                         |
| **Argus.AgentService**    | `src/Services/Argus.AgentService/Program.cs`                                                   | AI agent task execution, provider usage tracking, and code review.                            |
| **Argus.TaskService**     | `src/Services/Argus.TaskService/Program.cs`                                                    | Task scheduling, worker orchestration, and event-driven workflows.                            |
| **AutoCollab**            | `autocollab/ai_team.py`                                                                        | Multi-agent collaboration for team-based AI workflows.                                         |

---

## **2. Core Components**
### **A. Argus.Web (Blazor UI)**
- **Key Features**: Asset relationship visualization, AI task assignment, GitHub Actions integration, real-time updates via SignalR.
- **Critical Files**:
  - `Program.cs`: Proxy endpoints for agent/task APIs.
  - `Components/`: Blazor UI components.

### **B. Argus.AgentService (AI Agents)**
- **Key Modules**:
  1. **Task Execution**: Selects agents, executes tasks using LLMs (e.g., Nenotron-3), records results.
  2. **Provider Usage**: Tracks LLM API usage (NVIDIA, OpenRouter, Claude) with rate limiting.
  3. **Agent Store**: PostgreSQL-backed persistence for agents, tasks, and chat history.
- **Critical Files**:
  - `Agents/TaskExecutionService.cs`: Core task execution logic.
  - `ProviderUsage/`: LLM usage tracking adapters.

### **C. Argus.TaskService (Task Orchestration)**
- **Key Features**: Task creation, deduplication, priority-based scheduling, integration with ephemeral workers.
- **Critical Files**:
  - `Program.cs`: Task endpoints.
  - `BuildingBlocks/Argus.BuildingBlocks.Workers/`: Worker interfaces.

### **D. Ephemeral Workers (Recon Tasks)**
- **Examples**: `Argus.Workers.Http` (HTTP probing), `Argus.Workers.DnsResolver` (DNS resolution), `Argus.Workers.AssetScoring` (asset prioritization).
- **Key Files**:
  - `Workers/Argus.Workers.Http/HttpWorker.cs`: Worker implementation.
  - `BuildingBlocks/Argus.BuildingBlocks.EventDrivenWorkers/EphemeralWorkerDispatcher.cs`: Dispatch logic.

### **E. AutoCollab (Multi-Agent Collaboration)**
- **Key Features**: Agents with distinct roles (Architect, Developer, QA), round-robin group chat, NVIDIA API integration.
- **Critical Files**:
  - `ai_team.py`: Agent definitions and chat logic.

---

## **3. Key Workflows**
### **A. API Request Lifecycle**
1. **Request Ingestion**: UI sends requests to backend via proxy endpoints (`Program.cs` lines 419-449).
2. **Task Creation**: `Argus.TaskService` creates tasks and publishes `TaskRequested` events.
3. **Agent Selection**: `Argus.AgentService` selects an agent based on role and provider capacity.
4. **Task Execution**: Agent executes the task using LLM; results are stored and artifacts generated.
5. **Event Propagation**: Results trigger `AssetDiscovered`/`AssetConfirmed` events.
6. **UI Update**: SignalR pushes updates to the Blazor UI.

### **B. Scan Plan Execution**
1. User creates a scan plan (e.g., domain discovery).
2. Scan plan generates tasks for ephemeral workers (e.g., `HttpWorker`, `DnsResolver`).
3. Workers discover assets and publish events.
4. `Argus.AssetService` stores assets and relationships.
5. `Argus.Workers.AssetScoring` assigns confidence scores and tags.

---

## **4. State Management**
### **Databases**
| Database      | Purpose                                   | Key Tables                                                                                     |
|---------------|-------------------------------------------|-------------------------------------------------------------------------------------------------|
| PostgreSQL    | Primary storage                           | `AgentRecord`, `AgentTaskRecord`, `ProviderUsageSnapshotRecord`                                |
| Redis         | Caching and rate limiting                 | `argus:rate-limit:bucket:{key}`, `argus:web:*`                                                 |

### **In-Memory Stores**
- **Outbox/Inbox Pattern**: Ensures reliable event delivery (`EfCoreOutboxStore.cs`).
- **Ephemeral Worker State**: Tracks worker instances and results (`EphemeralWorkerDispatcher.cs`).

---

## **5. Error Handling**
| Error Type               | Handling Strategy                                  | Example                                                                                       |
|--------------------------|---------------------------------------------------|-------------------------------------------------------------------------------------------------|
| Task Execution Failure   | Retry or mark as `failed`; update `LastError`     | `TaskExecutionService.cs` lines 78-91                                                         |
| Provider Rate Limits     | Delay task execution; retry later                 | `TokenBucketRateLimiter.cs` lines 258-262                                                     |
| Integration Failures     | Publish to outbox; poison message queue           | `EfCoreOutboxStore.cs` lines 82-89                                                            |

---

## **6. Testing**
- **Frameworks**: xUnit (C#), pytest (Python), Playwright (UI).
- **Key Commands**:
  ```bash
  dotnet test omnopticon/tests/Argus.ApiGateway.Tests/
  cd autocollab && python -m pytest test_groupchat.py
  ```

---

## **7. Conventions**
### **Naming**
- Services: `Argus.{ServiceName}Service` (e.g., `Argus.AgentService`).
- Workers: `Argus.Workers.{Capability}` (e.g., `Argus.Workers.Http`).

### **Code Style**
- **C#**: Async/await, LINQ, dependency injection.
- **Python**: Type hints, async/await.

### **Patterns**
| Pattern                | Usage                                  | Example                                                                                       |
|-----------------------|----------------------------------------|-------------------------------------------------------------------------------------------------|
| Outbox Pattern        | Reliable event delivery               | `EfCoreOutboxStore.cs`                                                                        |
| Ephemeral Workers     | Short-lived recon tasks               | `EphemeralWorkerDispatcher.cs`                                                                |
| Token Bucket RL       | Rate limiting                          | `TokenBucketRateLimiter.cs`                                                                    |

---

## **8. Key Dependencies**
| Dependency       | Purpose                                   | Integration Notes                                                                             |
|-------------------|-------------------------------------------|-------------------------------------------------------------------------------------------------|
| NVIDIA API        | LLM inference                            | Configure via `NVIDIA_API_KEY` in `ai_team.py`.                                               |
| Redis            | Caching and rate limiting                | Connection string in `appsettings.json`.                                                       |
| PostgreSQL        | Primary data storage                     | EF Core migrations (e.g., `20260525000000_AddAgentPriority.cs`).                              |

---

## **9. Antipatterns**
| Antipattern            | Risk                                      | Mitigation                                                                                     |
|-----------------------|-------------------------------------------|-------------------------------------------------------------------------------------------------|
| Hardcoded Secrets     | Security risk                            | Use `dotnet user-secrets` or environment variables.                                           |
| Raw SQL Queries       | SQL injection                          | Use parameterized queries (e.g., `FromSqlRaw` with `{0}` placeholders).                      |
| Synchronous Code      | Blocking I/O calls                      | Always use `await` with async methods.                                                        |

---

## **10. Critical Files**
| File                                                                 | Lines   | Description                                                                                     |
|---------------------------------------------------------------------|---------|-------------------------------------------------------------------------------------------------|
| `src/Argus.Web/Program.cs`                                            | 419-449 | Proxy endpoints for `Argus.AgentService`.                                                      |
| `src/Services/Argus.AgentService/Agents/TaskExecutionService.cs`    | 61-76   | LLM chat client invocation (core AI logic).                                                     |
| `src/BuildingBlocks/Argus.BuildingBlocks.RateLimiting/TokenBucketRateLimiter.cs` | 258-262 | Rate limiting script.                                                                        |
| `autocollab/ai_team.py`                                              | 47-130  | Agent definitions and collaboration logic.                                                     