#!/bin/bash

AGENT_STATE_DIR="${AGENT_STATE_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"

agent_now_utc() {
    date -u +"%Y-%m-%dT%H:%M:%S.%3NZ"
}

agent_state_path() {
    local agent_id="$1"
    echo "$AGENT_STATE_DIR/.agent-state-$agent_id.json"
}

agent_default_state_json() {
    local agent_id="$1"
    jq -n \
        --arg agentId "$agent_id" \
        --arg now "$(agent_now_utc)" \
        '{
            agentId: $agentId,
            currentTaskId: null,
            currentTaskDescription: null,
            context: null,
            status: "idle",
            pid: null,
            startedAt: null,
            lastRunAt: $now,
            lastHeartbeatAt: $now,
            lastError: null,
            updatedAt: $now
        }'
}

agent_read_state() {
    local agent_id="$1"
    local state_file
    state_file="$(agent_state_path "$agent_id")"

    if [ -s "$state_file" ] && jq -e . "$state_file" >/dev/null 2>&1; then
        cat "$state_file"
    else
        agent_default_state_json "$agent_id"
    fi
}

agent_write_state() {
    local agent_id="$1"
    local state_json="$2"
    local state_file
    state_file="$(agent_state_path "$agent_id")"

    local tmp_file
    tmp_file="$(mktemp "${state_file}.tmp.XXXXXX")"
    printf '%s\n' "$state_json" > "$tmp_file"
    mv "$tmp_file" "$state_file"
}

agent_update_state() {
    local agent_id="$1"
    local jq_filter="$2"
    local state_json
    state_json="$(agent_read_state "$agent_id")"
    echo "$state_json" | jq "$jq_filter"
}

agent_pid_is_running() {
    local pid="$1"
    if [ -z "$pid" ] || [ "$pid" = "null" ]; then
        return 1
    fi

    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" >/dev/null 2>&1
}

agent_status_age_seconds() {
    local timestamp="$1"
    if [ -z "$timestamp" ] || [ "$timestamp" = "null" ] || [ "$timestamp" = "never" ]; then
        echo 999999
        return
    fi

    local epoch
    epoch=$(date -d "$timestamp" +%s 2>/dev/null || echo 0)
    local now_epoch
    now_epoch=$(date +%s)
    echo $((now_epoch - epoch))
}

agent_runtime_status() {
    local agent_id="$1"
    local state_json pid status heartbeat_age
    state_json="$(agent_read_state "$agent_id")"
    pid="$(echo "$state_json" | jq -r '.pid // empty')"
    status="$(echo "$state_json" | jq -r '.status // "idle"')"
    heartbeat_age="$(agent_status_age_seconds "$(echo "$state_json" | jq -r '.lastHeartbeatAt // empty')")"

    if [ "$status" = "idle" ]; then
        echo "idle"
        return
    fi

    if agent_pid_is_running "$pid"; then
        local heartbeat_threshold
        heartbeat_threshold="${AGENT_HEARTBEAT_TIMEOUT:-60}"
        if [ "$heartbeat_age" -gt "$heartbeat_threshold" ]; then
            echo "unresponsive"
        else
            echo "running"
        fi
        return
    fi

    case "$status" in
        working|starting)
            echo "crashed"
            ;;
        stalled|blocked)
            echo "$status"
            ;;
        *)
            echo "$status"
            ;;
    esac
}
