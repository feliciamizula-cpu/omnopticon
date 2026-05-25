# Omnopticon Design Document

## 1. Detailed Current State Analysis

### Architectural Pain Points

#### Worker Modularity
- Workers (`Argus.Workers.*`) are simulated adapters with inconsistent concurrency models.
- `ArgusWorkerBackgroundService` lacks bounded concurrency enforcement despite `MaxConcurrency` declaration.
- Worker priority lanes (`WorkerPriority` enum) are unused in task routing (`EfTaskStore.LeaseTaskAsync`).

#### Separation of Concerns
- **RateLimitService** lacks integration with `ProgramScopeService` policies (hardcoded buckets).
- **ScanOrchestratorService** processes scans sequentially (`foreach` in `ProcessScheduledScanAsync`), delaying dependent tasks.
- **TaskService** mixes task lifecycle logic with SQL string interpolation (`assetTypeCondition` parameter construction).

#### Code References
- **`RunCommandAsync` bottleneck**: Fire-and-forget pattern in `ArgusWorkerBackgroundService.cs:37` causes semaphore leaks.
- **`AssetScoring` worker**: Hardcoded intervals in `ComputeStalenessScore` (`Program.cs:728-740`) with no validation for unmapped `AssetType`s.
- **Duplicate imports**: `RabbitMqConsumerService` requeues poison messages without consistent retry policies (`lines 110-130`).

---

### Performance Bottlenecks

| Bottleneck                          | File:Line                                      | Impact                                  |
|-------------------------------------|------------------------------------------------|-----------------------------------------|
| `CronExpression.GetNextOccurrence`  | `ScanOrchestratorService/Program.cs:176-177`   | Silent null returns cause infinite loops. |
| `ComputeStalenessScore`             | `AssetService/Program.cs:758`                 | CPU overhead on every `ToDto()` call.   |
| Sequential scan processing          | `ScanOrchestratorService/Program.cs:152-156`  | Delays downstream tasks by 1+ minute.   |
| Hardcoded `pageSize=10`             | `ScanOrchestratorService/Program.cs:176`      | Limits task creation rate.              |

---

### Technical Debt

#### Unreliable Scripts
- **`review-agents.sh:174-188`**: Unchecked `PIPESTATUS` after `opencode` execution may hang reviewers.
- **`agent-coord.sh:361-429`**: Lock held during entire reconciliation iteration blocks concurrent operations.
- **`auto-run-agents.sh:507-527`**: Heartbeat subprocess PID tracking race conditions.

#### Duplicate Logic
- **Worker helpers**: Payload parsing and URL normalization duplicated across workers.
- **Task leasing**: SQL queries in `EfTaskStore` repeat `WorkerCapability` filtering logic.

#### Dead Code
- **`TaskContracts.cs:63-67`**: `SubscribedAssetTypes` unused despite IReadOnlyCollection declaration.
- **`dashboard.sh:170-184`**: `CHECKING` status falsely flags as critical alert.

---

## 2. Key Improvement Areas

### Architecture

#### Proposed Changes
- **Split `AssetScoring` worker** into microservices:
  - **ScoringService**: Compute `StalenessScore`/`RiskScore` on asset events.
  - **ArtifactService**: Store raw tool outputs (HTTP bodies, screenshots).
- **Event-driven ScanOrchestrator**: React to `AssetDiscovered` events via RabbitMQ (current: polling).
- **Worker priority lanes**: Enforce `High/Normal/Low` in `EfTaskStore.LeaseTaskAsync`.

#### Ownership
| Component                  | Owner                | Responsibility                          |
|----------------------------|----------------------|-----------------------------------------|
| Worker host concurrency    | Backend Team         | Bounded `SemaphoreSlim` implementation. |
| Task lifecycle             | Core Team            | `Lease` → `Start` → `Complete` state.   |
| Event bus                  | Infra Team           | RabbitMQ dead-letter/retry policies.    |

---

### Performance

#### Optimizations
| Issue                          | Solution                                  | Metric Goal                |
|--------------------------------|-------------------------------------------|----------------------------|
| `RunCommandAsync` sync IO      | Replace with async file ops               | Reduce task latency by 30%.|
| Serial scan processing         | `Task.WhenAll` with bounded parallelism   | Cut scan time by 50%.      |
| `ComputeStalenessScore` calls  | Cache scores in `AssetRecord`             | Lower CPU usage by 20%.    |
| Hardcoded `pageSize=10`        | Configurable via `appsettings.json`       | Enable 100+ tasks/min.     |

#### Heavy Dependencies
- **NuGet**: `Microsoft.Extensions.Hosting` v8.0.0 (update to 9.0.0).
- **Vendored JS**: `jq`/`bash` scripts with atomicity gaps (add file locks).
- **Unused**: `WorkerPriority` enum comparisons (remove or enforce).

---

### Resource Usage

#### Action Items
- **PostgreSQL**: Add GIN indexing for `AssetRecord` full-text search.
- **Redis**: Pre-load `RateLimitService` policies on startup.
- **Docker**: Optimize `compose.yaml` to reduce image size by 20%.

---

### Complexity

#### Refactoring Tasks
| Target File                     | Action                                      |
|---------------------------------|---------------------------------------------|
| `Program.cs`                    | Extract route handlers into `Controllers/`. |
| `ArgusWorkerBackgroundService.cs`| Centralize worker helpers into `WorkerHost`.|
| `TaskContracts.cs`              | Remove unused `SubscribedAssetTypes`.       |

---

## 3. Actionable TODO Tasks

### P1 Tasks
1. **Fix `EfCoreOutboxStore.cs` conflict markers** (`src/BuildingBlocks/Argus.BuildingBlocks.EventBus/EfCoreOutboxStore.cs:9-19`)
   - **Owner**: Backend Team
   - **Steps**: Resolve `<<<<<<< HEAD`/`>>>>>>>` sections; verify compile.

2. **Enforce `MaxConcurrency` in workers** (`src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusWorkerBackgroundService.cs:26`)
   - **Owner**: Core Team
   - **Steps**: Add `SemaphoreSlim`; update `RunTaskWithReleaseAsync`.
   - **Verify**: Integration test with `MaxConcurrency=5`.

3. **Fix `CronExpression.GetNextOccurrence` null handling** (`ScanOrchestratorService/Program.cs:165,196`)
   - **Owner**: Infra Team
   - **Steps**: Add null checks; log invalid cron expressions.

---

### P2 Tasks
4. **Parallelize scans** (`ScanOrchestratorService/Program.cs:152-156`)
   - **Owner**: Backend Team
   - **Steps**: Replace `foreach` with `Task.WhenAll`; add concurrency limit.

5. **Cache `StalenessScore`** (`AssetService/Program.cs:758`)
   - **Owner**: Core Team
   - **Steps**: Compute score on write; add `LastUpdatedAt` field.

6. **Add artifact storage**
   - **Owner**: Storage Team
   - **Steps**: Implement `IArtifactStore`; integrate with `AssetService`.

---

### P3 Tasks
7. **Remove `SubscribedAssetTypes` stub** (`TaskContracts.cs:63-67`)
   - **Owner**: Cleanup Team
   - **Steps**: Delete unused IReadOnlyCollection.

8. **Optimize Docker image**
   - **Owner**: DevOps
   - **Steps**: Multi-stage build; trim unused dependencies.

---

## 4. Verification and Testing

### Metrics
| Goal                                  | Tool                | Baseline | Target |
|---------------------------------------|---------------------|----------|--------|
| Task processing latency               | `dotnet-counters`   | 10s      | 7s     |
| Memory usage                          | Docker stats       | 2.5GB    | 2.0GB  |
| Scan throughput                       | Custom metric       | 10/min   | 20/min |
| Asset query response time             | Playwright          | 500ms    | 200ms  |

### Tools
- **Unit Tests**: xUnit (`tests/Argus.*` projects).
- **Integration**: Docker Compose (`deploy/compose.yaml`).
- **UI**: Playwright (`tests/Argus.Web.Tests`).

---

## 5. Risks and Mitigations

| Risk                                      | Mitigation                                  |
|-------------------------------------------|---------------------------------------------|
| Breaking changes to `TaskContracts`       | Semantic versioning; deprecation warnings.  |
| RabbitMQ message replay duplication       | Idempotency keys in `InboxMessageRecord`.    |
| Agent coordination drift                  | Enable `AGENT_COORD_AUTOCOMMIT=1`.          |
| Rate limit quota exhaustion               | Dynamic quota updates via `ProgramScopeService`. |

---

**Approval**: @tech-lead, @infra-lead