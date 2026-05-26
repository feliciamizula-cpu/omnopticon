# AI_START_HERE - Collaborative Agent Guidelines

## Overview
Welcome, AI agent. This document establishes protocols for multi-agent collaboration on the Omnopticon project. All participating agents MUST follow these guidelines to ensure safe, coordinated work.

---

## Central File Locking System

### Purpose
Prevent simultaneous conflicting edits to the same files by multiple agents.

### Lock File Location
`./.agents/locks/`

### Lock File Naming Convention
`{agent-id}_{filename}.lock`

Example: `agent-alpha_Argus.Web.csproj.lock`

### Locking Protocol
1. **Before editing any file**, an agent MUST:
   - Check if a lock file exists for that file
   - If a lock exists, read it to identify the current holder
   - Wait for the lock to be released OR coordinate with the holding agent

2. **Creating a Lock**
   ```bash
   echo "AGENT_ID:{your-agent-id}|TIMESTAMP:{ISO8601}|TASK:{task-description}" > .agents/locks/{agent-id}_{filename}.lock
   ```

3. **Releasing a Lock**
   ```bash
   rm .agents/locks/{agent-id}_{filename}.lock
   ```

4. **Lock Timeout**
   - Locks older than 30 minutes are considered stale and can be overridden
   - Always verify the lock timestamp before overriding

### Critical Section Files
The following file types require locking:
- `.cs`, `.csproj`, `.sln` files
- `appsettings.json`, `*.env`
- Source code in `/src/`

---

## Central Logging Repository

### Log File Location
`ai_logs.txt` (in project root)

### Log Format
Every log entry MUST follow this structure:
```
[TIMESTAMP] | AGENT_ID: {id} | TASK: {task-name} | STATUS: {status} | MESSAGE: {message}
```

### Required Log Entries
1. **Agent Startup**: Log when you begin work, including your agent ID, task, and current status
2. **File Locks**: Log when acquiring or releasing locks
3. **Milestones**: Log significant progress points
4. **Errors**: Log any errors or blockers encountered
5. **Completion**: Log when tasks are finished

### Example Log Entries
```
[2026-05-26T10:30:00Z] | AGENT_ID: alpha | TASK: FixAuthBug | STATUS: STARTED | MESSAGE: Beginning investigation of auth token refresh issue
[2026-05-26T10:31:15Z] | AGENT_ID: alpha | TASK: FixAuthBug | STATUS: LOCKING | MESSAGE: Acquired lock on AuthService.cs
[2026-05-26T10:35:22Z] | AGENT_ID: alpha | TASK: FixAuthBug | STATUS: PROGRESS | MESSAGE: Identified root cause - missing null check
[2026-05-26T10:40:00Z] | AGENT_ID: alpha | TASK: FixAuthBug | STATUS: COMPLETED | MESSAGE: Fix deployed to staging, commit abcd123
```

### Verbosity Requirements
- Be **verbose** and **frequent** with updates
- Provide identifying information about who you are and your task
- Include current actions and statuses
- Write at least one log entry every 5-10 minutes of work
- When in doubt, log more rather than less

---

## Agent Identification

### Agent ID Format
`{role}-{unique-id}`

Examples: `frontend-agent-1`, `backend-agent-alpha`, `devops-agent-2`

### First Action
When starting work, announce yourself:
```
[TIMESTAMP] | AGENT_ID: {your-id} | TASK: {your-task} | STATUS: STARTED | MESSAGE: Agent initialized, beginning work on {task description}
```

---

## Coordination Protocol

### Before Starting Work
1. Read `ai_logs.txt` to see what other agents are doing
2. Check `.agents/locks/` for active locks
3. Post your startup log entry

### When Blocked
1. Log the blocker with STATUS: BLOCKED
2. Specify what you need (file lock, information, decision)
3. Wait or request human assistance

### When Discovering Conflicts
1. Log the conflict with STATUS: CONFLICT
2. Communicate with the other agent
3. Do NOT override locks without attempting coordination first

---

## Log Viewer Console Application

### Purpose
Real-time visualization of agent activity logs.

### Usage
```bash
cd tools/AgentLogViewer
dotnet run
```

### Features
- Auto-refreshes every 2 seconds
- Shows all agent activity in chronological order
- Displays agent ID, task, status, and messages
- Color-coded status indicators

---

## File Modifications

### Always Update Logs When:
- Starting a new task
- Acquiring/releasing any lock
- Making significant progress
- Encountering errors
- Completing a task
- Deploying to staging

### Commit Messages
Include task and agent ID in commit messages:
```
[TASK:{task-name}] {brief description}
```

---

## Safety Rules

1. **NEVER** edit locked files without coordination
2. **ALWAYS** log before and after critical actions
3. **ALWAYS** release locks when done
4. **NEVER** deploy broken code to staging
5. **ALWAYS** verify changes before committing

---

## Quick Reference

| Action | Command/Format |
|--------|---------------|
| Create lock | `echo "AGENT_ID:...|TIMESTAMP:...|TASK:..." > .agents/locks/{id}_{file}.lock` |
| Release lock | `rm .agents/locks/{id}_{file}.lock` |
| Log activity | Append to `ai_logs.txt` with required format |
| Check locks | `ls .agents/locks/` |
| Check logs | `tail -f ai_logs.txt` or use AgentLogViewer |

---

*Last updated: 2026-05-26*
*All agents must read this document before beginning work*