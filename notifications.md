# Code Review Notifications

**Generated:** 2026-05-23
**Last Updated:** 2026-05-23T03:47:47Z by agent-1
**Source:** Code review output from reviewer-1 and reviewer-2 across multiple batches (20260522-233640, 20260522-233847, 20260522-234356, 20260523-033112)

---

## CRITICAL (P0) - Immediate Action Required

### 1. Merge Conflict Markers in Source Code
**File:** `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/EfCoreOutboxStore.cs:9-19`

Unresolved `<<<<<<< HEAD` / `=======` / `>>>>>>>` conflict markers left in the `EfCoreOutboxStore` constructor. The `Add` call is left outside the constructor body while `_dbContext = dbContext` remains inside. This causes a **build failure**.

**Action:** Resolve conflict markers immediately. The constructor should preserve `_dbContext = dbContext` assignment and remove the conflicting sections.

**Reviewers:** reviewer-1 (20260522-233640), reviewer-2 (20260522-233640)

---

### 2. Password Exposed in API Response
**Files:** `src/Contracts/Argus.Contracts/Proxies/ProxyContracts.cs:8`, `src/Services/Argus.ProxyRegistryService/Program.cs:232-252`

The `ProxyDto` record includes `Password` as a plain string, exposed directly in API responses via `/proxies` and `/proxies/{id}` endpoints.

**Action:** Remove `Password` from `ProxyDto` entirely, or create `ProxyDtoPublic` excluding sensitive fields. Never return passwords in API responses.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 3. Depth Check Ordering Bug in InMemoryAssetStore.GetSubgraphAsync
**File:** `src/Services/Argus.AssetService/Program.cs:553-588`

Depth is checked BEFORE visited status in the BFS loop. When a node is reached at depth 2, then at depth 1 via a different path, the shorter path is skipped. Fix: check `visited.Contains()` first before depth check.

**Action:** Reorder logic to check visited status before depth limit, then add to visited immediately.

**Reviewer:** reviewer-1 (20260523-033112)

---

## HIGH (P1) - Address Soon

### 4. PIPESTATUS Unreliable in review-agents.sh and auto-run-agents.sh
**Files:** `tools/review-agents.sh:174-188`, `tools/auto-run-agents.sh:505-517`

The pipe to `while` introduces a subshell. `PIPESTATUS[1]` captures opencode exit code correctly, but if the `while` loop fails (e.g., signal), `exit_code` could reflect the wrong process. Also fragile if `cat` fails before opencode starts.

**Action:** Use `set -o pipefail` and capture `${PIPESTATUS[0]}` directly. Consider using process substitution instead of pipes.

**Reviewers:** reviewer-1 (20260522-233847), reviewer-2 (20260522-233832)

---

### 5. reconcile_state() Holds Lock for Entire Iteration
**File:** `tools/agent-coord.sh:361-429`

The `acquire_lock` at line 362 is held while looping through all agents and making multiple state writes. If any state write blocks, all other concurrent `agent-coord.sh` invocations will be blocked.

**Action:** Release the lock between agent iterations or use shorter critical sections. Consider per-agent locking instead of global lock.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 6. Heartbeat Subprocess Not Tracked for Cleanup
**File:** `tools/auto-run-agents.sh:507-527`

The heartbeat subprocess is started after the agent process. If `spawn_agent` exits prematurely, `heartbeat_pid` may race with `kill`. Race condition: subshell may be gone when `kill` is called.

**Action:** Use process group (`set -o monitor` + `kill -PID` of process group) or store PID in a file for reliable cleanup.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 7. Task Requeued with Stale Context After Crash Detection
**File:** `tools/agent-coord.sh:414-420`

When `reconcile_state()` detects a crashed agent, it requeues the task with potentially incomplete or corrupted context. No validation that the context is valid JSON or non-circular.

**Action:** Add validation that the recovery context is valid JSON before requeuing. Consider invalidating stale context entirely.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 8. Bug #14 vs Bug #15 Conflict
**File:** `src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusWorkerBackgroundService.cs`

Bug #14 introduced `SemaphoreSlim` and `_runningTaskCount` for true concurrency. Bug #15 removes these, effectively reverting Bug #14's fix. The heartbeat now always reports 0 running tasks.

**Action:** Clarify whether `SemaphoreSlim` concurrency control is correct. If yes, restore it. If no, update agent context notes to accurately reflect the fix.

**Reviewers:** reviewer-1 (20260522-233640), reviewer-2 (20260522-233832)

---

### 9. Agent Self-Recovery Removed
**File:** `tools/auto-run-agents.sh:576-588`

In commit `5ebef78c`, the resume logic for crashed/stalled agents was removed from `run_agent_if_needed()`. Crash recovery now depends entirely on `reconcile_task_board()` periodic call.

**Action:** Verify that `RECONCILE_INTERVAL` and `AGENT_STALE_TIMEOUT` are configured small enough to prevent tasks from being stuck on crashed agents. Document this behavioral change.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 10. AGENT_COORD_AUTOCOMMIT=0 Default
**File:** `tools/agent-coord.sh:42-55`

Without `AGENT_COORD_AUTOCOMMIT=1`, agent task state changes are not committed to git. Multiple supervisors running with different clones will have divergent state.

**Action:** Either set `AGENT_COORD_AUTOCOMMIT=1` in all supervisor environments, or implement alternative coordination for distributed supervisors.

**Reviewers:** reviewer-1 (20260522-233847), reviewer-2 (20260522-233832)

---

### 11. Webhook Secret in HTTP Headers
**File:** `src/Services/Argus.RealtimeService/WebhookService.cs:183-186`

Secrets transmitted in headers may be logged by proxies, web servers, or monitoring systems.

**Action:** Consider using HMAC signatures or other non-replayable authentication methods instead of static header values.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 12. Hardcoded Default Credentials in Compose
**File:** `deploy/compose.yaml:267-268`

```yaml
MINIO_ROOT_USER: ${ARGUS_MINIO_ACCESS_KEY:-argus}
MINIO_ROOT_PASSWORD: ${ARGUS_MINIO_SECRET_KEY:-argus-minio-secret}
```

**Action:** Enforce strong passwords via environment variables. Add documentation about required secrets in deployment.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 13. Dead-Letter Admin Endpoints Expose Replay Without Authentication
**Files:** `src/Services/Argus.RealtimeService/Program.cs`, `tools/reviews/...`

The `/admin/dead-letters/{eventId}/replay` endpoint allows re-publishing any dead-lettered message without authentication. This is a significant operational risk in production.

**Action:** Add authentication/authorization to admin endpoints, or restrict to internal network access.

**Reviewers:** reviewer-1 (20260522-233847), reviewer-2 (20260522-233832)

---

## MEDIUM (P2) - Address When Possible

### 14. CronExpression.GetNextOccurrence May Return Null Silently
**File:** `src/Services/Argus.ScanOrchestratorService/Program.cs:113, 130, 154, 165, 176, 177, 196`

`CronExpression.GetNextOccurrence` returns `DateTimeOffset?`. When assigned to `NextRunAt`, no null-check is performed. Scheduled scans with invalid cron expressions could cause infinite reprocessing loops.

**Action:** Add null-check for `GetNextOccurrence` result. Log a warning for unmapped asset types in `ComputeStalenessScore`.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 15. Database Query in Hot Path (WebhookService)
**File:** `src/Services/Argus.RealtimeService/WebhookService.cs:77-83`

Database query on every event in `ProcessEvent`. Under high event volume, this creates database load.

**Action:** Consider caching webhook configs with a short TTL or using a refresh interval pattern.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 16. Unbounded Retry Delay in WebhookService
**File:** `src/Services/Argus.RealtimeService/WebhookService.cs:228`

Exponential backoff grows unbounded (2^6 = 64 seconds). Consider capping at a reasonable max (e.g., 30 seconds).

**Action:** Add max cap to retry delay.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 17. Scheduled Scan Processes Scans Sequentially
**File:** `src/Services/Argus.ScanOrchestratorService/Program.cs:152-156`

Scans are processed one at a time with `await`. If one scan is slow, other due scans are delayed up to 1 minute.

**Action:** Consider `Task.WhenAll` with a bounded degree of parallelism.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 18. take_task() Uses `.status // "active"` Which Masks "inactive"
**File:** `tools/agent-coord.sh:247-250`

If an agent is truly inactive (e.g., disabled), this silently overrides the inactive status to "active".

**Action:** Reject or warn when taking a task for an inactive agent.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 19. git_pull || true Masks All Errors
**File:** `tools/auto-run-agents.sh:653-654`, `tools/auto-run-agents.sh:656`, `tools/auto-run-agents.sh:679`

If `git_pull` fails due to network, auth, or merge conflicts, the supervisor continues silently.

**Action:** At minimum, log a warning when `git_pull` fails.

**Reviewers:** reviewer-1 (20260522-233640), reviewer-1 (20260522-233847), reviewer-2 (20260522-233832)

---

### 20. WorkerPriority Enum Missing Explicit Values
**File:** `src/Contracts/Argus.Contracts/Tasks/TaskContracts.cs`

The `WorkerPriority` enum has implicit values (0, 1, 2). If members are reordered, SQL logic silently breaks.

**Action:** Use explicit values for enum members.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 21. Inconsistent Error Handling in ScopeSyncService
**File:** `src/Services/Argus.ProgramScopeService/ScopeSyncService.cs:70-75`

Errors are swallowed silently. If a provider API fails repeatedly, there's no alerting mechanism.

**Action:** Track consecutive failures and add circuit breaker logic.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 22. Missing Validation for Proxy URL Format
**File:** `src/Services/Argus.ProxyRegistryService/Program.cs:55-56`

Proxy URLs are accepted without validation. Malformed URLs could cause downstream failures.

**Action:** Add URL format validation (scheme, host, port ranges).

**Reviewer:** reviewer-1 (20260523-033112)

---

### 23. Magic Strings for Event Actions
**File:** `src/Services/Argus.ProgramScopeService/ScopeSyncService.cs:97, 112, 123, 135`

Action strings ("created", "deleted") are magic strings.

**Action:** Define as constants or enums for type safety.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 24. HttpClient in TaskCompletedConsumer Not Disposed
**File:** `src/Services/Argus.AssetService/Program.cs:1070-1071`

The `HttpClient` created via `IHttpClientFactory` isn't explicitly returned to the pool (no `DisposeAsync` call).

**Action:** Use `await using` or proper dispose pattern.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 25. StalenessScore Computed on Every ToDto() Call
**File:** `src/Services/Argus.AssetService/Program.cs:726-750, 758`

`ComputeStalenessScore()` is called in `ToDto()` every time. Floating-point division and dictionary lookup add CPU overhead for large asset lists.

**Action:** Consider caching the score or computing it at insert time.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 26. IPoisonMessageStore May Be Dead Code
**File:** `src/Services/Argus.RealtimeService/Program.cs`

The dead-letter admin endpoints call `IPoisonMessageStore` methods, but no code was observed that writes to it. The `InMemoryPoisonMessageStore` may be a no-op stub.

**Action:** Confirm the population path or remove the admin endpoints if they are dead code.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 27. Batch Size Limit for Scheduled Scans
**File:** `src/Services/Argus.ScanOrchestratorService/Program.cs:176`

`assetsResult.Items.Take(10)` caps at 10 assets per run while `pageSize=100` is sent to query. Mismatch between queried (100) and processed (10) could be confusing or too restrictive.

**Action:** Document the intentional rate limiting, or adjust limits if they are too aggressive.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 28. Capability Routing Is a Stub
**File:** `src/Contracts/Argus.Contracts/Tasks/TaskContracts.cs:63-67`, `src/Services/Argus.TaskService/Program.cs:610`

`LeaseReconTaskRequest.SubscribedAssetTypes` parameter is passed but never used in routing decisions. Workers receive all tasks matching their capability, regardless of asset type subscription.

**Action:** Implement functional capability routing, or remove the unused parameter.

**Reviewer:** reviewer-2 (20260522-233832) - Bug #13

---

### 29. Build Status "CHECKING" Treated as Critical Alert
**File:** `tools/dashboard.sh:170-184`

"CHECKING" status is treated as a critical alert and shown in the red warning banner. A pending build check is not critical.

**Action:** Remove "CHECKING" from critical alerts, or use a separate warning level.

**Reviewer:** reviewer-1 (20260522-233847)

---

## LOW (P3) - Consider for Future

### 30. Unused Field in ProxyRegistry
**File:** `src/Services/Argus.ProxyRegistryService/Program.cs:181`

The `WorkerType` parameter in `/proxies/next` is accepted but never used in `SelectProxy`.

**Reviewer:** reviewer-1 (20260523-033112)

---

### 31. Reviewer State File Parse Failure Not Handled
**File:** `tools/review-agents.sh:61-70`

If `jq -e .` fails due to parse error, the malformed file is not repaired or moved aside. Stale temp files accumulate.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 32. doctor_agents() Output Not Parseable
**File:** `tools/agent-coord.sh:680-684`

`doctor_agents()` emits free-text warnings that cannot be consumed programmatically.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 33. Shell Script Diff Verbosity - mktemp Pattern
**Files:** `tools/agent-coord.sh:37-41`, `tools/agent-state-lib.sh:52-55`

The `XXXXXX` template in `mktemp` relies on system implementation. On some systems, this may not be sufficiently random.

**Reviewer:** reviewer-2 (20260522-233832)

---

### 34. Missing EOF Trailing Newline
**File:** `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/EfCoreOutboxStore.cs:89`

**Reviewer:** reviewer-1 (20260522-233640)

---

### 35. Reconcile State and Checkpoint Can Race
**File:** `tools/agent-coord.sh`

Both `reconcile_state()` and `checkpoint_agent()` acquire the same lock but `write_state` is inside the locked section. Per-agent lock doesn't protect against concurrent `write_state` calls.

**Reviewer:** reviewer-1 (20260522-233847)

---

### 36. Reviewer Loop Review
**File:** `tools/reviews/review-agents.sh`

The review was generated by the reviewer agent loop itself. The review document is being written by the system being reviewed (meta-recursion concern).

**Reviewer:** reviewer-2 (20260522-233832)

---

## OPEN QUESTIONS Requiring Your Input

### Q1: StalenessScore Intent
`AssetDto.StalenessScore` is computed server-side at query time via `ComputeStalenessScore()` but not persisted. Identical queries at different times return different scores. Is this intentional for real-time staleness, or should it be precomputed and stored?

**From:** reviewer-1 (20260522-233847)

---

### Q2: Task Reclaim Race Condition
`reconcile_state()` requeues tasks whose assignee is not currently working them. What is the expected behavior when multiple agents race to reclaim the same requeued task? Does the task board locking prevent double-assignment?

**From:** reviewer-1 (20260522-233847)

---

### Q3: WorkerPriority Semantic
The `WorkerPriority` enum has three values (high/normal/low). In `EfTaskStore.LeaseTaskAsync`, SQL uses `task.Priority >= request.MinimumPriority`. If a worker requests `WorkerPriority.Normal`, it receives tasks with priority >= Normal (Normal and High). Is this intended, or should the comparison be equality-based?

**From:** reviewer-1 (20260522-233847)

---

### Q4: AGENT_COORD_AUTOCOMMIT=0 Intentional?
Is the autocommit=0 default intentional for production? If so, how do multiple supervisor instances coordinate without git-pushed state? Distributed supervisors could have divergent views.

**From:** reviewer-2 (20260522-233832)

---

### Q5: Agent Self-Recovery Intent
Why was the agent self-recovery (resume from crashed) removed from `run_agent_if_needed()`? Was the reconcile loop intended to fully replace this?

**From:** reviewer-2 (20260522-233832)

---

### Q6: Heartbeat Accuracy Under Concurrent Load
Does the heartbeat (after Bug #14/#15) account for fire-and-forget pattern where multiple `RunTaskWithReleaseAsync` calls may be in flight simultaneously? The semaphore count fluctuates between wait and release.

**From:** reviewer-2 (20260522-233832)

---

### Q7: Reviewer RECENT_LIMIT Appropriateness
Is the RECENT_LIMIT of 20 appropriate for the reviewer batch? If many commits accumulate between reviewer cycles, the batch could grow large.

**From:** reviewer-2 (20260522-233832)

---

## FOLLOW-UP ITEMS

1. **Build verification needed:** Run `dotnet build` on `Argus.BuildingBlocks.EventBus`, `Argus.BuildingBlocks.Workers`, `Argus.RealtimeService` to confirm conflict markers don't break CI.
2. **Integration test for reconcile_state():** Simulate agent crash mid-task and verify task is correctly requeued.
3. **Verify heartbeat accuracy under concurrent load:** Add integration test for `MaxConcurrency > 1` scenario.
4. **Integration test for dead-letter replay:** Publish poison message, verify it appears in `/admin/dead-letters`, replay it, confirm it reaches DLQ consumer.
5. **Investigate CronExpression.Validate:** Ensure `POST /scheduled-scans` validates correctly, no DB bypass path exists.
6. **Add shell script tests:** Use bats or shUnit2 for `agent-coord.sh`, `auto-run-agents.sh`, `review-agents.sh` critical paths.
7. **Reviewer findings comparison:** Two reviewers per batch but no mechanism to compare or merge their findings.

---

## RESIDUAL RISK SUMMARY

| Category | Risk Level | Primary Concerns |
|----------|------------|------------------|
| Build | HIGH | Unresolved conflict markers in EfCoreOutboxStore.cs |
| Security | HIGH | Password exposure in API, unauthenticated admin endpoints |
| Concurrency | MEDIUM | SemaphoreSlim removal reverted Bug #14; heartbeat always 0 |
| Coordination | MEDIUM | AGENT_COORD_AUTOCOMMIT=0 causes state drift |
| Operations | MEDIUM | Silent git pull failures; no agent self-recovery |
| Integration | MEDIUM | Missing tests for critical paths; poison message store unverified |
| Performance | LOW | Staleness score computed on every DTO call; DB query in hot path |