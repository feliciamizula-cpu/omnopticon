#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/agent-state-lib.sh"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
LOCK_FILE="$SCRIPT_DIR/.agent-tasks.lock"
AGENT_ID="${AGENT_ID:-agent-1}"
FORCE_TAKE="${FORCE_TAKE:-0}"

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
    local tmp_file
    tmp_file="$(mktemp "${STATE_FILE}.tmp.XXXXXX")"
    printf '%s\n' "$state" > "$tmp_file"
    mv "$tmp_file" "$STATE_FILE"

    if [ "${AGENT_COORD_AUTOCOMMIT:-0}" = "1" ]; then
        (
            cd "$SCRIPT_DIR"
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
    fi
}

default_agents_json() {
    jq -n '
        [
            { "id": "agent-1", "name": "Agent 1", "role": "development", "status": "active", "responsibilities": ["application implementation"] },
            { "id": "agent-2", "name": "Agent 2", "role": "development", "status": "inactive", "responsibilities": ["application implementation"] },
            { "id": "agent-3", "name": "Agent 3", "role": "development", "status": "inactive", "responsibilities": ["application implementation"] },
            { "id": "agent-4", "name": "Agent 4", "role": "development", "status": "inactive", "responsibilities": ["application implementation"] },
            { "id": "agent-5", "name": "Agent 5", "role": "development", "status": "inactive", "responsibilities": ["application implementation"] },
            { "id": "devops-1", "name": "DevOps Agent 1", "role": "devops", "status": "active", "responsibilities": ["application health", "deployed component health", "release readiness"] },
            { "id": "devops-2", "name": "DevOps Agent 2", "role": "devops", "status": "active", "responsibilities": ["AI agent health", "coordination system health", "task-board hygiene"] }
        ] | map(. + {
            currentTask: null,
            workStatus: "idle",
            lastUpdated: "",
            pid: null,
            lastHeartbeatAt: null,
            currentTaskDescription: null,
            context: null,
            lastError: null
        })
    '
}

init_state() {
    acquire_lock
    trap release_lock EXIT

    cat > "$STATE_FILE" <<EOF
{
    "agents": $(default_agents_json),
    "tasks": [],
    "version": "1.0"
}
EOF

    write_state "$(cat "$STATE_FILE")"
    echo "Initialized coordination state with 7 agents"
}

list_tasks() {
    if [ ! -f "$STATE_FILE" ]; then
        echo "No state file. Run: agent-coord.sh init"
        return
    fi

    local state
    state="$(read_state)"
    state="$(echo "$state" | jq --arg now "$(agent_now_utc)" '
        .agents |= map(
            .status = (.status // "inactive") |
            .currentTask = (.currentTask // null) |
            .lastUpdated = (.lastUpdated // $now) |
            .pid = (.pid // null) |
            .lastHeartbeatAt = (.lastHeartbeatAt // null) |
            .currentTaskDescription = (.currentTaskDescription // null) |
            .context = (.context // null) |
            .lastError = (.lastError // null)
        )
    ')"

    echo "=== Tasks ==="
    local task_lines
    task_lines=$(echo "$state" | jq -r '.tasks[] | @json' 2>/dev/null || true)
    if [ -z "$task_lines" ]; then
        echo "(no tasks)"
    else
        while IFS= read -r task; do
            [ -n "$task" ] || continue
            local id status priority desc assigned
            id=$(echo "$task" | jq -r '.id')
            status=$(echo "$task" | jq -r '.status')
            priority=$(echo "$task" | jq -r '.priority')
            desc=$(echo "$task" | jq -r '.description')
            assigned=$(echo "$task" | jq -r '.assignedTo // "-"')
            printf "[%-3s] %-12s %-8s %-10s %s\n" "$id" "$status" "$priority" "$assigned" "$desc"
        done <<< "$task_lines"
    fi

    echo ""
    echo "=== Agents ==="
    local agent_lines
    agent_lines=$(echo "$state" | jq -r '.agents[] | @json' 2>/dev/null || true)
    if [ -z "$agent_lines" ]; then
        echo "(no agents)"
    else
        while IFS= read -r agent; do
            [ -n "$agent" ] || continue
            local name id status current pid heartbeat runtime context
            name=$(echo "$agent" | jq -r '.name')
            id=$(echo "$agent" | jq -r '.id')
            status=$(echo "$agent" | jq -r '.status')
            current=$(echo "$agent" | jq -r '.currentTask // empty')
            pid=$(echo "$agent" | jq -r '.pid // empty')
            heartbeat=$(echo "$agent" | jq -r '.lastHeartbeatAt // "-"')
            context=$(echo "$agent" | jq -r '.context // "-"')
            runtime=$(agent_runtime_status "$id")
            printf "%-12s (%-8s) %-10s task=%-8s runtime=%-12s pid=%-7s heartbeat=%-24s\n" \
                "$name" "$id" "$status" "${current:--}" "$runtime" "${pid:--}" "$heartbeat"
            if [ "$context" != "-" ] && [ -n "$context" ]; then
                printf "  context: %s\n" "$(echo "$context" | cut -c1-120)"
            fi
        done <<< "$agent_lines"
    fi
}

add_task() {
    if [ -z "${DESCRIPTION:-}" ]; then
        echo "Error: DESCRIPTION required for 'add'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    local task_count new_id new_task new_state
    task_count=$(echo "$state" | jq '.tasks | length')
    new_id=$(printf "%03d" $((task_count + 1)))

    new_task=$(jq -n \
        --arg id "$new_id" \
        --arg description "$DESCRIPTION" \
        --arg priority "${PRIORITY:-medium}" \
        --arg now "$(agent_now_utc)" \
        '{
            id: $id,
            description: $description,
            priority: $priority,
            status: "pending",
            assignedTo: null,
            attempts: 0,
            createdAt: $now,
            claimedAt: null,
            completedAt: null
        }')

    new_state=$(echo "$state" | jq --argjson task "$new_task" '.tasks += [$task]')
    write_state "$new_state"
    echo "Added task [$new_id]: $DESCRIPTION"
}

take_task() {
    if [ -z "${TASK_ID:-}" ]; then
        echo "Error: TASK_ID required for 'take'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state task_json current_status task_description now new_state
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    task_json=$(echo "$state" | jq ".tasks[] | select(.id == \"$TASK_ID\")")
    if [ -z "$task_json" ] || [ "$task_json" = "null" ]; then
        echo "Task [$TASK_ID] not found"
        exit 1
    fi

    current_status=$(echo "$state" | jq -r ".tasks[] | select(.id == \"$TASK_ID\") | .status")
    if [ "$current_status" = "in_progress" ] && [ "$FORCE_TAKE" != "1" ]; then
        echo "Task [$TASK_ID] is already in progress"
        exit 1
    fi

    task_description=$(echo "$state" | jq -r ".tasks[] | select(.id == \"$TASK_ID\") | .description")
    now="$(agent_now_utc)"

    new_state=$(echo "$state" | jq \
        --arg tid "$TASK_ID" \
        --arg agent "$AGENT_ID" \
        --arg desc "$task_description" \
        --arg now "$now" \
        '.tasks |= map(
            if .id == $tid then
                .status = "in_progress" |
                .assignedTo = $agent |
                .claimedAt = $now |
                .attempts = ((.attempts // 0) + 1)
            else
                .
            end
        ) |
         .agents |= map(
            if .id == $agent then
                .status = (.status // "active") |
                .workStatus = "working" |
                .currentTask = $tid |
                .currentTaskDescription = $desc |
                .lastUpdated = $now |
                .lastRunAt = $now |
                .lastHeartbeatAt = $now |
                .lastError = null
            else
                .
            end
         )')

    write_state "$new_state"
    echo "Agent $AGENT_ID took task [$TASK_ID]"
}

complete_task() {
    if [ -z "${TASK_ID:-}" ]; then
        echo "Error: TASK_ID required for 'done'"
        exit 1
    fi

    acquire_lock
    trap release_lock EXIT

    local state task_json now new_state
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file. Run: agent-coord.sh init"
        exit 1
    fi

    task_json=$(echo "$state" | jq ".tasks[] | select(.id == \"$TASK_ID\")")
    if [ -z "$task_json" ] || [ "$task_json" = "null" ]; then
        echo "Task [$TASK_ID] not found"
        exit 1
    fi

    now="$(agent_now_utc)"
    new_state=$(echo "$state" | jq \
        --arg tid "$TASK_ID" \
        --arg now "$now" \
        '.tasks |= map(
            if .id == $tid then
                .status = "completed" |
                .completedAt = $now
            else
                .
            end
        ) |
         .agents |= map(
            if .currentTask == $tid or .currentTaskId == $tid then
                .currentTask = null |
                .currentTaskDescription = null |
                .context = null |
                .status = (.status // "active") |
                .workStatus = "idle" |
                .pid = null |
                .startedAt = null |
                .lastHeartbeatAt = $now |
                .lastRunAt = $now |
                .lastUpdated = $now |
                .lastError = null
            else
                .
            end
         )')

    write_state "$new_state"
    echo "Completed task [$TASK_ID]"
}

checkpoint_agent() {
    acquire_lock
    trap release_lock EXIT

    local state current_task_id current_task_desc context status pid last_error now updated
    state="$(agent_read_state "$AGENT_ID")"
    current_task_id="${TASK_ID:-$(echo "$state" | jq -r '.currentTaskId // empty')}"
    current_task_desc="${DESCRIPTION:-$(echo "$state" | jq -r '.currentTaskDescription // empty')}"
    context="${CONTEXT:-$(echo "$state" | jq -r '.context // empty')}"
    status="${STATUS:-$(echo "$state" | jq -r '.status // "idle"')}"
    pid="${PID:-$(echo "$state" | jq -r '.pid // empty')}"
    last_error="${LAST_ERROR:-$(echo "$state" | jq -r '.lastError // empty')}"
    now="$(agent_now_utc)"

    updated=$(echo "$state" | jq \
        --arg agent "$AGENT_ID" \
        --arg task_id "$current_task_id" \
        --arg task_desc "$current_task_desc" \
        --arg context "$context" \
        --arg status "$status" \
        --arg pid "$pid" \
        --arg last_error "$last_error" \
        --arg now "$now" '
        .agentId = $agent |
        .currentTaskId = (if $task_id == "" then null else $task_id end) |
        .currentTaskDescription = (if $task_desc == "" then null else $task_desc end) |
        .context = (if $context == "" then null else $context end) |
        .status = $status |
        .pid = (if $pid == "" then null else ($pid | tonumber?) end) |
        .lastError = (if $last_error == "" then null else $last_error end) |
        .lastHeartbeatAt = $now |
        .lastRunAt = (.lastRunAt // $now) |
        .updatedAt = $now
    ')
    agent_write_state "$AGENT_ID" "$updated"
    echo "Updated agent $AGENT_ID: status=$status task=${current_task_id:-none}"
}

reconcile_state() {
    acquire_lock
    trap release_lock EXIT

    local state now agent_ids agent_id local_state local_status local_task central_task task_desc updated local_runtime local_context
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file"
        exit 1
    fi

    now="$(agent_now_utc)"
    agent_ids="$(echo "$state" | jq -r '.agents[].id')"

    while IFS= read -r agent_id; do
        [ -n "$agent_id" ] || continue
        local_state="$(agent_read_state "$agent_id")"
        local_status="$(echo "$local_state" | jq -r '.status // "idle"')"
        local_task="$(echo "$local_state" | jq -r '.currentTaskId // empty')"
        central_task="$(echo "$state" | jq -r --arg agent "$agent_id" '.agents[] | select(.id == $agent) | .currentTask // empty')"
        local_runtime="$(agent_runtime_status "$agent_id")"

        if [ -n "$local_task" ] && [ "$local_status" != "idle" ]; then
            task_desc="$(echo "$state" | jq -r --arg tid "$local_task" '.tasks[] | select(.id == $tid) | .description // empty')"
            if [ "$local_runtime" = "crashed" ] || [ "$local_runtime" = "stalled" ] || [ "$local_runtime" = "unresponsive" ]; then
                local_context="$(echo "$local_state" | jq -r '.context // empty')"
                state="$(echo "$state" | jq \
                    --arg agent "$agent_id" \
                    --arg tid "$local_task" \
                    --arg context "$local_context" \
                    --arg runtime "$local_runtime" \
                    --arg now "$now" '
                    .agents |= map(
                        if .id == $agent then
                            .status = (if .status == "inactive" then "inactive" else "active" end) |
                            .workStatus = "idle" |
                            .currentTask = null |
                            .currentTaskDescription = null |
                            .lastUpdated = $now
                        else
                            .
                        end
                    ) |
                    .tasks |= map(
                        if .id == $tid then
                            .status = "pending" |
                            .assignedTo = null |
                            .requeuedAt = $now |
                            .requeueReason = ("released from " + $agent + " after " + $runtime) |
                            .recoveryContext = (if $context == "" then null else $context end)
                        else
                            .
                        end
                    )
                ')"
                updated="$(echo "$local_state" | jq \
                    --arg now "$now" \
                    --arg tid "$local_task" '
                    .currentTaskId = null |
                    .currentTaskDescription = null |
                    .status = "idle" |
                    .pid = null |
                    .lastError = ("Task " + $tid + " requeued during reconciliation") |
                    .lastHeartbeatAt = $now |
                    .updatedAt = $now
                ')"
                agent_write_state "$agent_id" "$updated"
                continue
            fi
            updated="$(echo "$local_state" | jq \
                --arg agent "$agent_id" \
                --arg tid "$local_task" \
                --arg desc "$task_desc" \
                --arg now "$now" '
                .agentId = $agent |
                .currentTaskId = $tid |
                .currentTaskDescription = (if $desc == "" then .currentTaskDescription else $desc end) |
                .updatedAt = $now
            ')"
            agent_write_state "$agent_id" "$updated"
            state="$(echo "$state" | jq \
                --arg agent "$agent_id" \
                --arg tid "$local_task" \
                --arg desc "$task_desc" \
                --arg now "$now" '
                .agents |= map(
                    if .id == $agent then
                        .status = (if .status == "inactive" then "inactive" else "active" end) |
                        .workStatus = "working" |
                        .currentTask = $tid |
                        .currentTaskDescription = (if $desc == "" then null else $desc end) |
                        .lastUpdated = $now
                    else
                        .
                    end
                ) |
                .tasks |= map(
                    if .id == $tid then
                        .status = "in_progress" |
                        .assignedTo = $agent
                    else
                        .
                    end
                )
            ')"
        elif [ -n "$central_task" ]; then
            task_desc="$(echo "$state" | jq -r --arg tid "$central_task" '.tasks[] | select(.id == $tid) | .description // empty')"
            updated="$(echo "$local_state" | jq \
                --arg agent "$agent_id" \
                --arg tid "$central_task" \
                --arg desc "$task_desc" \
                --arg now "$now" '
                .agentId = $agent |
                .currentTaskId = $tid |
                .currentTaskDescription = (if $desc == "" then null else $desc end) |
                .status = "working" |
                .pid = null |
                .lastError = "Reconstructed from central task board during reconciliation" |
                .lastHeartbeatAt = $now |
                .updatedAt = $now
            ')"
            agent_write_state "$agent_id" "$updated"
        else
            updated="$(echo "$local_state" | jq \
                --arg agent "$agent_id" \
                --arg now "$now" '
                .agentId = $agent |
                .currentTaskId = null |
                .currentTaskDescription = null |
                .status = "idle" |
                .pid = null |
                .updatedAt = $now
            ')"
            agent_write_state "$agent_id" "$updated"
            state="$(echo "$state" | jq \
                --arg agent "$agent_id" \
                --arg now "$now" '
                .agents |= map(
                    if .id == $agent then
                        .status = (if .status == "inactive" then "inactive" else "active" end) |
                        .workStatus = "idle" |
                        .currentTask = null |
                        .currentTaskDescription = null |
                        .lastUpdated = $now
                    else
                        .
                    end
                )
            ')"
        fi
    done <<< "$agent_ids"

    state="$(echo "$state" | jq --arg now "$now" '
        . as $root |
        .agents |= map(
            .status = (if .status == "inactive" then "inactive" else "active" end) |
            .workStatus = (.workStatus // (if .currentTask == null then "idle" else "working" end))
        ) |
        .tasks |= map(
            . as $task |
            if $task.status == "in_progress" and ($task.assignedTo != null) then
                ([ $root.agents[] | select(.id == $task.assignedTo) | .currentTask ][0]) as $currentTask |
                if $currentTask != $task.id then
                    .status = "pending" |
                    .assignedTo = null |
                    .requeuedAt = $now |
                    .requeueReason = "assigned agent is not currently working this task"
                else
                    .
                end
            else
                .
            end
        )
    ')"

    write_state "$state"
    echo "Reconciled agent task board"
}

doctor_agents() {
    local state
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file"
        exit 1
    fi

    echo "=== Agent Doctor ==="
    local issues=0

    while IFS= read -r agent_id; do
        [ -n "$agent_id" ] || continue
        local state_file runtime central_task local_state local_task local_status
        state_file="$(agent_state_path "$agent_id")"
        runtime="$(agent_runtime_status "$agent_id")"
        central_task="$(echo "$state" | jq -r --arg agent "$agent_id" '.agents[] | select(.id == $agent) | .currentTask // "-"')"
        local_state="$(agent_read_state "$agent_id")"
        local_task="$(echo "$local_state" | jq -r '.currentTaskId // "-"')"
        local_status="$(echo "$local_state" | jq -r '.status // "idle"')"

        if [ ! -s "$state_file" ]; then
            echo "WARN $agent_id: missing or empty local state file"
            issues=$((issues + 1))
        fi
        if [ "$central_task" != "$local_task" ] && { [ "$central_task" != "-" ] || [ "$local_task" != "-" ]; }; then
            echo "WARN $agent_id: central task=$central_task local task=$local_task local status=$local_status runtime=$runtime"
            issues=$((issues + 1))
        fi
    done <<< "$(echo "$state" | jq -r '.agents[].id')"

    echo "$state" | jq -r '
        . as $root |
        .tasks[] |
        select(.status == "in_progress" and (.assignedTo == null or .assignedTo == "")) |
        "WARN task \(.id): in_progress with no assignee"
    ' | while read -r line; do
        echo "$line"
    done

    if [ "$issues" -eq 0 ]; then
        echo "No local/central agent mismatches detected"
    fi
}

show_status() {
    local state
    state="$(read_state)"
    if [ "$state" = "null" ] || [ -z "$state" ]; then
        echo "No state file"
        exit 1
    fi

    local pending in_progress completed active
    pending=$(echo "$state" | jq '[.tasks[] | select(.status == "pending")] | length')
    in_progress=$(echo "$state" | jq '[.tasks[] | select(.status == "in_progress")] | length')
    completed=$(echo "$state" | jq '[.tasks[] | select(.status == "completed")] | length')
    active=$(echo "$state" | jq '[.agents[] | select(.status == "active")] | length')
    echo "Status: $pending pending, $in_progress in progress, $completed completed, $active active agents"
    echo ""
    echo "Agents:"
    echo "$state" | jq -r '.agents[] | [.id, .status, (.currentTask // "-"), (.pid // "-"), (.lastHeartbeatAt // "-"), (.currentTaskDescription // "-")] | @tsv' | \
        while IFS=$'\t' read -r id status current_task pid heartbeat desc; do
            local runtime local_state local_pid local_heartbeat local_desc
            runtime="$(agent_runtime_status "$id")"
            local_state="$(agent_read_state "$id")"
            local_pid="$(echo "$local_state" | jq -r '.pid // "-"')"
            local_heartbeat="$(echo "$local_state" | jq -r '.lastHeartbeatAt // "-"')"
            local_desc="$(echo "$local_state" | jq -r '.currentTaskDescription // empty')"
            if [ -n "$local_desc" ]; then
                desc="$local_desc"
            fi
            if [ "$local_pid" != "-" ]; then
                pid="$local_pid"
            fi
            if [ "$local_heartbeat" != "-" ]; then
                heartbeat="$local_heartbeat"
            fi
            printf "  %-10s %-12s %-10s pid=%-7s heartbeat=%-24s %s\n" \
                "$id" "$status/$runtime" "$current_task" "$pid" "$heartbeat" "$desc"
        done

}

monitor_agents() {
    show_status
}

show_help() {
    echo "Usage: agent-coord.sh <action> [options]"
    echo ""
    echo "Actions:"
    echo "  init              Initialize the coordination state"
    echo "  list              List all tasks and agents"
    echo "  add               Add a new task"
    echo "  take <task-id>    Claim a task for this agent"
    echo "  done <task-id>    Mark a task as completed"
    echo "  checkpoint        Update agent status/context"
    echo "  reconcile         Repair central/local agent task mismatches"
    echo "  doctor            Diagnose agent todo/state health"
    echo "  monitor           Show agent/task health"
    echo "  status            Show summary status"
    echo ""
    echo "Options:"
    echo "  -d, --description    Task description (for 'add' or checkpoint context)"
    echo "  -p, --priority       Priority: high, medium, low (for 'add')"
    echo "  -t, --task-id        Task ID (for 'take', 'done', or checkpoint)"
    echo "  -a, --agent-id       Agent ID (default: agent-1)"
    echo "  -s, --status         Agent status for checkpoint"
    echo "  -c, --context        Agent context for checkpoint"
    echo "  -x, --pid            Runtime pid for checkpoint"
    echo "  -e, --error          Last error for checkpoint"
    echo "  -f, --force          Force-claim an in-progress task"
    echo "  -h, --help           Show this help"
}

ACTION=""
TASK_ID=""
DESCRIPTION=""
PRIORITY="medium"
STATUS=""
CONTEXT=""
PID=""
LAST_ERROR=""

while [[ $# -gt 0 ]]; do
    case $1 in
        init|list|add|take|done|checkpoint|reconcile|doctor|monitor|status)
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
        -s|--status)
            STATUS="$2"
            shift 2
            ;;
        -c|--context)
            CONTEXT="$2"
            shift 2
            ;;
        -x|--pid)
            PID="$2"
            shift 2
            ;;
        -e|--error)
            LAST_ERROR="$2"
            shift 2
            ;;
        -f|--force)
            FORCE_TAKE=1
            shift
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
    checkpoint) checkpoint_agent ;;
    reconcile)  reconcile_state ;;
    doctor)     doctor_agents ;;
    monitor)    monitor_agents ;;
    status)     show_status ;;
    *)          show_help; exit 1 ;;
esac
