# AI Agent Start Here

## Overview
When an AI agent begins work on this repository, it must first check the hidden `.ai` folder. This folder contains coordination files that let multiple agents work concurrently without stepping on each other's toes.

## 1. Check for Other Agents
- Look for existing sub‑folders under `.ai/home`. Each folder name corresponds to an active agent model (e.g., `claude-code`, `agent-2`, `agent-3`).
- Review the `communications.md` file in each agent's folder to see any pending messages or ongoing discussions.
- Examine `.ai/file-locks.json` to see which files are currently locked and by which agent.

## 2. File‑Locking System
- The file‑locks are stored in `.ai/file-locks.json`. The JSON has a top‑level `description` field and a `locks` object.
- **Before modifying any project file**:
  1. Add an entry under `locks` with the absolute path, a `lockedBy` field set to your agent identifier, a short `reason`, and a timestamp.
  2. Save the change so other agents can see the lock.
- **After finishing the edit**:
  1. Remove your entry from `locks`.
  2. Optionally add a note in your `communications.md` describing what was changed.

## 3. Per‑Agent Home Directory (`.ai/home/<agent-id>`)
Each agent gets its own folder with four core documents:
- `context.md` – store persistent notes, concepts, and any context the agent wants to remember for future tasks.
- `communications.md` – asynchronous log between the user and the agent. Append messages; the other party replies by appending another line.
- `project-architecture.md` – capture insights about the project's architecture, component relationships, design decisions, and any new discoveries.
- `rules.md` – standard operating procedures. The default rules (included in each folder) are:
  - Keep these documents up to date.
  - Always acquire a lock via `.ai/file-locks.json` before editing any project file.
  - Respect other agents' work; check `.ai` for lock entries and communications before editing.
  - Append to the communications file for both user and agent messages.
  - Update the project‑architecture file with any new architectural insight.
  - Release your lock when the edit is finished.

## 4. Communication Protocol
- Both the user and an agent can **append** to `communications.md`. Use a simple line format, e.g.:
```text
[2026-05-28T10:15:00Z] User: Please add logging to X.
[2026-05-28T10:16:05Z] claude-code: Added logging to X.
```
- Do **not** rewrite the file; just add new lines.

## 5. Updating the Architecture Document
When you discover something about the system (e.g., a new service, a data flow pattern, a dependency), add a concise entry to `project-architecture.md`. Use headings or bullet points to keep the document searchable.

## 6. General Rules (also duplicated in each agent's `rules.md`)
1. Never edit a file without first acquiring a lock.
2. Never remove another agent's lock unless you have explicit permission from that agent.
3. Always check the communications logs before starting work to avoid duplicate effort.
4. Keep all `.ai` documents up to date; they are the single source of truth for coordination.
5. Be courteous to other agents – if you notice a stale lock, leave a note in that agent's `communications.md` asking if the lock can be released.

---
*These guidelines ensure safe, collaborative, multi‑agent development on the Omnopticon project.*
