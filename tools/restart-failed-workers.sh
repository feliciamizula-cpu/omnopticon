#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/agent-state-lib.sh"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"

usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS]

Restart failed workers by resetting their state and allowing the supervisor to reassign tasks.

OPTIONS:
    -a, --agent ID     Restart specific agent (e.g., agent-1, devops-1)
    -r, --reset        Reset to idle instead of respawning
    -l, --list         List failed/crashed agents
    -h, --help        Show this help

EXAMPLES:
    $(basename "$0") -l                     # List all failed agents
    $(basename "$0") -a agent-1             # Restart agent-1
    $(basename "$0") -a agent-1 -r          # Reset agent-1 to idle
    $(basename "$0")                        # Restart all crashed agents

EOF
}

list_failed() {
    echo "=== Failed Agent Status ==="
    for state_file in "$SCRIPT_DIR"/.agent-state-*.json; do
        [ -f "$state_file" ] || continue
        agent_id=$(basename "$state_file" | sed 's/.agent-state-//' | sed 's/.json//')
        state_json=$(agent_read_state "$agent_id")
        status=$(echo "$state_json" | jq -r '.status // "unknown"')
        runtime=$(agent_runtime_status "$agent_id")
        current_task=$(echo "$state_json" | jq -r '.currentTaskId // empty')
        last_error=$(echo "$state_json" | jq -r '.lastError // empty')
        
        if [ "$status" = "crashed" ] || [ "$status" = "stalled" ] || [ "$runtime" = "crashed" ]; then
            echo ""
            echo "Agent: $agent_id"
            echo "  Status: $status"
            echo "  Runtime: $runtime"
            echo "  Current Task: ${current_task:-none}"
            echo "  Last Error: ${last_error:-none}"
        fi
    done
}

reset_agent() {
    local agent_id="$1"
    local state_json now
    
    state_json="$(agent_read_state "$agent_id")"
    now="$(agent_now_utc)"
    
    updated="$(echo "$state_json" | jq \
        --arg now "$now" \
        --arg task_id "$(echo "$state_json" | jq -r '.currentTaskId // empty')" '
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
    agent_write_state "$agent_id" "$updated"
    
    if [ -f "$STATE_FILE" ]; then
        jq --arg agent "$agent_id" \
           --arg now "$now" '
           .agents |= map(
               if .id == $agent then
                   .status = "active" |
                   .workStatus = "idle" |
                   .currentTask = null |
                   .currentTaskDescription = null |
                   .pid = null |
                   .lastHeartbeatAt = $now |
                   .lastUpdated = $now |
                   .lastError = null
               else .
               end
           )
        ' "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
    fi
    
    echo "Reset $agent_id to idle"
}

restart_agent() {
    local agent_id="$1"
    local task_id
    
    reset_agent "$agent_id"
    
    task_id=$(jq -r ".tasks[] | select(.assignedTo == \"$agent_id\" and .status == \"in_progress\") | .id" "$STATE_FILE" 2>/dev/null | head -1)
    if [ -n "$task_id" ]; then
        echo "Task $task_id will be reassigned by supervisor"
    fi
}

restart_all_crashed() {
    echo "=== Restarting All Crashed Agents ==="
    for state_file in "$SCRIPT_DIR"/.agent-state-*.json; do
        [ -f "$state_file" ] || continue
        agent_id=$(basename "$state_file" | sed 's/.agent-state-//' | sed 's/.json//')
        state_json=$(agent_read_state "$agent_id")
        status=$(echo "$state_json" | jq -r '.status // "unknown"')
        
        if [ "$status" = "crashed" ] || [ "$status" = "stalled" ]; then
            restart_agent "$agent_id"
        fi
    done
    
    supervisor_pid=$(cat "$SCRIPT_DIR/.agent-supervisor.pid" 2>/dev/null || echo "")
    if [ -n "$supervisor_pid" ] && kill -0 "$supervisor_pid" 2>/dev/null; then
        echo "Supervisor is running (PID: $supervisor_pid)"
    else
        echo "WARNING: Supervisor is not running"
    fi
}

AGENT=""
RESET_ONLY=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--agent)
            AGENT="$2"
            shift 2
            ;;
        -r|--reset)
            RESET_ONLY=true
            shift
            ;;
        -l|--list)
            list_failed
            exit 0
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

if [ -n "$AGENT" ]; then
    if [ "$RESET_ONLY" = true ]; then
        reset_agent "$AGENT"
    else
        restart_agent "$AGENT"
    fi
else
    restart_all_crashed
fi