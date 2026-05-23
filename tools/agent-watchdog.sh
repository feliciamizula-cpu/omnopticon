#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOG_FILE="${LOG_FILE:-/tmp/agent-supervisor.log}"
PID_FILE="${PID_FILE:-$SCRIPT_DIR/.agent-supervisor.pid}"
AUTO_RUN_SCRIPT="$SCRIPT_DIR/auto-run-agents.sh"
INTERVAL="${INTERVAL:-5}"
MAX_RESTART_INTERVAL="${MAX_RESTART_INTERVAL:-60}"

log() { echo "[$(date +'%Y-%m-%dT%H:%M:%S')] [watchdog] $*" >> "$LOG_FILE"; }

get_pid() {
    if [ -f "$PID_FILE" ]; then
        cat "$PID_FILE"
    fi
}

is_running() {
    local pid="$1"
    if [ -z "$pid" ] || [ "$pid" = "0" ]; then return 1; fi
    kill -0 "$pid" 2>/dev/null
}

start_supervisor() {
    log "Starting agent supervisor..."
    (
        exec "$AUTO_RUN_SCRIPT" "$INTERVAL"
    ) &
    echo $! > "$PID_FILE"
    log "Agent supervisor started with PID $(get_pid)"
}

stop_supervisor() {
    local pid
    pid="$(get_pid)"
    if [ -n "$pid" ] && is_running "$pid"; then
        log "Stopping agent supervisor (PID: $pid)..."
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    fi
    rm -f "$PID_FILE"
    log "Agent supervisor stopped"
}

restart_supervisor() {
    stop_supervisor
    sleep 2
    start_supervisor
}

signal_handler() {
    log "Received signal, shutting down..."
    stop_supervisor
    exit 0
}

trap signal_handler SIGTERM SIGINT SIGHUP

restart_count=0
restart_interval=5

log "========================================"
log "Agent watchdog starting"
log "Auto-run script: $AUTO_RUN_SCRIPT"
log "Interval: ${INTERVAL}s, Max restart interval: ${MAX_RESTART_INTERVAL}s"
log "========================================"

if [ ! -x "$AUTO_RUN_SCRIPT" ]; then
    log "ERROR: $AUTO_RUN_SCRIPT not found or not executable"
    exit 1
fi

start_supervisor

while true; do
    sleep 10

    local pid
    pid="$(get_pid)"

    if ! is_running "$pid"; then
        log "Agent supervisor not running (PID file: $pid)"

        if [ $restart_count -gt 0 ] && [ $(( $(date +%s) - last_death_time )) -lt 60 ]; then
            if [ $restart_interval -lt $MAX_RESTART_INTERVAL ]; then
                restart_interval=$((restart_interval * 2))
                [ $restart_interval -gt $MAX_RESTART_INTERVAL ] && restart_interval=$MAX_RESTART_INTERVAL
            fi
        else
            restart_interval=5
            restart_count=0
        fi

        log "Restarting in ${restart_interval}s... (count: $restart_count, interval: ${restart_interval}s)"
        sleep "$restart_interval"

        last_death_time=$(date +%s)
        restart_count=$((restart_count + 1))

        start_supervisor
    fi
done