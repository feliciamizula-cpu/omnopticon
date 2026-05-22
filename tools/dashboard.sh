#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REFRESH="${1:-5}"
REVIEW_DIR="$SCRIPT_DIR/reviews"

if [ -t 0 ]; then
    INTERACTIVE=1
else
    INTERACTIVE=0
fi

get_modified_files() {
    git -C "$WORK_DIR" status --porcelain 2>/dev/null | grep -v "^??" | awk '{print $2}' | grep -v "^tools/\.agent" | grep -v "^tools/\.agent-prompt" | sort
}

get_recent_commits() {
    git -C "$WORK_DIR" log --oneline -5 2>/dev/null
}

get_last_pull_time() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE" | jq -r '.lastPullAt // "never"'
    else
        echo "never"
    fi
}

seconds_since_pull() {
    local last_pull=$(get_last_pull_time)
    if [ "$last_pull" = "never" ] || [ -z "$last_pull" ]; then
        echo 999999
        return
    fi
    local last_pull_epoch=$(date -d "${last_pull}" +%s 2>/dev/null || echo 0)
    local now_epoch=$(date +%s)
    echo $((now_epoch - last_pull_epoch))
}

get_critical_alerts() {
    local alerts=""
    
    local build_result=$(cd "$WORK_DIR" && dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet 2>&1; echo "EXIT:$?")
    if ! echo "$build_result" | grep -q "EXIT:0"; then
        alerts="${alerts}BUILD FAILED"
    fi
    
    local modified=$(get_modified_files)
    if echo "$modified" | grep -qi "Security\|Auth\|Permission\|Token\|Secret\|Password"; then
        alerts="${alerts} SECURITY-SENSITIVE"
    fi
    
    local critical_file="$SCRIPT_DIR/.critical-alerts"
    if [ -f "$critical_file" ]; then
        local critical=$(cat "$critical_file" 2>/dev/null | head -3)
        if [ -n "$critical" ]; then
            alerts="${alerts} CRITICAL: $critical"
        fi
    fi
    
    echo "$alerts"
}

get_latest_review() {
    if [ -d "$REVIEW_DIR" ]; then
        ls -t "$REVIEW_DIR"/*.md 2>/dev/null | head -1
    fi
}

get_review_summary() {
    local latest=$(get_latest_review)
    if [ -n "$latest" ] && [ -f "$latest" ]; then
        local date=$(head -1 "$latest" | grep -oE '[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}' || echo "unknown")
        local reviewer=$(grep "Reviewer:" "$latest" | head -1 | awk '{print $2}' || echo "?")
        local findings=$(grep -c "## Critical" "$latest" 2>/dev/null || echo "0")
        echo "Last review: $date by $reviewer, $findings critical sections"
    else
        echo "No reviews yet"
    fi
}

get_tasks_requiring_attention() {
    local attention=""
    
    if [ -f "$STATE_FILE" ]; then
        local stale=$(cat "$STATE_FILE" | jq -r '[.tasks[] | select(.status == "in_progress") | select(.assignedTo == null)] | length' 2>/dev/null || echo "0")
        if [ "$stale" -gt 0 ]; then
            attention="Unassigned tasks: $stale"
        fi
        
        local high_pending=$(cat "$STATE_FILE" | jq -r '[.tasks[] | select(.status == "pending") | select(.priority == "high")] | length' 2>/dev/null || echo "0")
        if [ "$high_pending" -gt 0 ]; then
            attention="${attention} High priority pending: $high_pending"
        fi
    fi
    
    local ssp=$(seconds_since_pull)
    if [ "$ssp" -gt 300 ]; then
        attention="${attention} No pull in $(($ssp / 60))m"
    fi
    
    if [ -z "$attention" ]; then
        echo "None"
    else
        echo "$attention"
    fi
}

draw() {
    if [ $INTERACTIVE -eq 1 ]; then
        printf "\033[2J\033[H"
    else
        echo "═══════════════════════════════════════════════════════════════════"
        echo "              ★ ARGUS AGENT + REVIEWER DASHBOARD ★"
        echo "═══════════════════════════════════════════════════════════════════"
    fi
    
    ts=$(date +'%Y-%m-%d %H:%M:%S')
    last_pull=$(get_last_pull_time)
    alerts=$(get_critical_alerts)
    
    total=$(cat "$STATE_FILE" 2>/dev/null | jq '.tasks | length' 2>/dev/null || echo "0")
    completed=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "completed")] | length' 2>/dev/null || echo "0")
    pending=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "pending")] | length' 2>/dev/null || echo "0")
    in_progress=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "in_progress")] | length' 2>/dev/null || echo "0")
    
    if [ $INTERACTIVE -eq 1 ]; then
        printf "\033[1;36m┌──────────────────────────────────────────────────────────────────┐\033[0m\n"
        printf "\033[1;36m│\033[0m           \033[1;33m★ ARGUS AGENT + REVIEWER DASHBOARD ★\033[0m            \033[1;36m│\033[0m\n"
        printf "\033[1;36m└──────────────────────────────────────────────────────────────────┘\033[0m\n"
        printf "  %s\n" "$ts"
        printf "  Last pull: \033[1;35m%s\033[0m\n" "$last_pull"
        
        if [ -n "$alerts" ]; then
            printf "\n  \033[1;41m⚠ CRITICAL ALERTS: %s\033[0m\n" "$alerts"
        fi
        
        printf "\n  \033[1;32m✓ %d\033[0m completed  \033[1;33m⟳ %d\033[0m in progress  \033[1;34m○ %d\033[0m pending\n" "$completed" "$in_progress" "$pending"
    else
        echo "  $ts"
        echo "  Last pull: $last_pull"
        if [ -n "$alerts" ]; then
            echo "  [WARNING] CRITICAL ALERTS: $alerts"
        fi
        echo "  [OK] $completed completed  [RUN] $in_progress in progress  [---] $pending pending"
    fi
    
    echo ""
    printf "\033[1;35m━━━ REVIEWERS ━━━\033[0m\n"
    printf "%-12s %-12s %-20s\n" "REVIEWER" "STATUS" "LAST REVIEW"
    echo "────────────────────────────────────────────────────────"
    for reviewer in reviewer-1 reviewer-2; do
        local review_state="$SCRIPT_DIR/.reviewer-state-$reviewer.json"
        if [ -f "$review_state" ]; then
            local last_review=$(cat "$review_state" | jq -r '.lastReviewAt // "never"' | cut -c1-20)
            local status=$(cat "$review_state" | jq -r '.status // "idle"')
        else
            last_review="never"
            status="idle"
        fi
        
        if [ "$status" = "reviewing" ]; then
            printf "\033[1;33m%-12s\033[0m %-12s %-20s\n" "$reviewer" "REVIEWING" "$last_review"
        else
            printf "\033[1;32m%-12s\033[0m %-12s %-20s\n" "$reviewer" "IDLE" "$last_review"
        fi
    done
    printf "  Latest review: \033[1;36m%s\033[0m\n" "$(get_review_summary)"
    
    echo ""
    printf "\033[1;34m━━━ DEV AGENTS ━━━\033[0m\n"
    printf "%-10s %-10s %-8s %-35s\n" "AGENT" "STATUS" "TASK" "DESCRIPTION"
    echo "──────────────────────────────────────────────────────────────────"
    
    for agent_id in agent-1 agent-2 agent-3 agent-4 agent-5; do
        agent_json=$(cat "$STATE_FILE" 2>/dev/null | jq -r ".agents[] | select(.id == \"$agent_id\")" 2>/dev/null || echo "{}")
        status=$(echo "$agent_json" | jq -r '.status // "inactive"')
        current_task=$(echo "$agent_json" | jq -r '.currentTask // "-"')
        
        local_state="$SCRIPT_DIR/.agent-state-$agent_id.json"
        if [ -f "$local_state" ]; then
            task_desc=$(cat "$local_state" | jq -r '.currentTaskDescription // ""' | cut -c1-33)
            work_status=$(cat "$local_state" | jq -r '.status // "idle"')
        else
            task_desc="-"
            work_status="idle"
        fi
        
        if [ "$status" = "active" ] && [ "$work_status" = "working" ]; then
            printf "\033[1;33m%-10s\033[0m %-10s \033[1;36m%-8s\033[0m %-35s\n" \
                "$agent_id" "WORKING" "$current_task" "$task_desc"
        elif [ "$status" = "active" ]; then
            printf "\033[1;32m%-10s\033[0m %-10s %-8s %s\n" \
                "$agent_id" "IDLE" "$current_task" "$task_desc"
        else
            printf "\033[1;30m%-10s\033[0m %-10s %-8s %s\n" \
                "$agent_id" "OFF" "$current_task" "$task_desc"
        fi
    done
    
    echo ""
    printf "\033[1;33m━━━ TASKS IN PROGRESS ━━━\033[0m\n"
    in_prog=$(cat "$STATE_FILE" 2>/dev/null | jq -c '[.tasks[] | select(.status == "in_progress")] | sort_by(.id)' 2>/dev/null || echo "[]")
    if [ "$in_prog" != "[]" ]; then
        echo "$in_prog" | jq -r '.[] | "\(.id) \(.assignedTo // "?") \(.description[0:50])"' 2>/dev/null | while read tid agent desc; do
            printf "  \033[1;36m[\033[0m%s\033[1;36m]\033[0m %-10s %s\n" "$tid" "$agent" "$desc"
        done
    else
        printf "  \033[1;30m(none)\033[0m\n"
    fi
    
    echo ""
    printf "\033[1;31m⚠ ATTENTION REQUIRED:\033[0m %s\n" "$(get_tasks_requiring_attention)"
    
    echo ""
    printf "\033[1;35m━━━ GIT CHANGES ━━━\033[0m\n"
    modified=$(get_modified_files)
    if [ -n "$modified" ]; then
        local count=$(echo "$modified" | wc -l)
        printf "  \033[1;33m%d files modified:\033[0m\n" "$count"
        echo "$modified" | head -8 | while read f; do
            printf "    \033[1;36m•\033[0m %s\n" "$f"
        done
        if [ $count -gt 8 ]; then
            printf "    \033[1;30m... and %d more\033[0m\n" $((count - 8))
        fi
    else
        printf "  \033[1;32mClean - no modified files\033[0m\n"
    fi
    
    echo ""
    printf "\033[1;33mRecent commits:\033[0m\n"
    get_recent_commits | while read line; do
        printf "  %s\n" "$line" | cut -c1-75
    done
    
    echo ""
    printf "\033[1;32mCompleted:\033[0m "
    cat "$STATE_FILE" 2>/dev/null | jq -r '[.tasks[] | select(.status == "completed")] | 
sort_by(.completedAt) | reverse | .[0:5] | 
"[" + .id + "]" + (.assignedTo // "?")' 2>/dev/null | tr '\n' ' '
    echo ""
}

if [ $INTERACTIVE -eq 1 ]; then
    while true; do
        draw
        printf "\n  \033[1;34mRefreshing every %ds... Ctrl+C to exit\033[0m" "$REFRESH"
        sleep $REFRESH
        printf "\033[2A\033[J"
    done
else
    draw
fi