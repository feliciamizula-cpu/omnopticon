# Argus Recon Platform Implementation Handoff

This document is intended for a coding agent that will continue implementing Argus Recon Platform from the current repository state. It summarizes what exists, what remains, and how to complete the application against the original product specification.

## Current Repository State

The repository has been converted from the eShop foundation into an Argus-focused Aspire solution.

Current major projects:

```text
src/
  Argus.AppHost/
  Argus.ApiGateway/
  Argus.ServiceDefaults/
  Argus.Web/

  BuildingBlocks/
    Argus.BuildingBlocks.EventBus/
    Argus.BuildingBlocks.Workers/

  Contracts/
    Argus.Contracts/

  Services/
    Argus.ProgramScopeService/
    Argus.AssetService/
    Argus.TaskService/
    Argus.RateLimitService/
    Argus.ScanOrchestratorService/
    Argus.RealtimeService/

  Workers/
    Argus.Workers.Amass/
    Argus.Workers.Subfinder/
    Argus.Workers.DnsResolver/
    Argus.Workers.HttpProbe/
    Argus.Workers.HtmlDomSpider/
    Argus.Workers.JsExtractor/
    Argus.Workers.WordlistDiscovery/
    Argus.Workers.HeadlessSpider/
    Argus.Workers.Fingerprint/
    Argus.Workers.AssetScoring/
```

Recent committed milestones:

```text
ecf03d1 Advance Argus deployment and operations workflow
30e86f0 Add demo seed data workflow and UI row inspector
f9bfdd7 Show asset relationships in UI selection rail
c6ae85e Remove obsolete eShop projects
a801fb3 Add durable outbox dispatch for core events
```

Known uncommitted work at the time this document was created:

```text
README.md is modified by another agent/user. Do not overwrite or revert it unless explicitly asked.
```

## Hard Rules For Future Agents

1. Do not reintroduce eShop commerce projects.
2. Do not revert user changes or unrelated dirty work.
3. Preserve scoped bug-bounty safety boundaries:
   - no unbounded recursive crawling
   - all HTTP-capable workers must call `RateLimitService` first
   - workers must validate scope before publishing produced assets
4. Prefer existing Argus patterns before adding new abstractions.
5. Keep implementation incremental and verifiable. Build after each meaningful slice.
6. Commit completed work to `main` in focused commits.

## Verification Commands

Use these after each major change:

```bash
dotnet build src/Argus.AppHost/Argus.AppHost.csproj
docker compose --env-file deploy/argus.env.example -f deploy/compose.yaml config --quiet
```

For local container verification:

```bash
docker compose --env-file deploy/argus.env.example -f deploy/compose.yaml up --build -d
tools/seed-demo-data.sh
curl -fsS http://localhost:8080/ui/state | jq
```

Expected local URLs:

```text
Argus Web:       http://localhost:8080
API Gateway:     http://localhost:8081
Realtime Service http://localhost:8082
```

## Phase Status

### Phase 1 - Fork eShop Foundation

Status: mostly complete.

Completed:

- Argus AppHost exists and orchestrates local services/workers.
- Argus ServiceDefaults exists for telemetry, health, resilience, and service discovery.
- eShop projects were removed.
- Docker Compose deployment exists in `deploy/`.
- Demo data seeding exists in `tools/seed-demo-data.sh`.

Outstanding:

- Add first-class automated tests for deployment and health checks.
- Confirm all docs consistently refer to Argus-only project names.
- Add CI pipeline for build, compose config validation, formatting, and smoke tests.

Implementation instructions:

1. Add `.github/workflows/ci.yml`.
2. CI should run:
   - `dotnet restore`
   - `dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release`
   - `docker compose --env-file deploy/argus.env.example -f deploy/compose.yaml config --quiet`
3. If test projects are added, include `dotnet test`.
4. Do not depend on live external recon tools in CI.

Acceptance criteria:

- CI passes on a clean checkout.
- `src/` remains Argus-only.
- Compose config validates without secrets beyond `deploy/argus.env.example`.

## Phase 2 - Core Domain Services

Status: MVP service shape exists, but domain depth is incomplete.

### 2.1 ProgramScopeService

Status: complete.

Completed:

- Program CRUD basics.
- Scope creation.
- Scope validation endpoint.
- PostgreSQL/in-memory backing.
- Outbox integration for program/scope events.
- Rule revision history (`program_rule_revisions` table, `GET /programs/{programId}/rules/revisions`).
- Scope exclusions as first-class records (`scope_exclusions` table, `POST /programs/{programId}/exclusions`).
- Rate-limit policy ownership per program/scope (`rate_limit_policies` table, `GET/POST /programs/{programId}/rate-limit-policies`).
- Batch scope validation (`POST /scope-validation/check-batch`).

Remaining (lower priority):

- Scope ingestion from HackerOne/Bugcrowd/custom sources.
- Signed scope snapshots for workers.
- More precise CIDR/URL-pattern validation.
- `ProgramScopeChanged`, `ProgramRuleRevisionCreated`, `ScopeSnapshotPublished` events.
- Scope revision history write path (currently read-only).

### 2.2 AssetService

Completed:

- Asset upsert with natural key.
- Asset query with paging/filtering basics.
- Asset relationships.
- PostgreSQL/in-memory backing.
- Outbox integration for asset events.
- UI relationship proxy exists.

Outstanding:

- Full asset table set from original spec:
  - `asset_types`
  - `asset_observations`
  - `asset_metadata`
  - `asset_artifacts`
  - `asset_tags`
  - `asset_scores`
  - `asset_status`
- Rich server-side sorting.
- Rich metadata/tag search.
- Artifact references to object storage.
- Better relationship dedupe.
- Bulk tagging and bulk task enqueue API support.
- Asset status transitions and out-of-scope marking.
- Search vector/GIN indexing.

Implementation instructions:

1. Extend `AssetDbContext` carefully. The current code uses `EnsureCreated`; either keep idempotent SQL initializers for new tables or introduce real EF migrations for all services.
2. Add API endpoints:
   - `PATCH /assets/{assetId}/status`
   - `POST /assets/{assetId}/tags`
   - `DELETE /assets/{assetId}/tags/{tag}`
   - `POST /assets/bulk/tag`
   - `POST /assets/bulk/enqueue`
   - `POST /assets/{assetId}/artifacts`
   - `GET /assets/{assetId}/artifacts`
   - `GET /assets/{assetId}/observations`
3. Add query parameters to `GET /assets`:
   - `sort`
   - `direction`
   - `tag`
   - `minInterestingScore`
   - `minRiskScore`
   - metadata filter support
4. Add unique constraints for edges:
   - `from_asset_id`
   - `to_asset_id`
   - `edge_type`
   - nullable `discovered_by_task_id` if useful
5. When a worker creates a child asset:
   - validate scope first
   - upsert child asset
   - create relationship from parent/input asset
   - store artifact reference if applicable
   - commit
   - publish outbox event

Acceptance criteria:

- Duplicate assets and duplicate relationships are idempotent.
- UI can request asset details, relationships, observations, artifacts, tags, and scores.
- Asset list supports server-side sort/filter/paging.
- Raw bodies/screenshots/tool outputs are not stored in hot asset rows.

### 2.3 TaskService

Completed:

- Durable task model with states.
- Task lease/start/progress/complete/fail endpoints.
- PostgreSQL/in-memory backing.
- Outbox integration for task events.

Outstanding:

- Retry state machine with backoff.
- Heartbeat lost detection.
- Expired lease recovery.
- Cancellation endpoint.
- Partial success retry policy.
- Checkpoint resume helpers.
- Task dedupe.
- Task dependency links.

Implementation instructions:

1. Add a background maintenance service inside TaskService.
2. Maintenance loop should:
   - find `Leased` or `Running` tasks with expired leases
   - mark as `HeartbeatLost` or `RetryPending`
   - increment attempt when requeued
   - mark as `Expired` when max attempts or TTL is exceeded
3. Add endpoints:
   - `POST /tasks/{taskId}/cancel`
   - `POST /tasks/{taskId}/retry`
   - `POST /tasks/{taskId}/heartbeat`
   - `GET /tasks/{taskId}/history`
4. Add task history table:
   - state
   - timestamp
   - worker id
   - message
   - checkpoint summary
5. Add dedupe keys to task creation:
   - `program_id`
   - `scope_id`
   - `task_type`
   - `input_asset_id`
   - normalized payload hash

Acceptance criteria:

- Killing a worker while it runs a task eventually requeues or fails the task according to policy.
- Retried tasks preserve checkpoint JSON.
- Duplicate task requests do not create runaway duplicate work.
- UI can distinguish queued, running, failed, retry pending, and expired tasks.

### 2.4 RateLimitService

Completed:

- Redis-backed and in-memory token buckets.
- Bucket dimensions for program/scope/host/domain/ip/worker/proxy.
- Rate-limit events.

Outstanding:

- Persisted program/scope-specific policies from ProgramScopeService.
- Dynamic quota updates.
- Token lease audit trail.
- Wait queue semantics for delayed requests.
- Metrics for pressure and wait time.

Implementation instructions:

1. Add a policy read path:
   - either call ProgramScopeService
   - or consume `RateLimitPolicyChanged` events into local cache
2. Add `GET /rate-limits/policies`.
3. Add `PUT /rate-limits/policies/{bucketKey}` for admin/dev usage.
4. Record token grants/delays into short-lived Redis history and optional PostgreSQL audit table.
5. Return structured delay data that workers can use to requeue tasks gracefully.

Acceptance criteria:

- Different programs/scopes can have different capacities.
- Workers do not busy-loop when delayed.
- UI shows bucket utilization, delayed requests, and policy source.

### 2.5 ScanOrchestratorService

Completed:

- Domain discovery plan creation.
- Initial task seeding.
- PostgreSQL/in-memory backing.

Outstanding:

- Explicit workflow state machine.
- Event consumer support.
- Asset-driven task planning.
- Stuck workflow recovery.
- Duplicate work avoidance across workflow steps.

Implementation instructions:

1. Add workflow records:
   - `workflow_instances`
   - `workflow_steps`
   - `workflow_transitions`
2. Implement domain discovery state machine:
   - ProgramAdded
   - ScopeImported
   - DomainAssetCreated
   - SubdomainEnumerationRequested
   - SubdomainAssetCreated
   - HttpProbeRequested
   - UrlAssetCreated
   - HtmlSpiderRequested
   - JsExtractorRequested
   - ApiEndpointAssetCreated
3. Add event consumers for:
   - `ProgramCreated`
   - `ScopeCreated`
   - `AssetDiscovered`
   - `TaskCompleted`
   - `TaskFailed`
4. On asset events, decide next task types based on asset type/subtype and existing task dedupe.
5. Add endpoints:
   - `GET /scan-plans/{id}`
   - `GET /scan-plans/{id}/timeline`
   - `POST /scan-plans/{id}/pause`
   - `POST /scan-plans/{id}/resume`

Acceptance criteria:

- Creating a program and domain scope can drive the first end-to-end scan without manual task creation.
- Workflow progress is visible in UI.
- Replaying an event does not duplicate work.

## Phase 3 - Event Bus, Outbox, Inbox

Status: publisher/outbox path exists for core EF services; consumer/inbox path remains incomplete.

Completed:

- `IntegrationEventEnvelope<T>`.
- Event contracts for MVP events.
- Realtime publisher.
- RabbitMQ publisher.
- Composite publisher.
- EF Core outbox store.
- Durable publisher that queues events.
- Background outbox dispatcher.
- Idempotent `outbox_messages` table initializer.
- ProgramScope, Asset, and Task services use durable outbox when EF is enabled.

Outstanding:

- RabbitMQ consumers.
- Inbox/idempotent consumer tracking.
- Dead-letter/retry policy for consumers.
- Event type registry.
- Contract versioning.
- Correlation/causation propagation from incoming HTTP requests and task execution.
- Outbox support for RateLimitService and ScanOrchestratorService where appropriate.

Implementation instructions:

1. Add `IIntegrationEventHandler<T>` or extend existing `IIntegrationEventConsumer<T>`.
2. Add RabbitMQ subscription host:
   - declares durable queues per service
   - binds event types/routing keys
   - deserializes `IntegrationEventEnvelope<JsonElement>`
   - resolves typed handlers
3. Implement inbox table:
   - event id
   - event type
   - consumer name
   - received at
   - processed at
   - error
   - attempt count
4. Add `InboxMessage` EF mapping and idempotency checks:
   - if processed, ack immediately
   - if in progress and lock valid, skip or reject/requeue
   - on success, mark processed and ack
   - on failure, retry with backoff or dead-letter
5. Wire consumers into ScanOrchestratorService first.
6. Then wire optional UI/realtime consumers if needed.

Acceptance criteria:

- Replaying the same RabbitMQ message does not process it twice.
- ScanOrchestrator reacts to `AssetDiscovered` events through RabbitMQ.
- Failed consumer processing is observable and retryable.
- Correlation id flows from original program/scope/asset/task action through produced events.

## Phase 4 - Worker Host And Workers

Status: shared worker host exists; worker implementations are mostly simulated adapters.

Completed:

- Worker registration.
- Heartbeats.
- Task leasing.
- Progress reporting.
- Rate-limit checks.
- Scope validation before publishing produced assets.
- Produced asset publishing.
- Worker projects for MVP worker set.

Known issue to clean up:

- `ArgusWorkerBackgroundService.RunTaskAsync` has awkward indentation around produced asset publication. Verify behavior and reformat while preserving logic.
- Workers duplicate `WorkerPayload` helper logic. Move shared helpers into `Argus.BuildingBlocks.Workers`.

### 4.1 Shared Worker Host

Outstanding:

- Multi-task concurrency per worker.
- Graceful cancellation and lease release.
- Checkpoint restore helpers.
- Parent-child relationship creation.
- Artifact upload helpers.
- Structured tool execution wrapper.
- Standard output parsing contracts.

Implementation instructions:

1. Change the worker loop from single-task polling to bounded concurrency using `Capability.MaxConcurrency` and a configurable local cap.
2. Add cancellation support:
   - check task state before long operations
   - report cancellation
   - complete as cancelled when requested
3. Add relationship publishing:
   - after publishing a produced asset, create an edge from `task.InputAssetId` to child asset
   - edge type should be produced by worker or inferred by asset type
4. Add artifact helper APIs:
   - store raw tool stdout/stderr
   - store HTTP bodies/headers
   - store screenshots
   - return artifact references for AssetService
5. Add shared helpers:
   - payload parsing
   - registered-domain extraction
   - safe URL normalization
   - scope target extraction
   - retry/delay helpers

Acceptance criteria:

- One worker process can run more than one task when configured.
- Produced child assets have durable relationships to input assets.
- Long-running workers can resume from checkpoints.
- Worker logs include worker id, task id, program id, scope id, and target host.

### 4.2 Real Worker Implementations

Replace simulated output with real, safe adapters. Keep all adapters scoped, rate-limited, and non-exploitative.

#### AmassWorker

Instructions:

1. Run `amass enum` only for authorized domains.
2. Prefer passive mode by default.
3. Make binary path configurable: `ARGUS_AMASS_PATH`.
4. Add timeout and max output size.
5. Parse JSON output if available; otherwise line-oriented parser.
6. Store stdout/stderr as artifacts.
7. Produce Subdomain and DNS observation assets.

Acceptance criteria:

- Missing binary produces a failed task with clear `ErrorCode`.
- Passive enumeration completes for a test domain fixture.
- Output parsing is covered by unit tests using sample output files.

#### SubfinderWorker

Instructions:

1. Run `subfinder` for authorized domains.
2. Make binary path configurable: `ARGUS_SUBFINDER_PATH`.
3. Use JSONL output where possible.
4. Store raw output artifact.
5. Produce Subdomain assets with source metadata.

Acceptance criteria:

- Missing binary is handled cleanly.
- Parser accepts JSONL and plain-line fixtures.

#### DnsResolverWorker

Instructions:

1. Use .NET DNS APIs or a resolver library.
2. Resolve A, AAAA, CNAME, NS, MX, TXT when appropriate.
3. Respect per-host and per-domain rate limits even for DNS if configured.
4. Produce IP and DnsRecord assets.

Acceptance criteria:

- Handles NXDOMAIN and timeout as partial success, not process crash.
- CNAME chains create relationships.

#### HttpProbeWorker

Instructions:

1. Use `HttpClientFactory`.
2. Rate-limit before each request.
3. Try HTTPS first, then HTTP only if policy allows.
4. Follow redirects with a bounded maximum.
5. Store response headers/body references as artifacts.
6. Produce Url and HttpResponse assets.
7. Emit HTTP telemetry events.

Acceptance criteria:

- No request happens without `RateLimitService`.
- Redirect chains are bounded and recorded.
- Response bodies are stored as artifacts, not in asset rows.

#### HtmlDomSpiderWorker

Instructions:

1. Use an HTML parser library, not regex-only parsing.
2. Extract links, forms, script URLs, canonical links, API-looking paths.
3. Normalize relative URLs against base URL.
4. Enforce max depth, max pages, max links per page.
5. Use checkpoint JSON for frontier/visited state.
6. Produce Url, ApiEndpoint, JavaScriptFile assets.

Acceptance criteria:

- Parser fixture tests cover links, forms, scripts, malformed HTML, and relative URLs.
- Crawl cannot exceed configured bounds.

#### JsEndpointExtractorWorker

Instructions:

1. Fetch JS through rate-limited HTTP.
2. Store JS body as artifact.
3. Extract endpoint candidates using a layered approach:
   - URL literals
   - fetch/XMLHttpRequest/axios patterns
   - GraphQL hints
   - config object keys
4. Do not report secrets as confirmed findings; only `FindingCandidate`.
5. Redact obvious sensitive literal values in UI-facing metadata.

Acceptance criteria:

- Fixture tests cover minified JS, source maps references, GraphQL paths, and false positives.
- No raw secret values are printed in logs.

#### WordlistDiscoveryWorker

Instructions:

1. Use a small, configurable MVP wordlist.
2. Rate-limit every request.
3. Bound concurrency per host.
4. Treat 200/204/301/302/401/403 as interesting by policy.
5. Store status and content type metadata.

Acceptance criteria:

- A configured max request count cannot be exceeded.
- 404-heavy targets do not flood the asset table.

#### HeadlessSpiderWorker

Instructions:

1. Use Playwright for .NET or another proven browser automation library.
2. Rate-limit navigations and captured request replays.
3. Bound browser contexts, route depth, runtime, and network event count.
4. Capture rendered routes, XHR/fetch endpoints, JS assets, and screenshots.
5. Store screenshots and HAR/network summary as artifacts.

Acceptance criteria:

- Browser dependencies are documented for Docker.
- Worker handles navigation timeout and SPA hangs.
- Screenshots are artifact references.

#### FingerprintWorker

Instructions:

1. Fingerprint from headers, body hints, JS package hints, TLS/server data where available.
2. Use deterministic rules first.
3. Produce Technology assets with confidence metadata.

Acceptance criteria:

- Fixture tests cover server headers, meta generator tags, framework hints.

#### AssetScoringWorker

Instructions:

1. Replace simple string matching with deterministic scoring rules.
2. Use asset type, subtype, tags, metadata, status, and relationships.
3. Write score updates to AssetService instead of only producing candidates.

Acceptance criteria:

- Scores update existing asset records.
- Reasons are stored for UI display.

## Phase 5 - Web UI

Status: compact UI exists, but it is not yet the full operational command center.

Completed:

- Web shell with command-center style state.
- Program/scope/assets/tasks/workers/events/rate-limits/scan-plans views.
- Live event stream.
- Demo data visibility.
- Asset row inspector and relationship rail.

Outstanding:

- Dense virtualized asset grid.
- Server-side sorting/filtering controls.
- Column chooser.
- Saved views.
- Keyboard shortcuts.
- Bulk tagging.
- Bulk task enqueue.
- Asset detail side panel with observations/artifacts/relationships.
- Task monitor controls for retry/cancel.
- Worker detail view.
- HTTP traffic view.
- Tool runs view.
- Rate-limit pressure view.
- Asset graph visualization.

Implementation instructions:

1. Keep UI dense and operational. Do not turn it into a marketing dashboard.
2. Add API proxy endpoints in `Argus.Web` only when same-origin browser access needs them.
3. Asset Explorer:
   - virtualized table
   - compact row height
   - server-side paging/filtering/sorting
   - column chooser persisted in local storage
   - saved views persisted by backend or local storage for MVP
   - right-side detail panel
4. Detail panel tabs:
   - Summary
   - Metadata
   - Relationships
   - Artifacts
   - Observations
   - Task History
5. Task Monitor:
   - filter by state/capability/program
   - retry failed
   - cancel running/requested
   - inspect checkpoint/output/error
6. Worker Fleet:
   - online/offline
   - running tasks
   - throughput
   - last heartbeat
   - worker capability details
7. Live Events:
   - batch refresh, do not push every event directly into large grids
   - pause/resume stream
   - filter by event type/source/correlation id
8. Rate Limits:
   - bucket utilization bars
   - delayed requests
   - policy source
9. Asset Graph:
   - start simple with selected asset neighborhood
   - avoid rendering entire graph at once

Acceptance criteria:

- UI remains usable with 10,000+ assets by using server-side queries and virtualization.
- Asset grid refreshes in controlled batches.
- Bulk actions call real backend endpoints.
- Text does not overlap at desktop or mobile widths.

## Storage And Artifacts

Status: PostgreSQL and Redis exist; object storage is still missing.

Outstanding:

- Object storage service/integration.
- Artifact API.
- Artifact retention policy.
- Large-output protection.
- Optional local filesystem/minio implementation for development.

Implementation instructions:

1. Add `Argus.BuildingBlocks.Persistence` or `Argus.BuildingBlocks.Artifacts`.
2. Define:
   - `IArtifactStore`
   - `ArtifactReference`
   - `ArtifactContentType`
3. Implement local filesystem artifact store for development:
   - root path from `ARGUS_ARTIFACT_ROOT`
   - content-addressed or date-partitioned keys
4. Optionally add MinIO/S3-compatible storage to compose.
5. Add AssetService artifact endpoints described earlier.
6. Workers should call artifact store through worker host helper, then attach artifact references through AssetService.

Acceptance criteria:

- Raw HTTP bodies, screenshots, and tool outputs are not stored in relational rows.
- Artifact size limits are enforced.
- Artifacts can be inspected from the UI.

## Observability

Status: Aspire/OpenTelemetry defaults exist, but custom metrics are incomplete.

Outstanding metrics:

```text
assets.discovered.count
assets.discovered.rate
tasks.queued.count
tasks.running.count
tasks.failed.count
workers.online.count
http.requests.count
http.requests.rate_limited.count
http.latency.p50/p95/p99
eventbus.consumer_lag
rate_limit.wait_time
tool_run.duration
```

Implementation instructions:

1. Add shared metric helpers in `Argus.BuildingBlocks.Observability`.
2. Add dimensions:
   - `program_id`
   - `scope_id`
   - `asset_id`
   - `task_id`
   - `worker_id`
   - `worker_type`
   - `target_host`
   - `registered_domain`
   - `rate_limit_bucket`
   - `correlation_id`
   - `causation_id`
3. Add structured logging scopes in services and workers.
4. Add `/metrics` compatibility only if needed by the deployment target.

Acceptance criteria:

- Aspire dashboard shows meaningful traces/logs for a complete scan.
- A task can be followed from request to worker completion by correlation id.

## Security, Auth, And Multi-User Readiness

Status: not complete.

Outstanding:

- User/org/API key authentication.
- Authorization boundaries by organization/program.
- Secret handling for tool API keys.
- Audit log.

Implementation instructions:

1. Add auth only after core MVP flow is stable.
2. Start with API key auth for service/API access.
3. Store secrets outside repo:
   - user secrets locally
   - environment variables or secret provider in containers
4. Add audit events for:
   - program creation
   - scope changes
   - policy changes
   - task cancellation/retry
   - artifact access

Acceptance criteria:

- No secrets in source control.
- Scope and program data cannot cross org boundaries once multi-user mode is enabled.

## Testing Plan

Add tests in phases. Do not wait until the end.

Recommended test projects:

```text
tests/Argus.Contracts.Tests
tests/Argus.ProgramScopeService.Tests
tests/Argus.AssetService.Tests
tests/Argus.TaskService.Tests
tests/Argus.RateLimitService.Tests
tests/Argus.WorkerHost.Tests
tests/Argus.WorkerParsers.Tests
tests/Argus.ApiGateway.SmokeTests
```

Priority tests:

1. Scope validation:
   - exact include
   - wildcard include
   - exclusion override
   - CIDR match
   - URL pattern match
2. Asset identity:
   - natural key dedupe
   - metadata merge
   - relationship dedupe
3. Task lifecycle:
   - create
   - lease
   - start
   - progress
   - complete
   - fail
   - retry after lease expiration
4. Rate limiting:
   - per-host bucket
   - per-program bucket
   - denied requests return retry delay
5. Event bus:
   - outbox enqueue
   - dispatcher marks processed
   - failed dispatch retries
   - inbox idempotency once implemented
6. Worker parsers:
   - amass output fixtures
   - subfinder output fixtures
   - HTML extraction fixtures
   - JS extraction fixtures

Acceptance criteria:

- CI runs tests without external network dependency.
- Tool adapter tests use fixture files, not live bug-bounty targets.

## First End-To-End MVP Target

The target remains:

```text
Create program
  -> Add in-scope domain
  -> Emit DomainAssetDiscovered
  -> Run subfinder/amass tasks
  -> Save subdomains
  -> Emit SubdomainAssetDiscovered
  -> HTTP probe subdomains under rate limit
  -> Save URL and HTTP response assets
  -> Extract links from HTML
  -> Save new URL assets
  -> Show all events/assets/tasks live in UI
```

To complete this:

1. Finish event consumers/inbox.
2. Make ScanOrchestrator react to `AssetDiscovered`.
3. Add task dedupe.
4. Replace simulated Subfinder/Amass/HTTP/HTML/JS internals with real safe adapters.
5. Add artifact storage.
6. Add UI task controls and asset detail artifacts.
7. Add integration/smoke tests that run against local compose.

## Suggested Implementation Order

Use this order to minimize rework:

1. Add CI and tests skeleton.
2. Add event consumer/inbox infrastructure.
3. Wire ScanOrchestrator to consume asset/task events.
4. Add task maintenance/retry/dedupe.
5. Add AssetService artifact/tags/status/bulk APIs.
6. Add local artifact storage.
7. Move worker helper duplication into shared worker building block.
8. Add parent-child relationship creation from workers.
9. Implement real HttpProbeWorker.
10. Implement real HtmlDomSpiderWorker with parser fixtures.
11. Implement real JsEndpointExtractorWorker with fixtures.
12. Implement Subfinder and Amass adapters with configurable binary paths.
13. Implement DnsResolver.
14. Implement UI Asset Explorer upgrades.
15. Implement Task Monitor and Worker Fleet controls.
16. Add observability metrics.
17. Add auth/API key support.

## Definition Of Done For MVP

The MVP is complete when:

- A fresh clone can run with Aspire or Docker Compose.
- Demo data can be seeded.
- A real in-scope domain can be added.
- The system creates domain/subdomain/url/content tasks automatically.
- Workers execute bounded, rate-limited, scoped recon.
- Assets and relationships are durably stored.
- Raw artifacts are stored outside hot relational rows.
- Events are delivered through outbox/inbox with idempotent consumers.
- The web UI shows programs, scopes, assets, relationships, tasks, workers, events, HTTP traffic, tool runs, and rate limits.
- Failed tasks can be inspected and retried.
- Tests cover core safety and reliability behavior.
- No deferred/excluded exploit, credential, stealth, or bypass behavior has been added.

