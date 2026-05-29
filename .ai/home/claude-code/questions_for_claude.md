# Questions for Claude

1. What are the top three highest-impact changes we could make to improve this application?

2. Generate a list of all the event types in the system, and leave me a sorted list here.

3. Are the datagrids on the web app being updated in realtime? Are they subscribed to events? How does SignalR work?

---

## Answers (2026-05-29, claude-code)

### 1. Top three highest-impact changes

After tracing the live system end to end, in priority order:

1. **Fix schema provisioning (the silent killer).** Services use `EnsureCreatedAsync()`, which no-ops on the shared `argusdb` that AgentService already created via migrations — so the `assets` table (and task/finding tables) were never created. The engine literally can't persist or read its core data; every `/assets` query 500s and the UI looks empty. Highest impact, smallest effort. *(Stream A — shipped to main 2026-05-29.)*

2. **Make the web app consume the backend event bus (real-time truth).** Today the grids only reflect changes made *through the web UI*; engine activity (a spider discovering assets) never reaches the screen. Bridging integration events → SignalR with batching + caching turns the dashboard from "stale snapshot" into "live system view." *(Stream D.)*

3. **Honest failures + efficient storage.** The BFF gateway fabricates empty-success on downstream errors (outages look like "no data"), and raw artifact bytes live in Postgres instead of a bucket. Removing silent failures (log + surface) and moving large/cold content to object storage with lifecycle tiering makes the system debuggable *and* scalable. *(Streams B and E.)*

### 2. Event types (integration events)

All integration events are defined in
[src/Contracts/Argus.Contracts/Events/IntegrationEventEnvelope.cs](../../../src/Contracts/Argus.Contracts/Events/IntegrationEventEnvelope.cs)
and published over RabbitMQ via the transactional outbox (`outbox_messages`). There are **29** event types.

Sorted alphabetically:

1. ArtifactCreated
2. AssetConfirmed
3. AssetCreated
4. AssetDiscovered
5. AssetPropertyChanged
6. AssetRelationshipDiscovered
7. AssetUpdated
8. EvidenceAdded
9. FindingCandidateCreated
10. FindingCreated
11. FindingTriaged
12. FindingUpdated
13. ProgramCreated
14. ProgramScopeChanged
15. ProxyAdded
16. ProxyRateLimitExceeded
17. ProxyRemoved
18. ProxyStatusChanged
19. RateLimitBackpressureSignaled
20. RateLimitDelayed
21. RateLimitTokenGranted
22. ScopeCreated
23. TaskCompleted
24. TaskFailed
25. TaskLeased
26. TaskProgressed
27. TaskRequested
28. TaskStarted
29. WorkerHeartbeat

Grouped by domain:
- **Assets:** AssetDiscovered, AssetCreated, AssetConfirmed, AssetUpdated, AssetPropertyChanged, AssetRelationshipDiscovered
- **Programs/Scope:** ProgramCreated, ScopeCreated, ProgramScopeChanged
- **Tasks:** TaskRequested, TaskLeased, TaskStarted, TaskProgressed, TaskCompleted, TaskFailed *(tasks being retired)*
- **Workers:** WorkerHeartbeat
- **Rate limiting:** RateLimitTokenGranted, RateLimitDelayed, RateLimitBackpressureSignaled
- **Proxies:** ProxyAdded, ProxyRemoved, ProxyStatusChanged, ProxyRateLimitExceeded
- **Findings/Artifacts:** FindingCandidateCreated, FindingCreated, FindingUpdated, FindingTriaged, ArtifactCreated, EvidenceAdded *(findings being retired)*

### 3. Realtime / SignalR — how the datagrids update

**Short answer:** Partially. The grids update in realtime **only for changes made through the web app's own UI**, not for backend-driven changes (worker discoveries, etc.). The web app does **not** subscribe to the RabbitMQ integration event bus above.

**How it works:**
- Pages render as **Blazor Server interactive circuits** (`InteractiveServerRenderMode`), each holding a WebSocket to the server (`/_blazor`).
- The web app hosts its **own** SignalR hub, `ArgusHub`, at `/hubs/argus` (see [DevelopmentRealtime.cs](../../../src/Argus.Web/DevelopmentRealtime.cs)). Clients call `JoinDevelopment()` to join the `development` group.
- `DevelopmentRealtimeClient` (client side) connects to that hub and raises a `Changed` event carrying `DevelopmentDataChanged { Area, Reason, ObservedAt }` — a **coarse "area X changed, go refetch" signal**, not the actual data.
- Pages subscribe and refetch on relevant areas. E.g. Operations.razor listens for `assets`, `agent-tasks`, `tasks` and reloads assets when one fires.

**The gap:** `DevelopmentRealtimeNotifier.NotifyAsync(...)` is called **only** from the web app's own BFF mutation endpoints — areas: `agents`, `agent-tasks`, `todos`, `provider-usage`, `development-environment`. There is **no consumer of the RabbitMQ event bus** in the web app. Consequences:
- Backend-driven changes (a worker discovering an asset) never reach the grids live.
- The `assets` area is **listened for but never emitted**, so the Operations "LIVE" asset refresh only ever fires incidentally on `agent-tasks` notifications.
- Separately there's a `/ui/events/stream` SSE endpoint that polls the realtime-service `/events` every 2s, but it isn't wired to the grids either.

**To make grids truly realtime for engine activity** (stream D), the web app needs to consume the integration event bus and translate those events into SignalR pushes — with batching for spider bursts and Redis caching for fast hydration.
