# Argus Recon Platform — Enhancement Suggestions

These are enhancements beyond correctness fixes — things that would meaningfully improve capability, reliability, operability, or developer experience. They assume the issues in the prior report are resolved.

---

## 1. Dead Letter Exchange and Poison Message Handling

**Category: Reliability**

Currently, a message that fails repeatedly in a consumer gets requeued indefinitely (`BasicNackAsync` with `requeue: true`). A malformed event or a bug in one handler can cause a consumer to spin on the same message forever, blocking all subsequent messages in that queue.

**Enhancement:**

Declare a dead letter exchange (`argus.events.dlx`) and configure each queue with `x-dead-letter-exchange`. After N nack cycles the broker automatically routes the message to the dead letter queue without any code changes in the consumer:

```csharp
var args = new Dictionary<string, object>
{
    ["x-dead-letter-exchange"] = "argus.events.dlx",
    ["x-dead-letter-routing-key"] = $"dead.{eventType}",
    ["x-message-ttl"] = 86_400_000  // 24h retention on DLQ
};
await _channel.QueueDeclareAsync(queueName, durable: true, arguments: args);
```

Add a `GET /admin/dead-letters` endpoint in the RealtimeService (or a dedicated admin service) to inspect and replay poisoned messages. Without this, silent message loss is the only alternative to an infinite retry loop.

---

## 2. Event Schema Versioning

**Category: Evolvability**

All event records are currently unversioned. When you add a field to `AssetConfirmed` (once it exists), any consumer running the old code will silently deserialize without the new field. When you rename a field, consumers explode. In a distributed system with rolling deploys this causes production incidents.

**Enhancement:**

Add a `SchemaVersion` field to `IntegrationEventEnvelope`:

```csharp
public required int SchemaVersion { get; init; } = 1;
```

In consumers, check the version before deserializing the payload and route to the appropriate handler version. Use a convention like `AssetConfirmed/v2` as the event type string for breaking changes.

A lighter-weight option is to make all event payload fields nullable/optional and evolve additively — never remove or rename fields, only add. This is simpler to operate but breaks down when structural changes are required.

---

## 3. Asset Change Detection — `AssetPropertyChanged` Event

**Category: Intelligence**

Currently assets are upserted (`UpsertAsync`), metadata is merged, and that's it. There's no record of *what changed* between observations. If a previously closed port opens, if a new technology appears, or if an HTTP title changes, the system has no way to surface that as a signal.

**Enhancement:**

In `EfAssetStore.UpsertAsync`, diff the incoming properties against the existing record before merging. If material properties changed, emit an `AssetPropertyChanged` event:

```csharp
public sealed record AssetPropertyChanged(
    Guid AssetId,
    Guid ProgramId,
    string AssetType,
    string Value,
    IReadOnlyDictionary<string, string> PreviousMetadata,
    IReadOnlyDictionary<string, string> NewMetadata,
    IReadOnlyCollection<string> ChangedKeys);
```

Workers and the UI can subscribe to this for alerting. A subdomain that changes its IP, a URL that suddenly returns 200 after returning 404, or a new `Server:` header appearing are all operationally significant signals in a recon context.

---


---

## 5. Worker Priority Lanes

**Category: Performance**

All tasks go into the same queue, processed in `ORDER BY Attempt, TaskId`. A slow Amass enumeration (minutes) and a fast HttpProbe (seconds) compete for the same workers. High-value confirmations are blocked behind low-value discoveries.

**Enhancement:**

Add a `Priority` field to `CreateReconTaskRequest` and `TaskRecord` (int, e.g., 0=low, 1=normal, 2=high). In the `TaskService` lease query, order by `Priority DESC, Attempt ASC, TaskId ASC`. Workers can optionally filter by a minimum priority when calling `POST /tasks/lease`.

For the event-driven path, map priorities to separate RabbitMQ queue bindings so high-priority events route to dedicated queues and worker instances. HttpProbe confirmations should be high priority; Wordlist discovery guesses should be low.

---

## 6. Per-Program Rate Limit Policies Wired into RateLimitService

**Category: Safety / Correctness**

`ProgramScopeService` already has a `rate_limit_policies` table and `GET/POST /programs/{programId}/rate-limit-policies` endpoints. `RateLimitService` has no knowledge of these policies. Every program is currently rate-limited the same way regardless of its declared budget, target sensitivity, or programme rules.

**Enhancement:**

On startup (and on `ProgramScopeChanged` events), `RateLimitService` should fetch the active policies for each program from `ProgramScopeService` and use them to set per-program bucket capacities. When a worker requests a rate limit token with a `ProgramId`, the service uses the per-program policy instead of the default global capacity.

This is especially important for bug bounty work where some programmes explicitly state a request rate (e.g., "no more than 10 req/s per subdomain") and violating it has legal/ethical consequences.

---

## 7. Adaptive Rate Limiting on 429 / 503 Responses

**Category: Safety**

Workers that receive HTTP 429 or 503 responses should automatically feed back into the rate limiter rather than just logging a warning. Currently a 429 from a target is an error, the task fails, and nothing adjusts. The next worker run immediately tries again at the same rate.

**Enhancement:**

Workers emit a new event `RateLimitBackpressureSignaled(host, retryAfter)` when they observe a 429 response. `RateLimitService` consumes this event and temporarily reduces the bucket capacity for that host. The `RetryAfter` header value from the target is respected if present.

On the task side, when a rate-limit check returns `IsAllowed = false`, the worker framework should automatically delay the task (set state to `RetryPending` with a `RetryAfter` delay) rather than completing with `PartiallySucceeded = true` and a manual note in the summary.

---

## 8. Signed Scope Snapshots for Workers

**Category: Safety / Performance**

Every produced asset currently triggers a synchronous HTTP call from the worker to `ProgramScopeService` for scope validation. Under high throughput (a wordlist spider producing hundreds of candidate URLs per second) this is a bottleneck and a single point of failure — if `ProgramScopeService` is slow or restarting, workers stall.

**Enhancement:**

`ProgramScopeService` issues signed scope snapshots: a compact, cacheable representation of the in-scope rules for a program, signed with an HMAC key. Workers fetch the snapshot at task start, validate the signature, and perform scope checks locally for the duration of the task. A `ScopeSnapshotVersion` field is included in task payloads so workers know which snapshot to use.

Snapshots are invalidated by `ProgramScopeChanged` events. Workers that hold a stale snapshot can fall back to the live API call. This reduces scope validation latency from one network hop per asset to zero for the common case.

---

## 9. Artifact Storage

**Category: Completeness**

The handoff doc lists this as outstanding and it's worth expanding. Currently workers that capture HTTP response bodies, screenshots, or raw tool output have nowhere to put them except in `OutputSummaryJson` on the task (which is a `jsonb` column in PostgreSQL — the wrong place for large blobs). There's no way to inspect the raw content that led to an asset discovery.

**Enhancement:**

Add `Argus.BuildingBlocks.Artifacts` with an `IArtifactStore` interface and two implementations:

- `LocalFileArtifactStore` — writes to a volume path under `ARGUS_ARTIFACT_ROOT`, content-addressed (SHA-256 of content as the key).
- `S3ArtifactStore` (or MinIO-compatible) — for production deployments.

`WorkerProducedAsset` gains an `ArtifactReferences` collection. Workers attach the raw response body, screenshot, or tool stdout as artifacts. `AssetService` stores artifact references alongside the asset record. The UI shows them in the asset detail panel.

Add MinIO to `compose.yaml` for local development. This avoids any cloud dependency.

---

## 10. Correlation Chain Visualization

**Category: Observability**

`IntegrationEventEnvelope` already carries `CorrelationId` and `CausationId`. This means every event in a chain — from the initial domain scan through subdomain discovery, HTTP probing, and HTML extraction — should be traceable back to the original trigger. Currently this chain is stored but never surfaced.

**Enhancement:**

Add a `GET /events/chain/{correlationId}` endpoint to `RealtimeService` that returns all events sharing the same `CorrelationId`, ordered by `OccurredAt`. The UI renders this as a timeline tree showing the causal path: which asset triggered which task, which task produced which event, which event triggered which worker.

This is extremely valuable for debugging why a particular asset was or wasn't processed, and for understanding the completeness of a scan.

---

## 11. Custom Operational Metrics

**Category: Observability**

`Argus.ServiceDefaults` registers an `"Argus.Recon"` meter but no instruments are defined against it. The handoff doc lists 12 specific metrics that should exist. Without them, the only observability is log lines and traces.

**Enhancement:**

Add `Argus.BuildingBlocks.Observability` with shared metric instrument definitions:

```csharp
public static class ArgusMetrics
{
    private static readonly Meter Meter = new("Argus.Recon");

    public static readonly Counter<long> AssetsDiscovered =
        Meter.CreateCounter<long>("argus.assets.discovered", "assets");
    public static readonly Counter<long> AssetsConfirmed =
        Meter.CreateCounter<long>("argus.assets.confirmed", "assets");
    public static readonly Histogram<double> TaskDuration =
        Meter.CreateHistogram<double>("argus.task.duration", "ms");
    public static readonly UpDownCounter<int> TasksRunning =
        Meter.CreateUpDownCounter<int>("argus.tasks.running", "tasks");
    public static readonly Counter<long> RateLimitDelays =
        Meter.CreateCounter<long>("argus.ratelimit.delayed", "requests");
    // ...
}
```

Tag all instruments with `program_id`, `worker_type`, `asset_type`, `registered_domain`. This feeds directly into the Aspire dashboard and any OTLP-compatible backend (Grafana, Honeycomb, Datadog).

---

## 12. Worker Graceful Draining on Shutdown

**Category: Reliability**

When a container receives SIGTERM, `BackgroundService.StopAsync` is called with a 5-second default cancellation timeout. Any in-progress task gets its `CancellationToken` cancelled mid-execution. If the worker is 90% through a long Amass run, all that work is discarded and the task is requeued.

**Enhancement:**

Override `StopAsync` in `ArgusWorkerBackgroundService` to honour a configurable drain timeout:

```csharp
public override async Task StopAsync(CancellationToken cancellationToken)
{
    _logger.LogInformation("Worker draining, waiting up to {Timeout} for in-flight tasks",
        _options.DrainTimeout);
    // signal no new tasks, wait for running tasks to complete
    await base.StopAsync(cancellationToken);
}
```

Add `ARGUS_WORKER_DRAIN_TIMEOUT` (default 60s). In `compose.yaml`, set `stop_grace_period: 90s` on worker services. Workers that support checkpointing should write a checkpoint before yielding to cancellation, so a restart resumes rather than replays from scratch.

---

## 13. Finding Candidate Promotion Workflow

**Category: Domain Completeness**

`AssetType.FindingCandidate` exists in the enum with the highest interesting score (70) but there's no defined workflow for what happens with it. Currently a worker can produce a `FindingCandidate` asset and it sits in the database with no further action.

**Enhancement:**

Define a first-class promotion workflow:

1. A worker produces a `FindingCandidate` with metadata describing why (e.g., `{"reason": "admin panel detected", "evidence": "title contains 'admin'"}`)
2. `AssetService` emits a `FindingCandidateCreated` event.
3. A (future) `ValidationWorker` or manual operator reviews candidates in the UI.
4. The operator either promotes to `Finding` (a new asset type, with severity/CVSS data) or dismisses with a reason.
5. Dismissed candidates get a `dismissed` tag and `Archived` status so they don't resurface.

This is the bridge between automated recon and human-reviewed findings — the most valuable output of the system.

---

## 14. Proxy Rotation Support

**Category: Stealth / Operational**

`RateLimitRequest` already has a `ProxyId` field. `ArgusWorkerOptions` has a `RealtimeServiceBaseAddress` but no proxy configuration. The proxy field is never populated. Bug bounty recon often requires routing through multiple IPs to avoid target-side blocking.

**Enhancement:**

Add a `ProxyRegistryService` (or extend `RateLimitService`) that manages a pool of proxies with per-proxy rate limit buckets. Workers that require HTTP access (`RequiresHttp: true`) request a proxy assignment along with a rate limit token. The rate limit check response includes a `ProxyEndpoint` field when a proxy is required. The worker configures its `HttpClient` with that proxy for the duration of the request.

The `RateLimitService` tracks per-proxy usage alongside per-host usage, so a single proxy doesn't get burned by too many requests to the same target.

---

## 15. Scheduled / Recurring Scan Plans

**Category: Operations**

All scans are triggered manually. There's no way to say "re-run this domain discovery plan every 24 hours."

**Enhancement:**

Add a `CronExpression` field to `ScanPlanDto` and a `RecurrencePolicy` entity in `ScanOrchestratorService`. A `ScanSchedulerBackgroundService` checks for due recurrences every minute and automatically creates new scan plans from the template. The scan plan record tracks `LastRunAt` and `NextRunAt`.

Use the NCrontab or Cronos library for cron expression parsing — both are well-maintained, lightweight, and have no external dependencies. Avoid Hangfire or Quartz unless you need their full feature set; for this use case a simple loop is sufficient.

---

## 16. Asset Graph Traversal API

**Category: Intelligence**

`AssetService` stores relationships but only exposes `GET /assets/{assetId}/relationships` (one hop). There's no way to ask "give me the full subgraph rooted at this domain" or "show me all assets reachable from this scope within 3 hops."

**Enhancement:**

Add a graph traversal endpoint backed by PostgreSQL's recursive CTEs:

```sql
WITH RECURSIVE reachable AS (
    SELECT to_asset_id FROM asset_relationships WHERE from_asset_id = @rootId
    UNION
    SELECT r.to_asset_id FROM asset_relationships r
    JOIN reachable ON r.from_asset_id = reachable.to_asset_id
)
SELECT * FROM assets WHERE asset_id IN (SELECT to_asset_id FROM reachable);
```

```
GET /assets/{assetId}/subgraph?maxDepth=3&assetTypes=Subdomain,Url
```

This enables the UI asset graph view described in the handoff doc without pulling the entire asset table into memory.

---

## 17. Worker Testing SDK — Fixture-Based Test Harness

**Category: Developer Experience**

The handoff doc calls for fixture-based worker tests (amass output, subfinder output, HTML parsing). Currently there's no test infrastructure at all in the `tests/` directory. Writing tests against real workers is impractical because they call external binaries.

**Enhancement:**

Add `Argus.BuildingBlocks.Workers.Testing` with:

```csharp
public class WorkerTestHarness<TWorker> where TWorker : IReconWorker
{
    public WorkerTestHarness<TWorker> WithTask(ReconTaskDto task);
    public WorkerTestHarness<TWorker> WithRateLimitAlwaysAllowed();
    public WorkerTestHarness<TWorker> WithScopeAlwaysInScope();
    public Task<WorkerTestResult> RunAsync();
}

public sealed record WorkerTestResult(
    WorkerProcessResult ProcessResult,
    IReadOnlyCollection<WorkerProducedAsset> ProducedAssets,
    IReadOnlyCollection<ProgressReport> ProgressReports);
```

Workers that wrap external tools (Amass, Subfinder) should accept a `Func<string, Task<string>>` command executor that the test harness replaces with a fixture reader. This makes all workers testable without a network or installed binaries.

---

## 18. Export / Import for Programs and Scope

**Category: Operations**

There's no way to back up a program's configuration, share scope templates between programmes, or migrate between environments. Operators rebuilding a compose stack lose all scope rules.

**Enhancement:**

Add `GET /programs/{programId}/export` returning a self-contained JSON document with the program, all scopes, exclusions, rules, and rate limit policies. Add `POST /programs/import` that accepts this format and reconstructs the full configuration.

This also enables a "scope library" — a collection of pre-built scope templates for common bug bounty platforms that can be imported to bootstrap a new program quickly.

---

## 19. Webhook Notifications for High-Value Events

**Category: Operations**

The only way to know something interesting happened is to watch the live event stream in the UI. For a recon platform that may run unattended for hours, operators want push notifications.

**Enhancement:**

Add a `WebhookService` (or integrate into `RealtimeService`) that allows configuration of webhook targets with event-type filters:

```json
{
  "url": "https://hooks.slack.com/services/...",
  "events": ["FindingCandidateCreated", "AssetConfirmed"],
  "filters": { "programId": "...", "minInterestingScore": 50 }
}
```

Webhooks fire on matching events via the existing SSE subscription mechanism. Include retry logic with exponential backoff. Store webhook configs in PostgreSQL so they survive restarts.

---

## 20. Scope Ingestion from HackerOne / Bugcrowd APIs

**Category: Domain Completeness**

Scopes are currently entered manually. Bug bounty programmes update their scopes regularly and manual sync is error-prone — missing a new wildcard scope means you skip valid targets; missing a new exclusion means you scan out-of-scope assets.

**Enhancement:**

Add optional scope provider adapters in `ProgramScopeService`:

```csharp
public interface IScopeProvider
{
    string ProviderName { get; }
    Task<ScopeSnapshot> FetchAsync(string programHandle, CancellationToken ct);
}
```

Implement `HackerOneScopeProvider` and `BugcrowdScopeProvider` using their public APIs (both have programme scope endpoints). Run a scheduled sync every 6 hours. On any scope change, publish `ProgramScopeChanged` to trigger downstream cache invalidation.

API keys for these providers should come from environment variables (`HACKERONE_API_TOKEN`, `BUGCROWD_API_TOKEN`), never hardcoded.

---

## 21. Bulk Asset Actions — Tag and Enqueue

**Category: UI / Operations**

`AssetContracts.cs` already defines `BulkTagRequest` and `BulkEnqueueRequest`. The store interfaces expose these methods. The API endpoints and UI bindings don't exist yet.

**Enhancement:**

Wire up:
```
POST /assets/bulk/tag     → BulkTagRequest
POST /assets/bulk/enqueue → BulkEnqueueRequest
```

In the UI, multi-select rows in the asset grid, then apply a tag or enqueue a task type to all selected assets. This enables workflows like: "I manually reviewed these 40 subdomains, tag them all as `reviewed`" or "I want to run a fingerprint scan on all assets tagged `alive` in this programme."

Deduplication (Issue #22 fix) is a prerequisite — bulk enqueue without dedupe will create duplicate tasks.

---

## 22. Asset Staleness Scoring

**Category: Intelligence**

Assets age out. A subdomain that was live 30 days ago may have been decommissioned. Currently `LastSeenAt` is stored but nothing acts on it. Operators have no way to know which assets are stale without querying manually.

**Enhancement:**

Add a `StalenessScore` (int, 0–100) to `AssetDto` computed from `(now - LastSeenAt)` relative to the expected scan frequency for that asset type. Surface this in the UI as a visual indicator (colour band or age column). Add `minStalenessScore` / `maxStalenessScore` to `AssetQuery` so operators can filter to "things I haven't checked in 7 days."

The `ScanSchedulerService` from enhancement #4 uses `StalenessScore` as the primary scheduling signal — highest staleness gets scanned first.

---

## 23. `WorkerCapabilityDescriptor.ProducedAssetTypes` Used for Graph Completeness Checking

**Category: Intelligence**

Workers declare what asset types they produce. This metadata is registered but never used to reason about scan completeness. You can't currently ask: "are there any subdomains that have never been processed by an HttpProbeWorker?"

**Enhancement:**

Add a `GET /scan-plans/{scanPlanId}/coverage` endpoint that cross-references:
- All assets of each type discovered under a programme.
- The `ProducedAssetTypes` of each worker type that was run.
- Which assets have tasks in `Succeeded` state for each relevant worker type.

The response reports coverage gaps: "423 subdomains, 387 http-probed (91%), 36 not yet probed." This drives automatic backfill task creation and gives operators a clear picture of scan completeness.

---

## Summary Table

| # | Enhancement | Category |
|---|-------------|----------|
| 1 | Dead letter exchange + poison message inspection/replay | Reliability |
| 2 | Event schema versioning | Evolvability |
| 3 | `AssetPropertyChanged` event for diff detection | Intelligence |
| 4 | Re-scan scheduling based on `LastScannedAt` | Operations |
| 5 | Worker priority lanes (high/normal/low) | Performance |
| 6 | Per-program rate limit policies wired into RateLimitService | Safety |
| 7 | Adaptive rate limiting on 429/503 feedback | Safety |
| 8 | Signed scope snapshots for workers (local validation) | Safety / Performance |
| 9 | Artifact storage (MinIO/S3 + local filesystem) | Completeness |
| 10 | Correlation chain visualization (`/events/chain/{correlationId}`) | Observability |
| 11 | Custom operational metrics via shared `ArgusMetrics` class | Observability |
| 12 | Graceful worker draining on SIGTERM with checkpoint | Reliability |
| 13 | `FindingCandidate` promotion workflow to `Finding` | Domain Completeness |
| 14 | Proxy rotation support via proxy pool in RateLimitService | Stealth / Ops |
| 15 | Scheduled / recurring scan plans (cron-based) | Operations |
| 16 | Asset graph traversal API (`/assets/{id}/subgraph`) | Intelligence |
| 17 | Worker test SDK with fixture-based harness | Developer Experience |
| 18 | Program/scope export and import | Operations |
| 19 | Webhook notifications for high-value events | Operations |
| 20 | Scope ingestion from HackerOne / Bugcrowd APIs | Domain Completeness |
| 21 | Bulk asset tag and enqueue endpoints + UI bindings | Operations / UI |
| 22 | Asset staleness scoring and stale-first scheduling | Intelligence |
| 23 | Scan coverage reporting via `WorkerCapabilityDescriptor.ProducedAssetTypes` | Intelligence |
