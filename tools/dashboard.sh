#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/agent-state-lib.sh"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REFRESH="${1:-5}"
REVIEW_DIR="$SCRIPT_DIR/reviews"
CACHE_DIR="${DASHBOARD_CACHE_DIR:-/tmp/argus-agent-dashboard}"
FAST_CACHE_TTL="${FAST_CACHE_TTL:-2}"
BUILD_CACHE_TTL="${BUILD_CACHE_TTL:-300}"

if [ -t 0 ]; then
    INTERACTIVE=1
else
    INTERACTIVE=0
fi

ensure_cache_dir() {
    mkdir -p "$CACHE_DIR"
}

cache_file() {
    echo "$CACHE_DIR/$1"
}

cache_age_seconds() {
    local file="$1"
    if [ ! -f "$file" ]; then
        echo 999999
        return
    fi

    local modified now
    modified="$(stat -c %Y "$file" 2>/dev/null || echo 0)"
    now="$(date +%s)"
    echo $((now - modified))
}

cache_is_fresh() {
    local file="$1"
    local ttl="$2"
    [ "$(cache_age_seconds "$file")" -le "$ttl" ]
}

read_cache_or() {
    local file="$1"
    local fallback="$2"
    if [ -s "$file" ]; then
        cat "$file"
    else
        printf "%s\n" "$fallback"
    fi
}

pid_running() {
    local pid="$1"
    [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" >/dev/null 2>&1
}

refresh_in_flight() {
    local pid_file="$1"
    [ -f "$pid_file" ] && pid_running "$(cat "$pid_file" 2>/dev/null)"
}

get_modified_files_uncached() {
    git -C "$WORK_DIR" status --porcelain 2>/dev/null |
        grep -v "^??" |
        awk '{print $2}' |
        grep -v "^tools/\.agent" |
        grep -v "^tools/\.agent-prompt" |
        sort
}

get_recent_commits_uncached() {
    git -C "$WORK_DIR" log --oneline -5 2>/dev/null
}

get_latest_review() {
    if [ -d "$REVIEW_DIR" ]; then
        ls -t "$REVIEW_DIR"/*.md 2>/dev/null | head -1
    fi
}

get_review_summary_uncached() {
    local latest
    latest="$(get_latest_review)"
    if [ -n "$latest" ] && [ -f "$latest" ]; then
        local date reviewer findings
        date="$(head -1 "$latest" | grep -oE '[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}' || echo "unknown")"
        reviewer="$(grep "Reviewer:" "$latest" | head -1 | awk '{print $2}' || echo "?")"
        findings="$(grep -c "## Critical" "$latest" 2>/dev/null || true)"
        [ -n "$findings" ] || findings="0"
        echo "Last review: $date by $reviewer, $findings critical sections"
    else
        echo "No reviews yet"
    fi
}

refresh_fast_cache() {
    ensure_cache_dir
    get_modified_files_uncached > "$(cache_file modified-files)"
    get_recent_commits_uncached > "$(cache_file recent-commits)"
    get_review_summary_uncached > "$(cache_file review-summary)"
    date +'%Y-%m-%d %H:%M:%S' > "$(cache_file fast-refreshed-at)"
}

refresh_build_cache() {
    ensure_cache_dir
    local status_file
    status_file="$(cache_file build-status)"
    if cache_is_fresh "$status_file" "$BUILD_CACHE_TTL"; then
        return
    fi

    if dotnet build "$WORK_DIR/src/Argus.AppHost/Argus.AppHost.csproj" --configuration Release --verbosity quiet >/tmp/argus-dashboard-build.log 2>&1; then
        echo "OK" > "$status_file"
    else
        echo "FAILED" > "$status_file"
    fi
    date +'%Y-%m-%d %H:%M:%S' > "$(cache_file build-refreshed-at)"
}

start_async_refresh() {
    ensure_cache_dir

    local fast_pid_file build_pid_file
    fast_pid_file="$(cache_file fast-refresh.pid)"
    build_pid_file="$(cache_file build-refresh.pid)"

    if ! cache_is_fresh "$(cache_file fast-refreshed-at)" "$FAST_CACHE_TTL" && ! refresh_in_flight "$fast_pid_file"; then
        (
            echo "${BASHPID:-$$}" > "$fast_pid_file"
            trap 'rm -f "$fast_pid_file"' EXIT
            refresh_fast_cache
        ) >/dev/null 2>&1 &
    fi

    if ! cache_is_fresh "$(cache_file build-status)" "$BUILD_CACHE_TTL" && ! refresh_in_flight "$build_pid_file"; then
        (
            echo "${BASHPID:-$$}" > "$build_pid_file"
            trap 'rm -f "$build_pid_file"' EXIT
            refresh_build_cache
        ) >/dev/null 2>&1 &
    fi
}

get_modified_files() {
    read_cache_or "$(cache_file modified-files)" ""
}

get_recent_commits() {
    read_cache_or "$(cache_file recent-commits)" "(collecting commits...)"
}

get_review_summary() {
    read_cache_or "$(cache_file review-summary)" "Collecting review status..."
}

get_last_pull_time() {
    local state_json="$1"
    if [ -n "$state_json" ]; then
        echo "$state_json" | jq -r '.lastPullAt // "never"'
    elif [ -f "$STATE_FILE" ]; then
        jq -r '.lastPullAt // "never"' "$STATE_FILE"
    else
        echo "never"
    fi
}

seconds_since_pull() {
    local last_pull="$1"
    if [ "$last_pull" = "never" ] || [ -z "$last_pull" ]; then
        echo 999999
        return
    fi

    local last_pull_epoch now_epoch
    last_pull_epoch="$(date -d "${last_pull}" +%s 2>/dev/null || echo 0)"
    now_epoch="$(date +%s)"
    echo $((now_epoch - last_pull_epoch))
}

get_critical_alerts() {
    local alerts=""
    local build_status
    build_status="$(read_cache_or "$(cache_file build-status)" "CHECKING")"

    case "$build_status" in
        FAILED)
            alerts="${alerts}BUILD FAILED"
            ;;
        CHECKING)
            alerts="${alerts}BUILD CHECK PENDING"
            ;;
    esac

    local modified
    modified="$(get_modified_files)"
    if echo "$modified" | grep -qi "Security\|Auth\|Permission\|Token\|Secret\|Password"; then
        alerts="${alerts} SECURITY-SENSITIVE"
    fi

    local critical_file="$SCRIPT_DIR/.critical-alerts"
    if [ -f "$critical_file" ]; then
        local critical
        critical="$(head -3 "$critical_file" 2>/dev/null)"
        if [ -n "$critical" ]; then
            alerts="${alerts} CRITICAL: $critical"
        fi
    fi

    echo "$alerts"
}

get_tasks_requiring_attention() {
    local state_json="$1"
    local last_pull="$2"
    local attention=""

    if [ -n "$state_json" ] && [ "$state_json" != "null" ]; then
        local stale high_pending unhealthy_agents
        stale="$(echo "$state_json" | jq -r '[.tasks[] | select(.status == "in_progress" and (.assignedTo == null or .assignedTo == ""))] | length' 2>/dev/null || echo "0")"
        if [ "$stale" -gt 0 ]; then
            attention="Unassigned tasks: $stale"
        fi

        high_pending="$(echo "$state_json" | jq -r '[.tasks[] | select(.status == "pending") | select(.priority == "high" or .priority == "critical")] | length' 2>/dev/null || echo "0")"
        if [ "$high_pending" -gt 0 ]; then
            attention="${attention} High priority pending: $high_pending"
        fi

        unhealthy_agents=0
        for agent_id in agent-1 agent-2 agent-3 agent-4 agent-5; do
            local runtime
            runtime="$(agent_runtime_status "$agent_id")"
            if [ "$runtime" = "crashed" ] || [ "$runtime" = "stalled" ] || [ "$runtime" = "unresponsive" ]; then
                unhealthy_agents=$((unhealthy_agents + 1))
            fi
        done
        if [ "$unhealthy_agents" -gt 0 ]; then
            attention="${attention} Unhealthy agents: $unhealthy_agents"
        fi
    fi

    local ssp
    ssp="$(seconds_since_pull "$last_pull")"
    if [ "$ssp" -gt 300 ]; then
        attention="${attention} No pull in $(($ssp / 60))m"
    fi

    if [ -z "$attention" ]; then
        echo "None"
    else
        echo "$attention"
    fi
}

state_snapshot() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE"
    else
        echo '{"agents":[],"tasks":[]}'
    fi
}

task_counts_tsv() {
    local state_json="$1"
    echo "$state_json" | jq -r '
        [
            (.tasks | length),
            ([.tasks[] | select(.status == "completed")] | length),
            ([.tasks[] | select(.status == "pending")] | length),
            ([.tasks[] | select(.status == "in_progress")] | length),
            ([.tasks[] | select(.status == "monitoring")] | length)
        ] | @tsv
    ' 2>/dev/null || echo "0	0	0	0	0"
}

task_counts_summary() {
    local state_json="$1"
    echo "$state_json" | jq -r '
        if (.tasks | length) == 0 then
            "none"
        else
            (.tasks | group_by(.status) |
                map({status: .[0].status, count: length}) |
                sort_by(
                    if .status == "critical" then 0
                    elif .status == "in_progress" then 1
                    elif .status == "pending" then 2
                    elif .status == "monitoring" then 3
                    elif .status == "completed" then 4
                    else 5 end,
                    .status
                ) |
                map("\(.status):\(.count)") |
                join("  "))
        end
    ' 2>/dev/null || echo "none"
}

age_label() {
    local timestamp="$1"
    local age
    age="$(agent_status_age_seconds "$timestamp")"
    if [ "$age" -ge 999999 ]; then
        echo "-"
    elif [ "$age" -lt 60 ]; then
        echo "${age}s"
    elif [ "$age" -lt 3600 ]; then
        echo "$((age / 60))m"
    else
        echo "$((age / 3600))h"
    fi
}

agent_row_data() {
    local state_json="$1"
    local agent_id="$2"
    local agent_json local_state role status work_status current_task pid heartbeat heartbeat_age runtime task_desc last_error

    agent_json="$(echo "$state_json" | jq -r ".agents[] | select(.id == \"$agent_id\")" 2>/dev/null || echo "{}")"
    role="$(echo "$agent_json" | jq -r '.role // "development"')"
    status="$(echo "$agent_json" | jq -r '.status // "inactive"')"
    work_status="$(echo "$agent_json" | jq -r '.workStatus // "idle"')"
    current_task="$(echo "$agent_json" | jq -r '.currentTask // "-"')"
    local_state="$(agent_read_state "$agent_id")"
    pid="$(echo "$local_state" | jq -r '.pid // "-"')"
    heartbeat="$(echo "$local_state" | jq -r '.lastHeartbeatAt // "-"')"
    heartbeat_age="$(age_label "$heartbeat")"
    runtime="$(agent_runtime_status "$agent_id")"
    task_desc="$(echo "$local_state" | jq -r '.currentTaskDescription // "-"' | cut -c1-44)"
    last_error="$(echo "$local_state" | jq -r '.lastError // empty' | cut -c1-36)"

    if [ -n "$last_error" ]; then
        task_desc="ERR: $last_error"
    elif [ "$task_desc" = "-" ] && [ "$role" = "devops" ]; then
        task_desc="$(echo "$agent_json" | jq -r '[.responsibilities[]?] | join(", ")' | cut -c1-44)"
    fi

    printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" \
        "$agent_id" "$role" "$status" "$work_status" "$runtime" "$current_task" "$pid" "$heartbeat_age" "$task_desc"
}

draw_agents() {
    local state_json="$1"
    printf "\033[1;34m━━━ AGENTS ━━━\033[0m\n"
    printf "%-10s %-7s %-12s %-7s %-7s %-5s %s\n" "ID" "ROLE" "STATE" "TASK" "PID" "HB" "NOTE"
    echo "────────────────────────────────────────────────────────────────────────────"

    while IFS= read -r agent_id; do
        [ -n "$agent_id" ] || continue
        local row role status work_status runtime label current_task pid heartbeat_age task_desc color
        row="$(agent_row_data "$state_json" "$agent_id")"
        IFS=$'\t' read -r _agent role status work_status runtime current_task pid heartbeat_age task_desc <<< "$row"

        if [ "$status" = "active" ] && [ "$runtime" = "running" ]; then
            label="$(echo "${work_status:-running}" | tr '[:lower:]' '[:upper:]')"
            color="\033[1;33m"
        elif [ "$status" = "active" ] && { [ "$runtime" = "stalled" ] || [ "$runtime" = "crashed" ] || [ "$runtime" = "unresponsive" ]; }; then
            label="${runtime^^}"
            color="\033[1;31m"
        elif [ "$status" = "active" ]; then
            label="$(echo "${work_status:-idle}" | tr '[:lower:]' '[:upper:]')"
            [ -n "$label" ] || label="IDLE"
            label="${label:0:12}"
            color="\033[1;32m"
        else
            label="OFF"
            color="\033[1;30m"
        fi
        printf "${color}%-10s\033[0m %-7s %-12s %-7s %-7s %-5s %s\n" \
            "$agent_id" "${role:0:7}" "$label" "$current_task" "$pid" "$heartbeat_age" "$task_desc"
    done <<< "$(echo "$state_json" | jq -r '.agents | sort_by(.role // "development", .id) | .[].id' 2>/dev/null)"

    for reviewer in reviewer-1 reviewer-2; do
        local review_state last_review status
        review_state="$SCRIPT_DIR/.reviewer-state-$reviewer.json"
        if [ -f "$review_state" ]; then
            last_review="$(jq -r '.lastReviewAt // "never"' "$review_state" 2>/dev/null | cut -c1-20)"
            status="$(jq -r '.status // "idle"' "$review_state" 2>/dev/null)"
        else
            last_review="never"
            status="idle"
        fi

        if [ "$status" = "reviewing" ]; then
            printf "\033[1;33m%-10s\033[0m %-7s %-12s %-7s %-7s %-5s last=%s\n" \
                "$reviewer" "review" "REVIEWING" "-" "-" "-" "$last_review"
        else
            printf "\033[1;32m%-10s\033[0m %-7s %-12s %-7s %-7s %-5s last=%s\n" \
                "$reviewer" "review" "IDLE" "-" "-" "-" "$last_review"
        fi
    done
}

draw_tasks_in_progress() {
    local state_json="$1"
    printf "\033[1;33m━━━ TASKS IN PROGRESS ━━━\033[0m\n"
    local in_prog
    in_prog="$(echo "$state_json" | jq -c '[.tasks[] | select(.status == "in_progress")] | sort_by(.id)' 2>/dev/null || echo "[]")"
    if [ "$in_prog" != "[]" ]; then
        echo "$in_prog" | jq -r '.[0:5][] | "\(.id) \(.assignedTo // "?") \(.description[0:54])"' 2>/dev/null | while read -r tid agent desc; do
            printf "  \033[1;36m[\033[0m%s\033[1;36m]\033[0m %-10s %s\n" "$tid" "$agent" "$desc"
        done
        local total
        total="$(echo "$in_prog" | jq 'length' 2>/dev/null || echo 0)"
        if [ "$total" -gt 5 ]; then
            printf "  \033[1;30m... and %d more\033[0m\n" $((total - 5))
        fi
    else
        printf "  \033[1;30m(none)\033[0m\n"
    fi
}

draw_git_changes() {
    printf "\033[1;35m━━━ GIT CHANGES ━━━\033[0m\n"
    local modified count
    modified="$(get_modified_files)"
    if [ -n "$modified" ]; then
        count="$(echo "$modified" | wc -l)"
        printf "  \033[1;33m%d files modified:\033[0m\n" "$count"
        echo "$modified" | head -4 | while read -r f; do
            printf "    \033[1;36m•\033[0m %s\n" "$f"
        done
        if [ "$count" -gt 4 ]; then
            printf "    \033[1;30m... and %d more\033[0m\n" $((count - 4))
        fi
    else
        printf "  \033[1;32mClean - no modified files\033[0m\n"
    fi
}

draw_completed() {
    local state_json="$1"
    printf "\033[1;32mCompleted:\033[0m "
    echo "$state_json" | jq -r '
        [.tasks[] | select(.status == "completed")] |
        sort_by(.completedAt) |
        reverse |
        .[0:5][] |
        "[" + .id + "]" + (.assignedTo // "?")
    ' 2>/dev/null | tr '\n' ' '
    echo ""
}

draw() {
    start_async_refresh

    local state_json ts last_pull alerts counts total completed pending in_progress monitoring count_summary
    state_json="$(state_snapshot)"
    ts="$(date +'%Y-%m-%d %H:%M:%S')"
    last_pull="$(get_last_pull_time "$state_json")"
    alerts="$(get_critical_alerts)"
    counts="$(task_counts_tsv "$state_json")"
    IFS=$'\t' read -r total completed pending in_progress monitoring <<< "$counts"
    count_summary="$(task_counts_summary "$state_json")"

    if [ "$INTERACTIVE" -eq 1 ]; then
        printf "\033[2J\033[H"
        printf "\033[1;36mARGUS OPS DASHBOARD\033[0m  %s  pull=\033[1;35m%s\033[0m\n" "$ts" "$last_pull"
        printf "  tasks(%d): \033[1;36m%s\033[0m  latest review: \033[1;35m%s\033[0m\n" "$total" "$count_summary" "$(get_review_summary)"
        if [ -n "$alerts" ]; then
            printf "  \033[1;41mALERTS: %s\033[0m\n" "$alerts"
        fi
    else
        echo "═══════════════════════════════════════════════════════════════════"
        echo "                       ARGUS OPS DASHBOARD"
        echo "═══════════════════════════════════════════════════════════════════"
        echo "  $ts  pull=$last_pull"
        echo "  tasks($total): $count_summary"
        echo "  latest review: $(get_review_summary)"
        if [ -n "$alerts" ]; then
            echo "  [WARNING] ALERTS: $alerts"
        fi
    fi

    echo ""
    draw_agents "$state_json"
    echo ""
    draw_tasks_in_progress "$state_json"
    echo ""
    printf "\033[1;31m⚠ ATTENTION REQUIRED:\033[0m %s\n" "$(get_tasks_requiring_attention "$state_json" "$last_pull")"
    echo ""
    draw_git_changes
    echo ""
    printf "\033[1;33mRecent commits:\033[0m\n"
    get_recent_commits | head -3 | while read -r line; do
        printf "  %s\n" "$line" | cut -c1-75
    done
    echo ""
    draw_completed "$state_json"
}

ensure_cache_dir
start_async_refresh

if [ "$INTERACTIVE" -eq 1 ]; then
    while true; do
        draw
        printf "\n  \033[1;34mRefreshing every %ds... Ctrl+C to exit\033[0m" "$REFRESH"
        sleep "$REFRESH"
        printf "\033[2A\033[J"
    done
else
    draw
fi
