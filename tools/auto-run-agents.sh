#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
AGENT_COORD="$SCRIPT_DIR/agent-coord.sh"
INTERVAL="${1:-60}"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OPENCODE_BIN="${OPENCODE_BIN:-opencode}"
LOG_FILE="${LOG_FILE:-/tmp/auto-run-agents.log}"

AGENTS=("agent-1" "agent-2" "agent-3" "agent-4" "agent-5")
REVIEW_AGENTS=("reviewer-1" "reviewer-2")
MAX_CONCURRENT="${MAX_CONCURRENT:-3}"

log() { echo "[$(date +'%Y-%m-%dT%H:%M:%S')] $*" >> "$LOG_FILE"; }

git_pull() {
    log "Pulling latest changes from origin..."
    if git -C "$WORK_DIR" pull origin main 2>&1 | tee -a "$LOG_FILE"; then
        local pull_time=$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")
        if [ -f "$STATE_FILE" ]; then
            jq ".lastPullAt = \"$pull_time\"" "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
        fi
        log "Pull completed at $pull_time"
        return 0
    else
        log "Pull failed - continuing with local state"
        return 1
    fi
}

get_modified_files() {
    git -C "$WORK_DIR" status --porcelain 2>/dev/null | grep -v "^??" | awk '{print $2}' | grep -v "^tools/\.agent" | grep -v "^tools/\.agent-prompt" | sort
}

get_recent_commits() {
    git -C "$WORK_DIR" log --oneline -10 2>/dev/null
}

get_last_pull_time() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE" | jq -r '.lastPullAt // "never"'
    else
        echo "never"
    fi
}

get_agent_state_file() {
    local agent_id="$1"
    echo "$SCRIPT_DIR/.agent-state-$agent_id.json"
}

read_agent_state() {
    local agent_id="$1"
    local state_file=$(get_agent_state_file "$agent_id")
    if [ -f "$state_file" ]; then
        cat "$state_file"
    else
        echo '{"agentId":"'$agent_id'","currentTaskId":null,"currentTaskDescription":null,"status":"idle","lastRunAt":null}'
    fi
}

write_agent_state() {
    local agent_id="$1"
    local state_file=$(get_agent_state_file "$agent_id")
    cat > "$state_file"
}

mark_agent_idle() {
    local agent_id="$1"
    write_agent_state "$agent_id" '{"agentId":"'$agent_id'","currentTaskId":null,"currentTaskDescription":null,"status":"idle","lastRunAt":"'$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")'"}'
}

is_agent_active() {
    local agent_id="$1"
    if [ ! -f "$STATE_FILE" ]; then return 1; fi
    local status=$(cat "$STATE_FILE" | jq -r ".agents[] | select(.id == \"$agent_id\") | .status // \"inactive\"")
    [ "$status" = "active" ]
}

is_agent_busy() {
    local agent_id="$1"
    pgrep -f "opencode.*$agent_id" > /dev/null 2>&1
}

find_next_pending_task() {
    cat "$STATE_FILE" | jq -r '[.tasks[] | select(.status == "pending")] | 
        sort_by(if .priority == "high" then 0 elif .priority == "medium" then 1 else 2 end, .id) | 
        .[0].id // empty'
}

get_task_description() {
    local task_id="$1"
    cat "$STATE_FILE" | jq -r ".tasks[] | select(.id == \"$task_id\") | .description // empty"
}

get_task_status() {
    local task_id="$1"
    cat "$STATE_FILE" | jq -r ".tasks[] | select(.id == \"$task_id\") | .status // \"pending\""
}

count_tasks() {
    local status_filter="$1"
    cat "$STATE_FILE" | jq "[.tasks[] | select(.status == \"$status_filter\")] | length"
}

take_task_in_coord() {
    local agent_id="$1"
    local task_id="$2"
    $AGENT_COORD take -t "$task_id" -a "$agent_id" 2>/dev/null
}

complete_task_in_coord() {
    local agent_id="$1"
    local task_id="$2"
    $AGENT_COORD done -t "$task_id" -a "$agent_id" 2>/dev/null
}

get_unreviewed_commits() {
    local review_marker="$SCRIPT_DIR/.reviewed-commits"
    local last_reviewed=""
    if [ -f "$review_marker" ]; then
        last_reviewed=$(cat "$review_marker")
    fi
    if [ -n "$last_reviewed" ]; then
        git -C "$WORK_DIR" log --oneline "$last_reviewed..HEAD" 2>/dev/null
    else
        git -C "$WORK_DIR" log --oneline -10 2>/dev/null
    fi
}

mark_commits_reviewed() {
    local review_marker="$SCRIPT_DIR/.reviewed-commits"
    git -C "$WORK_DIR" log --oneline -1 HEAD > "$review_marker"
}

write_review_doc() {
    local agent_id="$1"
    local review_file="$SCRIPT_DIR/reviews/$(date +'%Y%m%d-%H%M%S')-$agent_id.md"
    mkdir -p "$SCRIPT_DIR/reviews"
    
    local modified_files=$(get_modified_files)
    local recent=$(git -C "$WORK_DIR" log --oneline -5)
    local diff_summary=$(git -C "$WORK_DIR" diff --stat HEAD~5..HEAD 2>/dev/null | tail -1)
    
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

get_critical_alerts() {
    local alerts=""
    local build_status=$(dotnet build "$WORK_DIR/src/Argus.AppHost/Argus.AppHost.csproj" --configuration Release --verbosity quiet 2>&1; echo "EXIT:$?")
    if ! echo "$build_status" | grep -q "EXIT:0"; then
        alerts="${alerts}\n- [CRITICAL] Build failed - needs immediate attention"
    fi
    
    local modified=$(get_modified_files)
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
        local stale_tasks=$(cat "$STATE_FILE" | jq -r '[.tasks[] | select(.status == "in_progress") | select(.assignedTo == null)] | length')
        if [ "$stale_tasks" -gt 0 ]; then
            attention="${attention}\n- Unassigned in-progress tasks: $stale_tasks"
        fi
    fi
    
    local last_pull=$(get_last_pull_time)
    local last_pull_epoch=$(date -d "${last_pull:-1970-01-01}" +%s 2>/dev/null || echo 0)
    local now_epoch=$(date +%s)
    local seconds_since_pull=$((now_epoch - last_pull_epoch))
    if [ $seconds_since_pull -gt 600 ]; then
        attention="${attention}\n- No pull in $(($seconds_since_pull / 60)) minutes - may be behind"
    fi
    
    if [ -z "$attention" ]; then
        echo "None"
    else
        echo "$attention"
    fi
}

spawn_review_agent() {
    local agent_id="$1"
    
    (
        log "Reviewer $agent_id starting review..."
        
        local modified_files=$(get_modified_files)
        if [ -z "$modified_files" ]; then
            log "Reviewer $agent_id: no changes to review"
            return 0
        fi
        
        local review_file=$(write_review_doc "$agent_id")
        
        local prompt="You are $agent_id, a code reviewer. Review the following changed files:
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

        echo "$prompt" | $OPENCODE_BIN run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$agent_id] $line"
        done
        
        mark_commits_reviewed
        log "Reviewer $agent_id completed review"
    ) &
}

spawn_agent() {
    local agent_id="$1"
    local task_id="$2"
    local description="$3"
    local status="$4"
    
    local state_file=$(get_agent_state_file "$agent_id")
    
    if [ "$status" = "working" ]; then
        cat << PROMPT > "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
You are $agent_id resuming work on an incomplete task.

Your state file: $state_file
Current task ID: $task_id
Task description: $description

CRITICAL INSTRUCTIONS:
1. FIRST: cat $state_file to read your current state
2. Continue the implementation from where you left off
3. Work until the task is fully complete
4. Verify: cd $WORK_DIR && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
5. On success, commit: git -C $WORK_DIR add -A && git -C $WORK_DIR commit -m "$agent_id: $description" && git -C $WORK_DIR push origin main
6. Mark done: cd $SCRIPT_DIR && ./agent-coord.sh done -t $task_id -a $agent_id

If you complete the task successfully, also update your state file: echo '{"agentId":"$agent_id","currentTaskId":null,"currentTaskDescription":null,"status":"idle","lastRunAt":"'$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")'"}' > $state_file
PROMPT
    else
        cat << PROMPT > "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
You are $agent_id starting a new task.

Task ID: $task_id
Task description: $description

CRITICAL INSTRUCTIONS:
1. FIRST: Write your initial state: echo '{"agentId":"$agent_id","currentTaskId":"$task_id","currentTaskDescription":"'$description'","status":"working","lastRunAt":"'$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")'"}' > $state_file
2. Explore the codebase to understand the relevant files
3. Implement the feature/fix completely
4. Verify: cd $WORK_DIR && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
5. On success, commit: git -C $WORK_DIR add -A && git -C $WORK_DIR commit -m "$agent_id: $description" && git -C $WORK_DIR push origin main
6. Mark done: cd $SCRIPT_DIR && ./agent-coord.sh done -t $task_id -a $agent_id
7. Update your state file: echo '{"agentId":"$agent_id","currentTaskId":null,"currentTaskDescription":null,"status":"idle","lastRunAt":"'$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")'"}' > $state_file

If you cannot complete in this run, keep status=working so the next run continues.
PROMPT
    fi
    
    (
        log "Agent $agent_id spawned for task $task_id"
        cat "$SCRIPT_DIR/.agent-prompt-$agent_id.txt" | $OPENCODE_BIN run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$agent_id] $line"
        done
        
        local exit_code=${PIPESTATUS[0]}
        
        local coord_status=$(get_task_status "$task_id")
        if [ "$coord_status" = "completed" ]; then
            mark_agent_idle "$agent_id"
            log "Agent $agent_id completed task $task_id - COMMITTING AND PUSHING"
            cd "$WORK_DIR" && git add -A && git commit -m "agent $agent_id: completed task $task_id" && git push origin main 2>&1 | tee -a "$LOG_FILE"
            log "Agent $agent_id push complete"
        else
            log "Agent $agent_id task $task_id exited (coord status: $coord_status, exit: $exit_code)"
        fi
        
        rm -f "$SCRIPT_DIR/.agent-prompt-$agent_id.txt"
    ) &
    
    log "Agent $agent_id background PID: $!"
}

run_agent_if_idle() {
    local agent_id="$1"
    
    if is_agent_busy "$agent_id"; then
        return
    fi
    
    local state_json=$(read_agent_state "$agent_id")
    local current_task_id=$(echo "$state_json" | jq -r '.currentTaskId')
    local status=$(echo "$state_json" | jq -r '.status')
    local task_desc=$(echo "$state_json" | jq -r '.currentTaskDescription')
    
    if [ "$status" = "working" ] && [ -n "$current_task_id" ] && [ "$current_task_id" != "null" ]; then
        spawn_agent "$agent_id" "$current_task_id" "$task_desc" "working"
    elif [ "$status" = "idle" ] || [ -z "$current_task_id" ] || [ "$current_task_id" = "null" ]; then
        local next_task=$(find_next_pending_task)
        if [ -n "$next_task" ]; then
            local description=$(get_task_description "$next_task")
            take_task_in_coord "$agent_id" "$next_task"
            spawn_agent "$agent_id" "$next_task" "$description" "new"
        fi
    fi
}

count_busy_agents() {
    local count=0
    for agent in "${AGENTS[@]}"; do
        if is_agent_busy "$agent"; then
            count=$((count + 1))
        fi
    done
    echo $count
}

run_review_cycle() {
    local modified_files=$(get_modified_files)
    if [ -z "$modified_files" ]; then
        return
    fi
    
    local last_review_marker="$SCRIPT_DIR/.reviewed-commits"
    local last_reviewed=""
    if [ -f "$last_review_marker" ]; then
        last_reviewed=$(cat "$last_review_marker")
    fi
    
    local new_commits=$(git -C "$WORK_DIR" log --oneline ${last_reviewed:-HEAD~10}..HEAD 2>/dev/null)
    if [ -z "$new_commits" ]; then
        return
    fi
    
    log "New commits detected, spawning review agents..."
    for reviewer in "${REVIEW_AGENTS[@]}"; do
        spawn_review_agent "$reviewer"
    done
}

log "========================================"
log "Auto-run-agents starting (interval: ${INTERVAL}s)"
log "Working directory: $WORK_DIR"
log "Dev agents: ${AGENTS[*]}"
log "Review agents: ${REVIEW_AGENTS[*]}"
log "Max concurrent: $MAX_CONCURRENT"

git_pull
log "========================================"

while true; do
    pending=$(count_tasks "pending")
    in_progress=$(count_tasks "in_progress")
    
    last_pull=$(get_last_pull_time)
    last_pull_epoch=$(date -d "${last_pull:-1970-01-01}" +%s 2>/dev/null || echo 0)
    now_epoch=$(date +%s)
    seconds_since_pull=$((now_epoch - last_pull_epoch))
    
    if [ $seconds_since_pull -gt 300 ]; then
        log "5+ minutes since last pull, fetching latest..."
        git_pull
    fi
    
    log "Status: $pending pending, $in_progress in progress, last pull: $last_pull"
    
    if [ "$pending" -eq 0 ] && [ "$in_progress" -eq 0 ]; then
        log "All tasks done. Running review cycle..."
        run_review_cycle
        sleep $INTERVAL
        continue
    fi
    
    busy_count=$(count_busy_agents)
    log "Busy dev agents: $busy_count / $MAX_CONCURRENT"
    
    if [ "$busy_count" -lt "$MAX_CONCURRENT" ]; then
        for agent in "${AGENTS[@]}"; do
            if ! is_agent_active "$agent"; then
                continue
            fi
            
            current_busy=$(count_busy_agents)
            if [ "$current_busy" -ge "$MAX_CONCURRENT" ]; then
                break
            fi
            
            run_agent_if_idle "$agent"
            sleep 1
        done
    fi
    
    sleep $INTERVAL
done