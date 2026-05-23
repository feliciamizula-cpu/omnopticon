#!/bin/bash
set -e

STATE_FILE="$(cd "$(dirname "$0")" && pwd)/.agent-tasks.json"
cp "$STATE_FILE" "${STATE_FILE}.bak"

jq '
(.tasks[] | select(.id == "046")).description = "Core: Reimplement AI agent framework (tools/agent-*.sh, auto-run-agents.sh, watchdog) as a .NET Core console project named Argus.AgentCoordinator in src/Tools/. Must support: 1) configurable agent pool with MAX_CONCURRENT=5 dev, 2 devops, 2 reviewers via env vars or config file; 2) JSON-based task board at tools/.agent-tasks.json; 3) per-agent state files at tools/.agent-state-*.json; 4) heartbeat monitoring every 45s per agent; 5) crash detection via PID check (kill -0); 6) automatic task assignment from pending queue sorted by priority then ID; 7) agent prompt writing to tools/.agent-prompt-*.txt with task context; 8) invocation of opencode run --dir <workdir> to execute each agent task; 9) devops standing-task sweep every 120s for health checks; 10) reviewer cycle triggered on new git commits; 11) watchdog process that auto-restarts supervisor on crash; 12) SIGTERM/SIGINT handling for graceful shutdown and state persistence. Output: src/Tools/Argus.AgentCoordinator/ with .csproj + Program.cs. Use System.Text.Json for state, System.Diagnostics.Process for opencode invocation."

' "$STATE_FILE" > /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "047")).description = "Enhancement #11: Custom operational metrics - Add Argus.BuildingBlocks.Observability project with shared ArgusMetrics class containing a static Meter named Argus.Recon. Define Counter<long> instruments: AssetsDiscovered (tagged assets), AssetsConfirmed (assets), RateLimitDelays (requests), TasksCompleted (tasks). Define Histogram<double> instruments: TaskDuration (ms), ScopeValidationDuration (ms). Define UpDownCounter<int>: TasksRunning (tasks). Tag ALL instruments with: program_id, worker_type, asset_type, registered_domain. Wire into Aspire dashboard and any OTLP-compatible backend (Grafana, Honeycomb, Datadog). File: src/BuildingBlocks/Argus.BuildingBlocks.Observability/ArgusMetrics.cs"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "048")).description = "Enhancement #12: Worker graceful draining on SIGTERM - In ArgusWorkerBackgroundService, override StopAsync to honour a configurable drain timeout. Read ARGUS_WORKER_DRAIN_TIMEOUT env var (default 60s). On StopAsync: signal no new tasks, wait for in-flight tasks up to drain timeout. Support checkpoint writing before yielding to cancellation. Update deploy/compose.yaml with stop_grace_period: 90s on workers. File: src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusWorkerBackgroundService.cs"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "049")).description = "Enhancement #13: FindingCandidate promotion workflow - When a worker produces a FindingCandidate asset (interesting score 70), AssetService emits FindingCandidateCreated event. Build a ValidationWorker or manual review: promote to Finding (new enum value + severity/CVSS fields) or dismiss with reason and Archived status. Add Finding to AssetType enum. Files: src/Services/Argus.AssetService, src/BuildingBlocks/Argus.Contracts, src/Workers/Argus.Workers.Validation"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "050")).description = "Enhancement #15: Scheduled/recurring scan plans - Add CronExpression field to ScanPlanDto. Create RecurrencePolicy entity. Build ScanSchedulerBackgroundService checking for due recurrences every 60s, auto-creates scan plans from template. Track LastRunAt and NextRunAt. Use NCrontab or Cronos NuGet library (lightweight, no external deps). File: src/Services/Argus.ScanOrchestratorService/"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "051")).description = "Enhancement #16: Asset graph traversal API - Add GET /assets/{assetId}/subgraph?maxDepth=3&assetTypes=Subdomain,Url to AssetService. PostgreSQL recursive CTE via Dapper: WITH RECURSIVE reachable AS (SELECT to_asset_id FROM asset_relationships WHERE from_asset_id = rootId UNION ALL SELECT r.to_asset_id FROM asset_relationships r JOIN reachable ON r.from_asset_id = reachable.to_asset_id) SELECT * FROM assets WHERE asset_id IN (SELECT to_asset_id FROM reachable). MaxDepth param controls recursion. AssetTypes param filters results. File: src/Services/Argus.AssetService/"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "052")).description = "Enhancement #18: Program/scope export and import - Add GET /programs/{programId}/export returning JSON with program, scopes, exclusions, rules, rate limit policies. Add POST /programs/import to reconstruct full config. Enables backup/restore, sharing scope templates, migration between environments, and a scope library for common bug bounty platforms. File: src/Services/Argus.ProgramScopeService/"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "053")).description = "Enhancement #19: Webhook notifications - Add WebhookService with configurable targets: url, event-type filters (FindingCandidateCreated, AssetConfirmed), filters (programId, minInterestingScore). Fire via SSE subscription mechanism. Retry with exponential backoff. Store webhook configs in PostgreSQL. Add UI for CRUD on webhook configs. File: src/Services/Argus.RealtimeService/"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "054")).description = "Enhancement #20: Scope ingestion from HackerOne/Bugcrowd APIs - Add IScopeProvider interface with ProviderName and FetchAsync(programHandle, ct) in ProgramScopeService. Implement HackerOneScopeProvider and BugcrowdScopeProvider using their public programme scope APIs. Run scheduled sync every 6h via BackgroundService. On scope change publish ProgramScopeChanged. API keys from env: HACKERONE_API_TOKEN, BUGCROWD_API_TOKEN. File: src/Services/Argus.ProgramScopeService/Providers/"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "055")).description = "Enhancement #21: Bulk asset tag and enqueue endpoints with UI - Wire up POST /assets/bulk/tag (BulkTagRequest) and POST /assets/bulk/enqueue (BulkEnqueueRequest). DTOs and store interfaces already defined in AssetContracts.cs. Add UI multi-select in asset grid with contextual tag/enqueue actions. Enables workflows like: select 40 subdomains, tag reviewed or enqueue fingerprint scan on all alive-tagged. Prerequisite: deduplication is done (Design #22 completed). Files: src/Services/Argus.AssetService, src/BuildingBlocks/Argus.Contracts"

' /tmp/agent-tasks-partial.json > /tmp/agent-tasks-partial2.json && mv /tmp/agent-tasks-partial2.json /tmp/agent-tasks-partial.json

jq '
(.tasks[] | select(.id == "056")).description = "Enhancement #22: Asset staleness scoring - Add StalenessScore (int 0-100) to AssetDto computed from (now - LastSeenAt) relative to expected scan frequency for asset type. UI visual indicator (colour band or age column). Add minStalenessScore/maxStalenessScore to AssetQuery for filtering. ScanSchedulerService uses StalenessScore as primary scheduling signal - highest staleness first. Files: src/Services/Argus.AssetService, src/BuildingBlocks/Argus.Contracts"

' /tmp/agent-tasks-partial.json > "$STATE_FILE"

echo "Done - backup at ${STATE_FILE}.bak"