#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/agent-state-lib.sh"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
AGENT_COORD="$SCRIPT_DIR/agent-coord.sh"
INTERVAL="${1:-5}"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OPENCODE_BIN="${OPENCODE_BIN:-opencode}"
LOG_FILE="${LOG_FILE:-/tmp/auto-run-agents.log}"
AGENT_HEARTBEAT_TIMEOUT="${AGENT_HEARTBEAT_TIMEOUT:-60}"
AGENT_STALE_TIMEOUT="${AGENT_STALE_TIMEOUT:-120}"
RECONCILE_INTERVAL="${RECONCILE_INTERVAL:-30}"

AGENTS=("agent-1" "agent-2" "agent-3" "agent-4" "agent-5")
DEVOPS_AGENTS=("devops-1" "devops-2")
REVIEW_AGENTS=("reviewer-1" "reviewer-2")
MAX_CONCURRENT="${MAX_CONCURRENT:-5}"
MAX_CONCURRENT_DEVOPS="${MAX_CONCURRENT_DEVOPS:-2}"
MAX_CONCURRENT_REVIEWERS="${MAX_CONCURRENT_REVIEWERS:-2}"
DEVOPS_AGENT_INTERVAL="${DEVOPS_AGENT_INTERVAL:-120}"

log() { echo "[$(date +'%Y-%m-%dT%H:%M:%S')] $*" >> "$LOG_FILE"; }

git_pull() {
    log "Pulling latest changes from origin..."
    if git -C "$WORK_DIR" pull origin main 2>&1 | tee -a "$LOG_FILE"; then
        local pull_time
        pull_time="$(agent_now_utc)"
        if [ -f "$STATE_FILE" ]; then
            jq ".lastPullAt = \"$pull_time\"" "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
        fi
        log "Pull completed at $pull_time"
        return 0
    fi

    log "Pull failed - continuing with local state"
    return 1
}

get_modified_files() {
    git -C "$WORK_DIR" status --porcelain 2>/dev/null | grep -v "^??" | awk '{print $2}' | grep -v "^tools/\.agent" | grep -v "^tools/\.agent-prompt" | sort
}

get_recent_commits() {
    git -C "$WORK_DIR" log --oneline -10 2>/dev/null
}

get_last_pull_time() {
    if [ -f "$STATE_FILE" ]; then
        jq -r '.lastPullAt // "never"' "$STATE_FILE"
    else
        echo "never"
    fi
}

get_task_description() {
    local task_id="$1"
    jq -r ".tasks[] | select(.id == \"$task_id\") | .description // empty" "$STATE_FILE"
}

get_task_status() {
    local task_id="$1"
    jq -r ".tasks[] | select(.id == \"$task_id\") | .status // \"pending\"" "$STATE_FILE"
}

get_task_assignee() {
    local task_id="$1"
    jq -r ".tasks[] | select(.id == \"$task_id\") | .assignedTo // empty" "$STATE_FILE"
}

get_monitoring_task_for_agent() {
    local agent_id="$1"
    jq -r --arg agent "$agent_id" '
        [.tasks[] | select(.status == "monitoring" and .assignedTo == $agent)] |
        sort_by(.id) |
        .[0].id // empty
    ' "$STATE_FILE"
}

count_tasks() {
    local status_filter="$1"
    jq "[.tasks[] | select(.status == \"$status_filter\")] | length" "$STATE_FILE"
}

read_state() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE"
    else
        echo "null"
    fi
}

is_agent_active() {
    local agent_id="$1"
    if [ ! -f "$STATE_FILE" ]; then
        return 1
    fi
    local status
    status="$(jq -r ".agents[] | select(.id == \"$agent_id\") | .status // \"inactive\"" "$STATE_FILE" 2>/dev/null || echo "inactive")"
    [ "$status" = "active" ]
}

take_task_in_coord() {
    local agent_id="$1"
    local task_id="$2"
    local force_flag="${3:-0}"

    if [ "$force_flag" = "1" ]; then
        FORCE_TAKE=1 AGENT_ID="$agent_id" "$AGENT_COORD" take -t "$task_id" -a "$agent_id" -f 2>/dev/null
    else
        AGENT_ID="$agent_id" "$AGENT_COORD" take -t "$task_id" -a "$agent_id" 2>/dev/null
    fi
}

complete_task_in_coord() {
    local agent_id="$1"
    local task_id="$2"
    AGENT_ID="$agent_id" "$AGENT_COORD" done -t "$task_id" -a "$agent_id" 2>/dev/null
}

reconcile_task_board() {
    "$AGENT_COORD" reconcile 2>&1 | while IFS= read -r line; do
        log "[reconcile] $line"
    done
}

sync_agent_runtime() {
    local agent_id="$1"
    local state_json pid status current_task heartbeat_age runtime now
    state_json="$(agent_read_state "$agent_id")"
    pid="$(echo "$state_json" | jq -r '.pid // empty')"
    status="$(echo "$state_json" | jq -r '.status // "idle"')"
    current_task="$(echo "$state_json" | jq -r '.currentTaskId // empty')"
    heartbeat_age="$(agent_status_age_seconds "$(echo "$state_json" | jq -r '.lastHeartbeatAt // empty')")"
    runtime="$(agent_runtime_status "$agent_id")"
    now="$(agent_now_utc)"

    if [ "$status" = "working" ] && [ -n "$current_task" ] && ! agent_pid_is_running "$pid"; then
        local updated
        updated="$(echo "$state_json" | jq \
            --arg now "$now" \
            --arg error "agent process exited unexpectedly" '
            .status = "crashed" |
            .pid = null |
            .lastError = $error |
            .lastHeartbeatAt = $now |
            .updatedAt = $now
        ')"
        agent_write_state "$agent_id" "$updated"
        log "Agent $agent_id crashed while on task $current_task"
        return
    fi

    if { [ "$runtime" = "unresponsive" ] || [ "$heartbeat_age" -gt "$AGENT_STALE_TIMEOUT" ]; } && [ "$status" = "working" ]; then
        local updated
        updated="$(echo "$state_json" | jq \
            --arg now "$now" \
            --arg error "heartbeat timeout" '
            .status = "unresponsive" |
            .lastError = $error |
            .lastHeartbeatAt = $now |
            .updatedAt = $now
        ')"
        agent_write_state "$agent_id" "$updated"
        log "Agent $agent_id marked unresponsive (task ${current_task:-none})"
    fi
}

find_recoverable_task() {
    local state_json task_lines task_id task_agent agent_state runtime
    state_json="$(read_state)"
    task_lines="$(echo "$state_json" | jq -r '
        [.tasks[] | select(.status == "in_progress")] |
        sort_by(
            if .priority == "high" then 0 elif .priority == "medium" then 1 else 2 end,
            .id
        ) |
        .[] | [.id, (.assignedTo // ""), (.description // "")] | @tsv
    ' 2>/dev/null || true)"

    if [ -z "$task_lines" ]; then
        return 1
    fi

    while IFS=$'\t' read -r task_id task_agent _desc; do
        [ -n "$task_id" ] || continue
        [ -n "$task_agent" ] || continue
        agent_state="$(agent_read_state "$task_agent")"
        runtime="$(agent_runtime_status "$task_agent")"
        if [ "$runtime" != "running" ] && [ "$runtime" != "unresponsive" ]; then
            echo "$task_id|$task_agent"
            return 0
        fi
    done <<< "$task_lines"

    return 1
}

find_next_pending_task() {
    jq -r '
        [.tasks[] | select(.status == "pending")] |
        sort_by(
            if .priority == "high" then 0 elif .priority == "medium" then 1 else 2 end,
            .id
        ) |
        .[0].id // empty
    ' "$STATE_FILE"
}

assign_task_for_agent() {
    local agent_id="$1"
    local task_id="$2"
    local source_agent="${3:-}"

    local description
    description="$(get_task_description "$task_id")"

    if [ -n "$source_agent" ]; then
        take_task_in_coord "$agent_id" "$task_id" 1
        local source_state updated now
        source_state="$(agent_read_state "$source_agent")"
        now="$(agent_now_utc)"
        updated="$(echo "$source_state" | jq \
            --arg agent "$agent_id" \
            --arg task_id "$task_id" \
            --arg desc "$description" \
            --arg now "$now" \
            --arg source "$source_agent" '
            .agentId = $agent |
            .currentTaskId = $task_id |
            .currentTaskDescription = $desc |
            .status = "working" |
            .pid = null |
            .startedAt = $now |
            .lastRunAt = $now |
            .lastHeartbeatAt = $now |
            .lastError = ("Recovered from " + $source) |
            .updatedAt = $now
        ')"
        agent_write_state "$agent_id" "$updated"
        return 0
    fi

    take_task_in_coord "$agent_id" "$task_id"
}

write_agent_prompt() {
    local agent_id="$1"
    local task_id="$2"
    local description="$3"
    local mode="$4"
    local source_agent="${5:-}"
    local state_file
    state_file="$(agent_state_path "$agent_id")"

    if [ "$mode" = "resume" ] && [ -n "$source_agent" ]; then
        cat << PROMPT > "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
You are $agent_id resuming a task after a stop or crash.

Your state file: $state_file
Recovered from: $source_agent
Current task ID: $task_id
Task description: $description

CRITICAL INSTRUCTIONS:
1. FIRST: cat $state_file to read your current state and saved context.
2. Continue from the latest checkpoint instead of restarting the task.
3. Update your state frequently with:
   cd $SCRIPT_DIR && ./agent-coord.sh checkpoint -a $agent_id -t $task_id -s working -c "<brief progress summary>"
4. If you need to pause, leave status=working and include your next step in context.
5. Verify: cd $WORK_DIR && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
6. On success, commit: git -C $WORK_DIR add -A && git -C $WORK_DIR commit -m "$agent_id: $description" && git -C $WORK_DIR push origin main
7. Mark done: cd $SCRIPT_DIR && ./agent-coord.sh done -t $task_id -a $agent_id

When you are finished, clear your state back to idle.
PROMPT
    else
        cat << PROMPT > "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
You are $agent_id working on a task.

Your state file: $state_file
Current task ID: $task_id
Task description: $description

CRITICAL INSTRUCTIONS:
1. FIRST: cat $state_file to read your current state and saved context.
2. Treat the state file as the source of truth for progress.
3. Keep the state file current with:
   cd $SCRIPT_DIR && ./agent-coord.sh checkpoint -a $agent_id -t $task_id -s working -c "<brief progress summary>"
4. If you pause or get blocked, leave status=working and record the blocker in context or error.
5. Verify: cd $WORK_DIR && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
6. On success, commit: git -C $WORK_DIR add -A && git -C $WORK_DIR commit -m "$agent_id: $description" && git -C $WORK_DIR push origin main
7. Mark done: cd $SCRIPT_DIR && ./agent-coord.sh done -t $task_id -a $agent_id

Continue from the saved state instead of re-discovering work that has already been done.
PROMPT
    fi
}

set_central_agent_status() {
    local agent_id="$1"
    local work_status="$2"
    local task_id="${3:-}"
    local task_desc="${4:-}"
    local pid="${5:-}"
    local last_error="${6:-}"

    [ -f "$STATE_FILE" ] || return

    local tmp_file now
    tmp_file="$(mktemp "${STATE_FILE}.tmp.XXXXXX")"
    now="$(agent_now_utc)"

    jq \
        --arg agent "$agent_id" \
        --arg work "$work_status" \
        --arg task_id "$task_id" \
        --arg task_desc "$task_desc" \
        --arg pid "$pid" \
        --arg last_error "$last_error" \
        --arg now "$now" '
        .agents |= map(
            if .id == $agent then
                .status = (if .status == "inactive" then "inactive" else "active" end) |
                .workStatus = $work |
                .currentTask = (if $task_id == "" then null else $task_id end) |
                .currentTaskDescription = (if $task_desc == "" then null else $task_desc end) |
                .pid = (if $pid == "" then null else ($pid | tonumber?) end) |
                .lastError = (if $last_error == "" then null else $last_error end) |
                .lastHeartbeatAt = $now |
                .lastUpdated = $now
            else
                .
            end
        )
    ' "$STATE_FILE" > "$tmp_file" && mv "$tmp_file" "$STATE_FILE"
}

write_devops_prompt() {
    local agent_id="$1"
    local task_id="$2"
    local description="$3"
    local state_file
    state_file="$(agent_state_path "$agent_id")"

    cat << PROMPT > "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
You are $agent_id, a DevOps operations agent for the Argus workspace.

Your state file: $state_file
Standing task ID: $task_id
Standing task: $description

Scope:
- Keep the application and deployed components healthy.
- Keep the AI agent runner, reviewer agents, coordination state, and dashboard healthy.
- Prefer observation, reconciliation, and precise fixes over broad product work.

Operating checklist:
1. FIRST: cat $state_file and inspect the current coordination state:
   cd $SCRIPT_DIR && ./agent-coord.sh doctor && ./agent-coord.sh status
2. Check application health:
   cd $WORK_DIR && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
3. Check deployment wiring without starting services:
   cd $WORK_DIR && test -f deploy/compose.yaml && docker compose -f deploy/compose.yaml config >/tmp/argus-compose-check.txt 2>&1 || true
4. If agents or tasks are stale, reconcile:
   cd $SCRIPT_DIR && ./agent-coord.sh reconcile
5. Update your state with a compact summary:
   cd $SCRIPT_DIR && ./agent-coord.sh checkpoint -a $agent_id -t $task_id -s working -c "<health summary, alerts, next action>"
6. If there is an urgent operational issue, append one short line to:
   $SCRIPT_DIR/.critical-alerts
7. Do not mark the standing monitoring task done; leave it available for the next sweep.

Make narrowly scoped operational fixes only when they are necessary to keep the app, deployment, or agent system running.
PROMPT
}

spawn_devops_agent() {
    local agent_id="$1"
    local task_id="$2"
    local description="$3"
    local prompt_file now runtime_pid heartbeat_pid exit_code
    prompt_file="$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
    now="$(agent_now_utc)"

    write_devops_prompt "$agent_id" "$task_id" "$description"

    (
        runtime_pid="${BASHPID:-$$}"
        local initial_state
        initial_state="$(agent_read_state "$agent_id")"
        initial_state="$(echo "$initial_state" | jq \
            --arg agent "$agent_id" \
            --arg task_id "$task_id" \
            --arg desc "$description" \
            --arg now "$now" \
            --argjson pid "$runtime_pid" '
            .agentId = $agent |
            .currentTaskId = $task_id |
            .currentTaskDescription = $desc |
            .status = "working" |
            .pid = $pid |
            .startedAt = $now |
            .lastRunAt = $now |
            .lastHeartbeatAt = $now |
            .lastError = null |
            .updatedAt = $now
        ')"
        agent_write_state "$agent_id" "$initial_state"
        set_central_agent_status "$agent_id" "monitoring" "$task_id" "$description" "$runtime_pid" ""

        (
            while kill -0 "$runtime_pid" 2>/dev/null; do
                sleep 45
                local heartbeat_state
                heartbeat_state="$(agent_read_state "$agent_id")"
                heartbeat_state="$(echo "$heartbeat_state" | jq --arg now "$(agent_now_utc)" '.lastHeartbeatAt = $now | .updatedAt = $now')"
                agent_write_state "$agent_id" "$heartbeat_state"
                set_central_agent_status "$agent_id" "monitoring" "$task_id" "$description" "$runtime_pid" ""
            done
        ) &
        heartbeat_pid=$!

        log "DevOps agent $agent_id spawned for standing task $task_id"
        set +e
        cat "$prompt_file" | "$OPENCODE_BIN" run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$agent_id] $line"
        done
        exit_code=${PIPESTATUS[1]}
        set -e
        kill "$heartbeat_pid" 2>/dev/null || true
        wait "$heartbeat_pid" 2>/dev/null || true

        local final_state final_error final_status
        if [ "$exit_code" -eq 0 ]; then
            final_status="idle"
            final_error=""
        else
            final_status="stalled"
            final_error="devops sweep exited with code $exit_code"
        fi

        final_state="$(agent_read_state "$agent_id")"
        final_state="$(echo "$final_state" | jq \
            --arg status "$final_status" \
            --arg error "$final_error" \
            --arg now "$(agent_now_utc)" '
            .status = $status |
            .pid = null |
            .startedAt = null |
            .lastHeartbeatAt = $now |
            .lastRunAt = $now |
            .updatedAt = $now |
            .lastError = (if $error == "" then null else $error end)
        ')"
        agent_write_state "$agent_id" "$final_state"
        set_central_agent_status "$agent_id" "idle" "" "" "" "$final_error"

        rm -f "$prompt_file"
        log "DevOps agent $agent_id finished standing task $task_id (exit: $exit_code)"
    ) &

    log "DevOps agent $agent_id background PID: $!"
}

spawn_review_agent() {
    local agent_id="$1"

    (
        log "Reviewer $agent_id starting review..."

        local modified_files
        modified_files="$(get_modified_files)"
        if [ -z "$modified_files" ]; then
            log "Reviewer $agent_id: no changes to review"
            return 0
        fi

        local review_file prompt
        review_file="$(write_review_doc "$agent_id")"
        prompt="You are $agent_id, a code reviewer. Review the following changed files:
$modified_files

Review for:
1. Code quality issues
2. Potential bugs
3. Security concerns
4. Performance issues
5. Best practices violations

Write your review to: $review_file
Append any critical findings to the file with ## Critical Findings section.
Also check for TODO/FIXME/HACK comments in changed files and report them.
Run: grep -r \"TODO\\|FIXME\\|HACK\" $modified_files 2>/dev/null || true

If you find critical issues, add them to the review file under ## Critical Findings.
If there are blocking issues, also update the dashboard alerts by running:
echo \"[CRITICAL] $agent_id found issues\" >> $SCRIPT_DIR/.critical-alerts"

        echo "$prompt" | "$OPENCODE_BIN" run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$agent_id] $line"
        done

        mark_commits_reviewed
        log "Reviewer $agent_id completed review"
    ) &
}

write_review_doc() {
    local agent_id="$1"
    local review_file="$SCRIPT_DIR/reviews/$(date +'%Y%m%d-%H%M%S')-$agent_id.md"
    mkdir -p "$SCRIPT_DIR/reviews"

    local modified_files recent diff_summary
    modified_files="$(get_modified_files)"
    recent="$(git -C "$WORK_DIR" log --oneline -5)"
    diff_summary="$(git -C "$WORK_DIR" diff --stat HEAD~5..HEAD 2>/dev/null | tail -1)"

    cat > "$review_file" << REVIEW_EOF
# Code Review Report
Generated: $(date +'%Y-%m-%d %H:%M:%S')
Reviewer: $agent_id

## Summary
Modified files since last review:
$modified_files

## Recent Commits
$recent

## Changes Summary
$diff_summary

## Critical Alerts
$(get_critical_alerts)

## Tasks Requiring Attention
$(get_tasks_requiring_attention)
REVIEW_EOF
    log "Review document written: $review_file"
    echo "$review_file"
}

mark_commits_reviewed() {
    local review_marker="$SCRIPT_DIR/.reviewed-commits"
    git -C "$WORK_DIR" log --oneline -1 HEAD > "$review_marker"
}

get_critical_alerts() {
    local alerts=""
    local build_status
    build_status="$(dotnet build "$WORK_DIR/src/Argus.AppHost/Argus.AppHost.csproj" --configuration Release --verbosity quiet 2>&1; echo "EXIT:$?")"
    if ! echo "$build_status" | grep -q "EXIT:0"; then
        alerts="${alerts}\n- [CRITICAL] Build failed - needs immediate attention"
    fi

    local modified
    modified="$(get_modified_files)"
    if echo "$modified" | grep -q "Security\|Auth\|Permission\|Token\|Secret"; then
        alerts="${alerts}\n- [CRITICAL] Security-sensitive files modified - requires security review"
    fi

    if [ -z "$alerts" ]; then
        echo "None"
    else
        echo "$alerts"
    fi
}

get_tasks_requiring_attention() {
    local attention=""

    if [ -f "$STATE_FILE" ]; then
        local stale_tasks
        stale_tasks="$(jq -r '[.tasks[] | select(.status == "in_progress" and (.assignedTo == null or .assignedTo == ""))] | length' "$STATE_FILE" 2>/dev/null || echo "0")"
        if [ "$stale_tasks" -gt 0 ]; then
            attention="${attention}\n- Unassigned in-progress tasks: $stale_tasks"
        fi
    fi

    local last_pull last_pull_epoch now_epoch seconds_since_pull
    last_pull="$(get_last_pull_time)"
    last_pull_epoch="$(date -d "${last_pull:-1970-01-01}" +%s 2>/dev/null || echo 0)"
    now_epoch="$(date +%s)"
    seconds_since_pull=$((now_epoch - last_pull_epoch))
    if [ "$seconds_since_pull" -gt 600 ]; then
        attention="${attention}\n- No pull in $(($seconds_since_pull / 60)) minutes - may be behind"
    fi

    if [ -z "$attention" ]; then
        echo "None"
    else
        echo "$attention"
    fi
}

choose_task_for_agent() {
    local agent_id="$1"
    local state_json current_task_id current_status current_task_assignee runtime recoverable
    state_json="$(agent_read_state "$agent_id")"
    current_task_id="$(echo "$state_json" | jq -r '.currentTaskId // empty')"
    current_status="$(echo "$state_json" | jq -r '.status // "idle"')"
    runtime="$(agent_runtime_status "$agent_id")"

    if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
        return 1
    fi

    if [ -n "$current_task_id" ] && [ "$current_status" != "idle" ]; then
        current_task_assignee="$(get_task_assignee "$current_task_id")"
        if [ -z "$current_task_assignee" ] || [ "$current_task_assignee" = "$agent_id" ] || [ "$current_status" = "crashed" ] || [ "$current_status" = "stalled" ]; then
            echo "resume|$current_task_id|$current_task_assignee"
            return 0
        fi
    fi

    recoverable="$(find_recoverable_task || true)"
    if [ -n "$recoverable" ]; then
        echo "recover|$recoverable"
        return 0
    fi

    local next_task
    next_task="$(find_next_pending_task)"
    if [ -n "$next_task" ]; then
        echo "new|$next_task"
        return 0
    fi

    return 1
}

spawn_agent() {
    local agent_id="$1"
    local task_id="$2"
    local description="$3"
    local mode="${4:-new}"
    local source_agent="${5:-}"
    local state_file prompt_file now runtime_pid heartbeat_pid exit_code coord_status
    state_file="$(agent_state_path "$agent_id")"
    prompt_file="$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
    now="$(agent_now_utc)"

    write_agent_prompt "$agent_id" "$task_id" "$description" "$mode" "$source_agent"

    (
        runtime_pid="${BASHPID:-$$}"
        local initial_state
        initial_state="$(agent_read_state "$agent_id")"
        initial_state="$(echo "$initial_state" | jq \
            --arg agent "$agent_id" \
            --arg task_id "$task_id" \
            --arg desc "$description" \
            --arg now "$now" \
            --argjson pid "$runtime_pid" '
            .agentId = $agent |
            .currentTaskId = $task_id |
            .currentTaskDescription = $desc |
            .status = "working" |
            .pid = $pid |
            .startedAt = (.startedAt // $now) |
            .lastRunAt = $now |
            .lastHeartbeatAt = $now |
            .lastError = null |
            .updatedAt = $now
        ')"
        agent_write_state "$agent_id" "$initial_state"

        (
            while kill -0 "$runtime_pid" 2>/dev/null; do
                sleep 45
                local heartbeat_state
                heartbeat_state="$(agent_read_state "$agent_id")"
                heartbeat_state="$(echo "$heartbeat_state" | jq \
                    --arg now "$(agent_now_utc)" '
                    .lastHeartbeatAt = $now |
                    .updatedAt = $now
                ')"
                agent_write_state "$agent_id" "$heartbeat_state"
            done
        ) &
        heartbeat_pid=$!

        log "Agent $agent_id spawned for task $task_id"
        set +e
        cat "$prompt_file" | "$OPENCODE_BIN" run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$agent_id] $line"
        done
        exit_code=${PIPESTATUS[1]}
        set -e
        kill "$heartbeat_pid" 2>/dev/null || true
        wait "$heartbeat_pid" 2>/dev/null || true

        coord_status="$(get_task_status "$task_id")"
        if [ "$coord_status" = "completed" ]; then
            local idle_state
            idle_state="$(agent_read_state "$agent_id")"
            idle_state="$(echo "$idle_state" | jq \
                --arg now "$(agent_now_utc)" '
                .currentTaskId = null |
                .currentTaskDescription = null |
                .context = null |
                .status = "idle" |
                .pid = null |
                .startedAt = null |
                .lastHeartbeatAt = $now |
                .lastRunAt = $now |
                .updatedAt = $now |
                .lastError = null
            ')"
            agent_write_state "$agent_id" "$idle_state"
            log "Agent $agent_id completed task $task_id"
        else
            local stalled_state
            stalled_state="$(agent_read_state "$agent_id")"
            stalled_state="$(echo "$stalled_state" | jq \
                --arg now "$(agent_now_utc)" \
                --arg error "agent exited with code $exit_code before task completion (task status: $coord_status)" '
                .status = "stalled" |
                .pid = null |
                .lastHeartbeatAt = $now |
                .lastRunAt = $now |
                .updatedAt = $now |
                .lastError = $error
            ')"
            agent_write_state "$agent_id" "$stalled_state"
            log "Agent $agent_id task $task_id exited (task status: $coord_status, exit: $exit_code)"
        fi

        rm -f "$prompt_file"
    ) &

    log "Agent $agent_id background PID: $!"
}

run_agent_if_needed() {
    local agent_id="$1"

    if ! is_agent_active "$agent_id"; then
        return
    fi

    sync_agent_runtime "$agent_id"

    local state_json runtime current_task_id current_status task_desc selected kind source_agent
    state_json="$(agent_read_state "$agent_id")"
    runtime="$(agent_runtime_status "$agent_id")"
    current_task_id="$(echo "$state_json" | jq -r '.currentTaskId // empty')"
    current_status="$(echo "$state_json" | jq -r '.status // "idle"')"
    task_desc="$(echo "$state_json" | jq -r '.currentTaskDescription // empty')"

    if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
        return
    fi

    if [ "$current_status" = "crashed" ]; then
        if [ -n "$current_task_id" ] && [ "$current_task_id" != "null" ]; then
            task_desc="$(get_task_description "$current_task_id")"
            log "Agent $agent_id recovering from crash, respawning for task $current_task_id"
            spawn_agent "$agent_id" "$current_task_id" "$task_desc" "resume" "$agent_id"
        fi
        return
    fi

    if [ "$current_status" != "idle" ]; then
        log "Agent $agent_id is $current_status; supervisor will not assign or resume work in this slot"
        return
    fi

    if selected="$(choose_task_for_agent "$agent_id")"; then
        IFS='|' read -r kind task_id source_agent <<< "$selected"
        case "$kind" in
            resume)
                if [ -n "$current_task_id" ] && [ "$current_task_id" != "null" ]; then
                    task_desc="$(get_task_description "$current_task_id")"
                    spawn_agent "$agent_id" "$current_task_id" "$task_desc" "resume" "$agent_id"
                fi
                ;;
            recover)
                task_desc="$(get_task_description "$task_id")"
                assign_task_for_agent "$agent_id" "$task_id" "$source_agent"
                spawn_agent "$agent_id" "$task_id" "$task_desc" "resume" "$source_agent"
                ;;
            new)
                task_desc="$(get_task_description "$task_id")"
                assign_task_for_agent "$agent_id" "$task_id"
                spawn_agent "$agent_id" "$task_id" "$task_desc" "new"
                ;;
        esac
    fi
}

count_busy_agents() {
    local count=0
    for agent in "${AGENTS[@]}"; do
        local runtime
        runtime="$(agent_runtime_status "$agent")"
        if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
            count=$((count + 1))
        fi
    done
    echo "$count"
}

count_busy_devops_agents() {
    local count=0
    for agent in "${DEVOPS_AGENTS[@]}"; do
        local runtime
        runtime="$(agent_runtime_status "$agent")"
        if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
            count=$((count + 1))
        fi
    done
    echo "$count"
}

count_busy_review_agents() {
    local count=0
    for agent in "${REVIEW_AGENTS[@]}"; do
        local runtime
        runtime="$(agent_runtime_status "$agent")"
        if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
            count=$((count + 1))
        fi
    done
    echo "$count"
}

run_devops_agent_if_needed() {
    local agent_id="$1"

    if ! is_agent_active "$agent_id"; then
        return
    fi

    sync_agent_runtime "$agent_id"

    local state_json runtime last_run_age task_id task_desc
    state_json="$(agent_read_state "$agent_id")"
    runtime="$(agent_runtime_status "$agent_id")"
    if [ "$runtime" = "running" ] || [ "$runtime" = "unresponsive" ]; then
        return
    fi

    last_run_age="$(agent_status_age_seconds "$(echo "$state_json" | jq -r '.lastRunAt // empty')")"
    if [ "$last_run_age" -lt "$DEVOPS_AGENT_INTERVAL" ]; then
        return
    fi

    task_id="$(get_monitoring_task_for_agent "$agent_id")"
    if [ -z "$task_id" ]; then
        log "DevOps agent $agent_id has no standing monitoring task"
        return
    fi

    task_desc="$(get_task_description "$task_id")"
    spawn_devops_agent "$agent_id" "$task_id" "$task_desc"
}

run_review_cycle() {
    local modified_files
    modified_files="$(get_modified_files)"
    if [ -z "$modified_files" ]; then
        return
    fi

    local last_review_marker last_reviewed new_commits
    last_review_marker="$SCRIPT_DIR/.reviewed-commits"
    last_reviewed=""
    if [ -f "$last_review_marker" ]; then
        last_reviewed="$(cat "$last_review_marker")"
    fi

    if [ -n "$last_reviewed" ]; then
        new_commits="$(git -C "$WORK_DIR" log --oneline "$last_reviewed..HEAD" 2>/dev/null)"
    else
        new_commits="$(git -C "$WORK_DIR" log --oneline -10 2>/dev/null)"
    fi

    if [ -z "$new_commits" ]; then
        return
    fi

    log "New commits detected, spawning review agents..."
    for reviewer in "${REVIEW_AGENTS[@]}"; do
        local busy_reviewers
        busy_reviewers="$(count_busy_review_agents)"
        if [ "$busy_reviewers" -ge "$MAX_CONCURRENT_REVIEWERS" ]; then
            log "Max concurrent reviewers ($MAX_CONCURRENT_REVIEWERS) reached, skipping $reviewer"
            break
        fi
        spawn_review_agent "$reviewer"
        sleep 1
    done
}

log "========================================"
log "Auto-run-agents starting (interval: ${INTERVAL}s)"
log "Working directory: $WORK_DIR"
log "Dev agents: ${AGENTS[*]}"
log "DevOps agents: ${DEVOPS_AGENTS[*]}"
log "Review agents: ${REVIEW_AGENTS[*]}"
log "Max concurrent: $MAX_CONCURRENT"
log "Max concurrent DevOps: $MAX_CONCURRENT_DEVOPS"
log "Max concurrent reviewers: $MAX_CONCURRENT_REVIEWERS"

git_pull || true
reconcile_task_board
log "========================================"

last_reconcile_epoch="$(date +%s)"

while true; do
    now_epoch="$(date +%s)"
    if [ $((now_epoch - last_reconcile_epoch)) -ge "$RECONCILE_INTERVAL" ]; then
        reconcile_task_board
        last_reconcile_epoch="$now_epoch"
    fi

    local_pending="$(count_tasks "pending")"
    local_in_progress="$(count_tasks "in_progress")"

    last_pull="$(get_last_pull_time)"
    last_pull_epoch="$(date -d "${last_pull:-1970-01-01}" +%s 2>/dev/null || echo 0)"
    now_epoch="$(date +%s)"
    seconds_since_pull=$((now_epoch - last_pull_epoch))

    if [ "$seconds_since_pull" -gt 300 ]; then
        log "5+ minutes since last pull, fetching latest..."
        git_pull || true
    fi

    for agent in "${AGENTS[@]}" "${DEVOPS_AGENTS[@]}"; do
        sync_agent_runtime "$agent"
    done

    log "Status: $local_pending pending, $local_in_progress in progress, last pull: $last_pull"

    if [ "$local_pending" -eq 0 ] && [ "$local_in_progress" -eq 0 ]; then
        log "All tasks done. Running review cycle..."
        run_review_cycle
        sleep "$INTERVAL"
        continue
    fi

    busy_count="$(count_busy_agents)"
    log "Busy dev agents: $busy_count / $MAX_CONCURRENT"

    if [ "$busy_count" -lt "$MAX_CONCURRENT" ]; then
        for agent in "${AGENTS[@]}"; do
            if [ "$(count_busy_agents)" -ge "$MAX_CONCURRENT" ]; then
                break
            fi

            run_agent_if_needed "$agent"
            sleep 1
        done
    fi

    busy_devops_count="$(count_busy_devops_agents)"
    log "Busy DevOps agents: $busy_devops_count / $MAX_CONCURRENT_DEVOPS"
    if [ "$busy_devops_count" -lt "$MAX_CONCURRENT_DEVOPS" ]; then
        for agent in "${DEVOPS_AGENTS[@]}"; do
            if [ "$(count_busy_devops_agents)" -ge "$MAX_CONCURRENT_DEVOPS" ]; then
                break
            fi

            run_devops_agent_if_needed "$agent"
            sleep 1
        done
    fi

    sleep "$INTERVAL"
done
