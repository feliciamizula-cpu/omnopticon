# Agent Execution Engine — Design & Handoff

**Date:** 2026-05-29
**Author:** claude-code
**Status:** PAUSED / handoff — design agreed, implementation not started (blocked on CLI backend)

## Goal

Make the app's **configured agents actually run**: on a schedule, execute each configured
agent via its chosen CLI/model to perform intermittent duties — checking deployment health,
scanning logs for errors, sanity-checking app state, **creating todos** from findings, and (for
developer agents) **implementing todos**. Origin: `.ai/home/claude-code/special.txt` +
follow-ups asking to integrate with the agent configuration page and support configurable CLIs.

## Current state (what exists vs. what's missing)

- **Agent configuration EXISTS in-app.** `AgentDto` / `CreateAgentRequest`
  ([src/Contracts/Argus.Contracts/Agents/AgentContracts.cs](../../src/Contracts/Argus.Contracts/Agents/AgentContracts.cs))
  already carry `Tool` (the CLI), `Model`, `Provider`, `Role`, `RoleDescription`,
  `Responsibilities[]`, `Priority`, `Status`. Agent-service persists these in Postgres and the
  Agents page edits them. **So defining an agent with a CLI + model is already possible.**
- **Agent EXECUTION does NOT exist.** Nothing reads those configs and runs the agents. This is
  the whole gap.
- **Todos API exists and is now persisted.** `CreateTodoRequest(Name, Instructions, Priority,
  WorkerType, Deadline)`; update status via `UpdateTodoRequest.Status`; visible on the Todos page.
  BFF: `POST /ui/todos`, `GET /ui/todos`, `PUT /ui/todos/{id}`.
- **CLI backend WORKS (blocker resolved).** `opencode` 1.15.10 is installed. The model id in
  special.txt, `nvidia/miniai/minimax-m2.7`, was a **typo** (`miniai`); the correct id is
  **`nvidia/minimaxai/minimax-m2.7`**, which is verified working:
  `opencode run --model nvidia/minimaxai/minimax-m2.7 "Reply with exactly: PONG"` → `PONG` (exit 0,
  ~first call warms up so allow a generous timeout). The model must be **configurable per agent**
  (use the existing `Model` field) — do not hardcode it.

## Decisions made (this session)

1. **Shape:** in-app **hosted service** (a `BackgroundService` on a timer), not a standalone cron
   script. Lives in agent-service (where agent configs already are).
2. **Configurable CLIs:** opencode, codex, claude, … selected per-agent via the existing `Tool`
   field; `Model`/`Provider` also per-agent. Invocation must be a pluggable abstraction.
3. **Todos:** created/updated via the existing todos API so they show in the web UI.
4. **Stagger execution** to respect rate limits (do NOT fire all agents at once).
5. **Reporting:** write per-run reports to `.ai/home/claude-code/monitoring/`; append a one-line
   alert to `.ai/home/claude-code/communications.md` only when something is wrong.
6. **Agent powers differ by role:** read-only monitors; todo-creators; and **developer agents that
   implement todos and commit straight to `main`** (user's explicit choice).

## Proposed design

### Components (in agent-service)
- `AgentExecutionBackgroundService : BackgroundService` — timer loop (interval configurable). Each
  tick: load **active** agents, then run them **staggered** (delay between each).
- `ICliRunner` abstraction with one implementation per CLI:
  - `OpenCodeCliRunner` → `opencode run --model <model> <prompt>`
  - `CodexCliRunner`, `ClaudeCliRunner` → analogous, command templates configurable.
  - Selected by the agent's `Tool`. Each returns stdout (+ exit code) with a timeout.
- `IAgentRole` behaviors keyed off `Role`/`Responsibilities`:
  - **monitor** (health / logs / state): collect context (kubectl/curl), prompt the CLI to assess,
    parse a machine-readable verdict (`VERDICT: OK` | `VERDICT: ISSUE | title | instructions`),
    write a report, and create a todo on ISSUE.
  - **developer**: pick one open todo, run the CLI **in an isolated git worktree** off fresh
    `origin/main`, **build-gate** (`dotnet build` must pass), then commit + push to `main`
    (per the user's choice), and mark the todo completed. The worktree isolation is essential so
    it never commits other agents' in-progress work in the shared tree.

### Configuration (per-agent + global)
- Per agent (already in the model): `Tool`, `Model`, `Provider`, `Role`, `Responsibilities`,
  `Priority`, `Status` (only `active` agents run).
- Global (new settings): tick interval, stagger delay, per-CLI command templates + timeouts,
  developer mode (`commit` | `pr` | `draft`), enable/disable switch.

### Safety
- Monitors are strictly read-only.
- Developer agents: isolated worktree + build-gate + bounded to one todo/run; `commit` mode is the
  user's choice but a `pr`/`draft` mode should be available as an escape hatch. Requires git creds
  + the CLI + a repo checkout in the runtime image — see blockers.

## Blockers / open questions

1. ~~CLI backend must work.~~ **RESOLVED** — `opencode run --model nvidia/minimaxai/minimax-m2.7`
   returns output (verified `PONG`). Keep the model/CLI configurable per agent.
2. **Developer-in-production runtime.** A developer agent that builds + commits needs: the CLI
   installed in the agent-service image, git credentials, and a repo checkout with network egress
   to GitHub. That's a meaningful infra addition (and a security consideration — autonomous,
   unreviewed commits to `main` auto-deploy via CD).
3. **Shared-tree safety.** Multiple agents (human + AI) are actively editing this repo; the
   developer role must use an isolated worktree/clone and never `git add -A` in the shared tree.
4. **Scope of "duties".** Confirm the exact monitor checks and how aggressively developer agents
   should pick up todos.

## Concrete next steps (when resumed)
1. ~~Unblock a CLI/model.~~ Done — use `nvidia/minimaxai/minimax-m2.7` via `opencode run` (keep configurable).
2. Add `ICliRunner` + `OpenCodeCliRunner` with a 1-call smoke test.
3. Add `AgentExecutionBackgroundService` (disabled by default) with the monitor role only;
   verify it reads active agents, staggers, writes a report, and creates a todo.
4. Add the developer role last, behind the worktree + build-gate, defaulting to `pr`/`draft`
   until trusted, then `commit` per the user's preference.

## Reference
- Verified model: `nvidia/minimaxai/minimax-m2.7` (returns output via `opencode run`). Full list
  via `opencode models` (e.g. `opencode-go/glm-5.1`, `opencode-go/kimi-k2.6`,
  `opencode-go/qwen3.7-max`, plus `opencode/*-free` tiers). Keep per-agent configurable.
- Todo contract: [TodoContracts.cs](../../src/Contracts/Argus.Contracts/Agents/TodoContracts.cs).
- Agent contract: [AgentContracts.cs](../../src/Contracts/Argus.Contracts/Agents/AgentContracts.cs).
