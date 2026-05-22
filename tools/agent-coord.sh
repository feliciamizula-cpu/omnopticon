#!/bin/bash
set -e

STATE_FILE="$(dirname "$0")/.agent-tasks.json"
LOCK_FILE="$(dirname "$0")/.agent-tasks.lock"
AGENT_ID="${AGENT_ID:-agent-1}"

acquire_lock() {
    local max_attempts=30
    local attempt=1
    while [ $attempt -le $max_attempts ]; do
        if mkdir "$LOCK_FILE" 2>/dev/null; then
            return 0
        fi
        sleep 0.1
        attempt=$((attempt + 1))
    done
    echo "Failed to acquire lock after $max_attempts attempts"
    exit 1
}

release_lock() {
    rmdir "$LOCK_FILE" 2>/dev/null || true
}

read_state() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE"
    else
        echo "null"
    fi
}

write_state() {
    local state="$1"
    echo "$state" > "$STATE_FILE"

    (
        cd "$(dirname "$0")"
        if git rev-parse --git-dir > /dev/null 2>&1; then
            git add .agent-tasks.json 2>/dev/null || true
            if git diff --cached --quiet; then
                :
            else
                git commit -m "chore: update agent coordination state [skip ci]" 2>/dev/null || true
                git push 2>/dev/null || true
            fi
        fi
    )
}

init_state() {
    acquire_lock
    trap release_lock EXIT

    cat > "$STATE_FILE" << 'EOF'
{
    "agents": [
        { "id": "agent-1", "name": "Agent 1", "status": "active", "currentTask": null, "lastUpdated": "" },
        { "id": "agent-2", "name": "Agent 2", "status": "inactive", "currentTask": null, "lastUpdated": "" }
    ],
    "tasks": [],
    "version": "1.0"
}
EOF

    cd "$(dirname "$0")"
    git add .agent-tasks.json 2>/dev/null || true
    git commit -m "chore: update agent coordination state [skip ci]" 2>/dev/null || true
    git push 2>/dev/null || true

    echo "Initialized coordination state with 2 agents"
}

list_tasks() {
    if [ ! -f "$STATE_FILE" ]; then
        echo "No state file. Run: agent-coord.sh init"
        return
    fi

    echo "=== Tasks ==="

    local task_lines=$(cat "$STATE_FILE" | jq -r '.tasks[] | @json' 2>/dev/null)
    if [ -z "$task_lines" ]; then
        echo "(no tasks)"
    else
        while IFS= read -r task; do
            local id=$(echo "$task" | jq -r '.id')
            local status=$(echo "$task" | jq -r '.status')
            local priority=$(echo "$task" | jq -r '.priority')
            local desc=$(echo "$task" | jq -r '.description')
            printf "[%-3s] %-12s %-8s %s\n" "$id" "$status" "$priority" "$desc"
        done <<< "$task_lines"
    fi

    echo ""
    echo "=== Agents ==="

    local agent_lines=$(cat "$STATE_FILE" | jq -r '.agents[] | @json' 2>/dev/null)
    if [ -z "$agent_lines" ]; then
        echo "(no agents)"
    else
        while IFS= read -r agent; do
            local name=$(echo "$agent" | jq -r '.name')
            local id=$(echo "$agent" | jq -r '.id')
            local status=$(echo "$agent" | jq -r '.status')
            local current=$(echo "$agent" | jq -r '.currentTask // empty')
            local task_info=$( [ -n "$current" ] && echo "working on: $current" || echo "idle" )
            echo "$name ($id): $status - $task_info"
        done <<< "$agent_lines"
    fi
}

add_task() {
    if [ -z "$DESCRIPTION" ]; then
        echo "Error: DESCRIPTION required for 'add'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state=$(read_state)
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    local task_count=$(echo "$state" | jq '.tasks | length')
    local new_id=$(printf "%03d" $((task_count + 1)))

    local new_task=$(cat << EOF
{
    "id": "$new_id",
    "description": "$DESCRIPTION",
    "priority": "${PRIORITY:-medium}",
    "status": "pending",
    "assignedTo": null,
    "createdAt": "$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")"
}
EOF
)

    local new_state=$(echo "$state" | jq ".tasks += [$new_task]")
    write_state "$new_state"
    echo "Added task [$new_id]: $DESCRIPTION"
}

take_task() {
    if [ -z "$TASK_ID" ]; then
        echo "Error: TASK_ID required for 'take'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state=$(read_state)
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    local task_json=$(echo "$state" | jq ".tasks[] | select(.id == \"$TASK_ID\")")
    if [ -z "$task_json" ] || [ "$task_json" = "null" ]; then
        echo "Task [$TASK_ID] not found"
        exit 1
    fi

    local current_status=$(echo "$state" | jq -r ".tasks[] | select(.id == \"$TASK_ID\") | .status")
    if [ "$current_status" = "in_progress" ]; then
        echo "Task [$TASK_ID] is already in progress"
        exit 1
    fi

    local new_state=$(echo "$state" | jq \
        --arg tid "$TASK_ID" \
        --arg agent "$AGENT_ID" \
        --arg now "$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")" \
        '.tasks |= map(if .id == $tid then .status = "in_progress" | .assignedTo = $agent else . end) |
         .agents |= map(if .currentTask == $tid or .id == $agent then .currentTask = $tid | .lastUpdated = $now else . end)')

    write_state "$new_state"
    echo "Agent $AGENT_ID took task [$TASK_ID]"
}

complete_task() {
    if [ -z "$TASK_ID" ]; then
        echo "Error: TASK_ID required for 'done'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state=$(read_state)
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    local task_json=$(echo "$state" | jq ".tasks[] | select(.id == \"$TASK_ID\")")
    if [ -z "$task_json" ] || [ "$task_json" = "null" ]; then
        echo "Task [$TASK_ID] not found"
        exit 1
    fi

    local new_state=$(echo "$state" | jq \
        --arg tid "$TASK_ID" \
        --arg now "$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")" \
        '.tasks |= map(if .id == $tid then .status = "completed" | .completedAt = $now else . end) |
         .agents |= map(if .currentTask == $tid then .currentTask = null | .lastUpdated = $now else . end)')

    write_state "$new_state"
    echo "Completed task [$TASK_ID]"
}

show_status() {
    local state=$(read_state)
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file"
        exit 1
    fi

    local pending=$(echo "$state" | jq '[.tasks[] | select(.status == "pending")] | length')
    local in_progress=$(echo "$state" | jq '[.tasks[] | select(.status == "in_progress")] | length')
    local completed=$(echo "$state" | jq '[.tasks[] | select(.status == "completed")] | length')

    echo "Status: $pending pending, $in_progress in progress, $completed completed"
}

show_help() {
    echo "Usage: agent-coord.sh <action> [options]"
    echo ""
    echo "Actions:"
    echo "  init              Initialize the coordination state"
    echo "  list              List all tasks and agents"
    echo "  add               Add a new task"
    echo "  take <task-id>     Claim a task for this agent"
    echo "  done <task-id>    Mark a task as completed"
    echo "  status            Show summary status"
    echo ""
    echo "Options:"
    echo "  -d, --description    Task description (for 'add')"
    echo "  -p, --priority       Priority: high, medium, low (for 'add')"
    echo "  -t, --task-id         Task ID (for 'take' or 'done')"
    echo "  -a, --agent-id        Agent ID (default: agent-1)"
    echo "  -h, --help            Show this help"
}

ACTION=""
TASK_ID=""
DESCRIPTION=""
PRIORITY="medium"

while [[ $# -gt 0 ]]; do
    case $1 in
        init|list|add|take|done|status)
            ACTION="$1"
            shift
            ;;
        -t|--task-id)
            TASK_ID="$2"
            shift 2
            ;;
        -d|--description)
            DESCRIPTION="$2"
            shift 2
            ;;
        -p|--priority)
            PRIORITY="$2"
            shift 2
            ;;
        -a|--agent-id)
            AGENT_ID="$2"
            shift 2
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            show_help
            exit 1
            ;;
    esac
done

case "$ACTION" in
    init)       init_state ;;
    list)       list_tasks ;;
    add)        add_task ;;
    take)       take_task ;;
    done)       complete_task ;;
    status)     show_status ;;
    *)          show_help; exit 1 ;;
esac