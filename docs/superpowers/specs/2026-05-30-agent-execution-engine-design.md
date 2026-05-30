# Agent Execution Engine — Design

**Date:** 2026-05-30
**Author:** claude-code (brainstormed with Derek Perez)
**Status:** Approved — ready for implementation planning
**Supersedes:** `.ai/design-docs/agent-execution-engine-design.md` (2026-05-29 PAUSED handoff)

## Goal

Make configured agents in Omnopticon actually run on a schedule (or on demand, or via trigger events), invoking their configured CLI/model, with role-based capability enforcement and per-runtime safety. End-to-end: an agent defined in the Agents page must be visibly executing tasks, producing reports, todos, code reviews, and (when authorized) git commits, all visible in the web UI.

## Decisions summary

- **Drop the "Schedules" concept as a separate entity.** Agent Tasks ARE scheduled actions. The `/agent-schedules` page is removed.
- **Tasks have 0..N schedules.** A task may run on multiple crons OR have no schedule (on-demand only). Any task, scheduled or not, can be manually triggered from the UI.
- **Capabilities are role-based and granular.** Agents have an explicit capability allow-list. Some agents must have full developer access (commit, push, push-to-main, history rewrite); nothing short of GitHub repo deletion is permitted.
- **Hybrid runtime.** Capability tier picks the runtime: read-only / app-only writes run **in-pod**; git / kubectl-apply / build operations run in an **ephemeral isolated workspace** (K8s Job in production, `git worktree` in local dev). Per-task override available as escape hatch.

## Data model

### Agent (extends existing)
Existing fields preserved. New:
- `Capabilities` (string[]) — granular allow-list (see capability table below). Stored as JSON.
- `DefaultRuntime` (`in_pod` | `workspace`) — derived from capabilities at seed time; user-overridable.

### AgentTask (rework)
Existing: `TaskId`, `Description`, `Priority`, `Status`, `AssignedTo`, `TargetRole`, `TaskType`, `RecoveryContext`, `Attempts`.

Changes:
- `ScheduleExpression` → **removed** (moved to `AgentTaskSchedule` rows).
- `TriggerEvent` → **moved** to `AgentTaskTrigger` rows (1:N) for symmetry.
- `Description` becomes the short label.
- **New** `Instructions` (text) — the full prompt body.
- **New** `Runtime` (`in_pod` | `workspace` | `default`) — `default` means "use the agent's capability-derived runtime."
- **New** `RequiredCapabilities` (string[]) — used by the router to filter eligible agents.
- **New** `EnabledAt` / `DisabledAt` — pause without deleting.

### AgentTaskSchedule (new)
1:N off task.
- `ScheduleId`, `TaskId`, `Cron` (string), `Enabled` (bool), `NextRunAt`, `LastRunAt`, `Label` (optional, e.g., "weekday mornings").
- A task with 0 schedules is on-demand-only.
- A task with N schedules fires independently on each cron.

### AgentTaskTrigger (new)
1:N off task.
- `TriggerId`, `TaskId`, `EventName` (string, e.g., `agent_task_completed`, `code_commit_pushed`, `health_check_failed`), `Enabled`.

### AgentTaskRun (new — history)
- `RunId`, `TaskId`, `ScheduleId?` (null if manual), `TriggerId?` (null if not trigger-driven), `AgentId` (selected at fire time), `StartedAt`, `CompletedAt`, `Status` (`pending` | `running` | `completed` | `failed`), `Runtime` (which path actually ran), `Output`, `Error`, `WorkspaceRef` (e.g., K8s Job name), `CapabilitiesSnapshot` (string[] — agent capabilities at fire time, for audit).
- Source of truth for "event trail" / per-task history in the UI.
- Also used to track in-flight executions and rate-limit per agent.

## Execution engine

### Components (in `Argus.AgentService`)

- **`AgentExecutionService`** (replaces today's `TaskExecutionService`)
  Single entry point: `ExecuteAsync(taskId, scheduleId?, triggerId?, triggerSource)`.
  Flow: create `AgentTaskRun` (status=`pending`) → select agent via `AgentSelectionService` (extended to respect `Capabilities` ⊇ task's `RequiredCapabilities`) → resolve runtime (task override → agent default → capability fallback) → dispatch to `IRuntimeAdapter` → write back run output, update task `LastRunAt`, compute each schedule's `NextRunAt`.

- **`IRuntimeAdapter`** — two implementations:

  1. **`InPodRuntimeAdapter`** — wraps the existing `CliChatClient`. Spawns the CLI as a subprocess inside agent-service. Strict timeout (default 120s, per-agent configurable). Captures stdout/stderr. No git, no shell, no filesystem outside `/tmp/agent-{runId}/`.

  2. **`WorkspaceRuntimeAdapter`** — provisions an ephemeral isolated workspace:
     - **Production (K8s):** creates a `Job` from a template (`argus-agent-runner` image with CLIs + git baked in). Mounts a `ConfigMap` (prompt + instructions) and a `Secret` (scoped GitHub PAT, scoped kubeconfig). Job: clone fresh `origin/main` into `/work` → run CLI → run `dotnet build` if any C# files changed (build-gate) → commit + push if build passes. Reports status back via `POST /agents/runs/{runId}/result` callback.
     - **Local dev (no K8s):** falls back to `git worktree add /tmp/agent-{runId} origin/main`, runs CLI inline, same build-gate.
     - Hard limits: 15-minute wall clock default (configurable per agent), bare `git push --force` denied at runner image level, repo deletion APIs unavailable (PAT scoped without `delete_repo`).

- **`TaskSchedulerService`** (existing background loop, reworked)
  Polls every 10s (configurable via `TASK_SCHEDULER_INTERVAL_SECONDS`). On each tick:
  - Queries `AgentTaskSchedule` rows where `Enabled = true` AND `NextRunAt <= now()`.
  - For each due schedule: enqueue execution and **immediately** compute the next `NextRunAt` (prevents re-firing on the next tick if execution is slow).
  - Drains pending one-off `AgentTaskRun` rows (manually triggered from UI).
  - Staggers within a tick to respect provider rate limits (configurable inter-run delay).

- **Trigger dispatch**: `AgentTaskTrigger` rows fire when a matching domain event lands on the integration event bus. Reuses existing `Argus.BuildingBlocks.EventBus`.

### Failure handling

- Run fails → `AgentTaskRun.Status = failed`, `Error` set, agent `LastError` updated.
- Schedule retries: configurable per agent (`max_attempts`, `backoff_seconds`). After max attempts: the **schedule** keeps firing on its cron (we don't dead-letter a recurring task because of transient errors), but each failed run is recorded.
- For one-offs: after max attempts the task transitions to `failed` and stays there.

## Capabilities, roles, safety

### Capability allow-list

| Capability | What it grants | Default runtime |
|---|---|---|
| `read_app_state` | Read-only access to app DB (assets, tasks, todos, logs, system reports) via a scoped HTTP client | `in_pod` |
| `read_external` | HTTP GET against allow-listed hosts (GitHub API, Grafana, k8s read API) | `in_pod` |
| `write_todos` | Create/update todos via `/todos` API | `in_pod` |
| `write_tasks` | Create/update agent tasks via `/agent-tasks` API | `in_pod` |
| `write_reports` | Append system reports / code reviews | `in_pod` |
| `git_read` | Clone, fetch, diff, blame | `workspace` |
| `git_commit` | Commit on a feature branch in the workspace | `workspace` |
| `git_push` | Push to GitHub (any branch). Implies `git_commit`. | `workspace` |
| `git_push_main` | Push directly to `main`. Implies `git_push`. Highest privilege absent history rewrite. | `workspace` |
| `git_history_rewrite` | Interactive rebase, amend, squash, cherry-pick. Plus `git push --force-with-lease` (bare `--force` stays blocked). Implies `git_commit` and `git_push_main`. | `workspace` |
| `dotnet_build` | Run `dotnet build` / `dotnet test` in the workspace as build-gate | `workspace` |
| `kubectl_read` | `kubectl get` / `describe` with read-only RBAC | `in_pod` |
| `kubectl_apply` | `kubectl apply` with deployment-scoped RBAC | `workspace` |

### Intentional non-capabilities (impossible, not just disallowed)

- **Repo deletion** — runner image's PAT is scoped without `delete_repo`. GitHub refuses even if attempted.
- **Bare force-push** — runner image ships a `pre-push` hook that rejects `--force` / `--force-with-lease` for any agent without `git_history_rewrite`; for agents with that capability, bare `--force` stays blocked, only `--force-with-lease` is permitted.
- **Secret exfil** — runner has no inbound network from outside its namespace; stdout is captured and stored alongside the run (auditable).

### Role → capability presets (seed defaults, fully editable per-agent in UI)

| Role | Default capabilities |
|---|---|
| junior_developer | `read_app_state`, `write_todos`, `git_read`, `git_commit`, `git_push`, `dotnet_build` (no `git_push_main`) |
| developer | + `git_push_main` |
| senior_developer | same as developer |
| junior_system_architect | `read_app_state`, `read_external`, `write_reports`, `write_todos`, `write_tasks` |
| senior_system_architect | + `git_read`, **`git_history_rewrite`** |
| junior_devops | `read_app_state`, `kubectl_read`, `write_reports`, `write_todos` |
| senior_devops | + `kubectl_apply`, `git_read`, `git_commit`, `git_push` |
| devops_architect | + `git_push_main` |

### Selection respects capabilities

When a task is auto-routed, `AgentSelectionService` filters to agents whose capability set is a superset of the task's `RequiredCapabilities`. The router picks the lowest-priority (cheapest) qualifying agent. Explicit `AssignedTo` bypasses routing but execution still fails-fast if the assigned agent lacks the required capabilities (recorded on the run).

### Extra guardrails for `git_history_rewrite`

- `AgentTaskRun.Output` captures pre- and post-rewrite SHAs of any branch touched, so audit + `git reflog` recovery are straightforward.
- A history-rewriting run automatically posts a notice to `.ai/home/claude-code/communications.md` so other agents in the shared tree know to re-fetch.
- Concurrency lock: only one `git_history_rewrite` run can be active at a time across all agents (prevents racing rewrites of the same branch).

## UI changes (`Argus.Web`)

### Remove
- `Pages/AgentSchedules.razor` and its `/agent-schedules` route. Sidebar nav entry removed. The agent-schedules concept is folded into Agent Tasks entirely.

### `Pages/Agents.razor` (extend)
- **Capabilities editor**: checkbox grid keyed off the capability list above. Tooltip per capability explains what it grants and which runtime it implies. Inline warning when granting `git_push_main` or `git_history_rewrite`.
- **Default runtime selector**: read-only display when fully derived from capabilities; editable toggle to override.
- **Recent runs tab** (per agent): table of last N `AgentTaskRun` rows for this agent. Status pill, duration, output preview, link to full output.
- **Seed reseed**: capabilities populated from the role preset table on initial creation; persisted thereafter.

### `Pages/AgentTasks.razor` (extend)
- Split **Description** (label) from **Instructions** (full prompt body).
- **Schedules sub-editor**: 1:N rows with `Cron`, `Label`, `Enabled` toggle, `NextRunAt` (display-only). Add / remove rows. Validates each cron expression.
- **Triggers sub-editor**: 1:N rows with `EventName`, `Enabled` toggle. Dropdown of known events.
- **Required Capabilities** multi-select.
- **Runtime override**: `default | in_pod | workspace` radio.
- **Runs history pane**: replaces the current ad-hoc "event trail" with a proper read of `AgentTaskRun`. Status, started/completed, runtime used, agent selected, output preview, link to full output. "Run Now" button creates a `AgentTaskRun` with `ScheduleId=null` and dispatches immediately.

### Realtime
Reuse `DevelopmentRealtimeClient` channels: `agents`, `agent-tasks`. New channel `agent-task-runs` for live run status updates.

### Seed adjustments
Existing seeded scheduled tasks (`sched-log-review`, `sched-junior-pick-task`, etc.) keep working — migration converts each task's single `ScheduleExpression` to one `AgentTaskSchedule` row, and `TriggerEvent` to one `AgentTaskTrigger` row.

## Migration

EF Core migration:
- `AgentRecord`: add `Capabilities` (jsonb), `DefaultRuntime` (text).
- `AgentTaskRecord`: add `Instructions` (text), `Runtime` (text default `'default'`), `RequiredCapabilities` (jsonb), `EnabledAt`, `DisabledAt`. Drop `ScheduleExpression`, `TriggerEvent` after data move.
- `AgentTaskSchedule` (new table): `ScheduleId` (uuid pk), `TaskId` (fk), `Cron`, `Enabled`, `NextRunAt`, `LastRunAt`, `Label`.
- `AgentTaskTrigger` (new table): `TriggerId` (uuid pk), `TaskId` (fk), `EventName`, `Enabled`.
- `AgentTaskRun` (new table): per spec above.
- Data move: for each existing `AgentTaskRecord` with `ScheduleExpression` non-null, insert a corresponding `AgentTaskSchedule` row. Same for `TriggerEvent` → `AgentTaskTrigger`. Then drop the old columns.
- Idempotent (per the existing `SchemaDatabaseInitializer` pattern from May 29 — tables created with `IF NOT EXISTS`, columns added with `ADD COLUMN IF NOT EXISTS`).

## Infrastructure

### `argus-agent-runner` container image (new)
- Base: same .NET 8 runtime as agent-service.
- Installed: `git`, `dotnet-sdk-8.0`, `kubectl`, `claude`, `opencode`, `codex`, `gemini`, `openai` CLIs (versions pinned).
- Pre-push hook (system-wide) that:
  - Rejects bare `--force` always.
  - Rejects `--force-with-lease` if `AGENT_CAPABILITIES` env doesn't contain `git_history_rewrite`.
- Runs as non-root, no inbound network, scoped PAT mounted as secret.

### K8s wiring
- New `ServiceAccount` `argus-agent-runner` with permissions: create/get/delete its own Job resources, get/list/watch app pods, optionally `apply` Deployment if `kubectl_apply` capability is granted to the launching agent.
- `RoleBinding` per capability scope.
- Network policy: agent-runner pods may egress to GitHub API + provider model APIs only.

### Local dev fallback
- `WorkspaceRuntimeAdapter` detects absence of K8s (`KUBERNETES_SERVICE_HOST` env var unset) and falls back to `git worktree add /tmp/agent-{runId} origin/main`, runs CLI inline, same build-gate. Cleanup on completion.

## Testing strategy

- **Unit**: capability gating, cron NextRunAt calc, runtime resolution logic.
- **Integration**: `InPodRuntimeAdapter` against a mock IChatClient; `WorkspaceRuntimeAdapter` against the local worktree fallback (no K8s in CI).
- **E2E (staging)**:
  1. Seed runs persist; verify each scheduled task fires within one cron interval.
  2. Manual "Run Now" produces a visible `AgentTaskRun` row within 30s.
  3. A `read_app_state`-only agent attempting `git_commit` is blocked at capability check, run marked failed with explicit error.
  4. A `developer`-tier agent commits a trivial code change (e.g., bumps a comment) via the workspace runtime; verify the workspace, build-gate, commit, and push all happen and the commit lands on `main`.

## Blockers / open infra work (call out before implementation)

1. **`argus-agent-runner` image** needs to be built and registered in the deployment pipeline. New CI step + new container image registry entry.
2. **Scoped GitHub PAT** must be provisioned (no `delete_repo`, scoped to the omnopticon repo). Stored as K8s Secret.
3. **Scoped kubeconfig** for `kubectl_apply` — narrowest RBAC that still lets devops agents update Deployments.
4. **Existing shared working tree problem** (per AI protocol memory): developer agents MUST use isolated workspaces; never `git add -A` or commit in the shared tree. This is enforced by the workspace adapter never running in agent-service's working directory.

## Success criteria

- Agents page shows all 27 seed agents, each with editable capabilities.
- An admin can create a new agent task with multiple cron schedules and one or more triggers.
- A scheduled task fires within its cron interval and a corresponding `AgentTaskRun` appears in the UI within seconds.
- "Run Now" works for both scheduled and on-demand tasks.
- A `senior_system_architect` agent successfully rebases a feature branch via `git_history_rewrite` capability; pre- and post-SHAs captured in the run output.
- A `developer` agent's commit lands on `main` in staging after build-gate passes.
- A junior developer agent without `git_push_main` is blocked when assigned a task requiring it; the run records the capability mismatch.
