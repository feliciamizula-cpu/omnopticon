#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/agent-state-lib.sh"
STATE_FILE="$SCRIPT_DIR/.agent-tasks.json"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

clear_screen() { printf "\033[2J\033[H"; }
move_cursor() { printf "\033[%d;%dH" "$1" "$2"; }

header() {
    echo -e "${BLUE}╔════════════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BLUE}║           AGENT TEAM CONTROL CENTER                          ║${NC}"
    echo -e "${BLUE}╚════════════════════════════════════════════════════════════════╝${NC}"
    echo ""
}

print_status() {
    local agents_json=$(cat "$STATE_FILE" 2>/dev/null || echo "{}")
    
    echo -e "${CYAN}═══ AGENT STATUS ═══${NC}"
    echo "--------------------------------------------------------------------------------"
    echo -e "AGENT         STATUS       TASK       WORK STATUS"
    echo "--------------------------------------------------------------------------------"
    
    for agent in agent-1 agent-2 agent-3 agent-4 agent-5 devops-1 devops-2; do
        local state_json=$(agent_read_state "$agent")
        local status=$(echo "$state_json" | jq -r '.status // "unknown"')
        local current_task=$(echo "$state_json" | jq -r '.currentTaskId // "-"')
        local work_status=$(echo "$state_json" | jq -r '.workStatus // "-"')
        local pid=$(echo "$state_json" | jq -r '.pid // empty')
        
        if [ "$status" = "active" ] || [ "$status" = "working" ]; then
            if [ -n "$pid" ] && [ "$pid" != "null" ] && ! kill -0 "$pid" 2>/dev/null; then
                status="crashed"
                work_status="crashed"
            fi
        fi
        
        printf "%-13s %-12s %-10s %s\n" "$agent" "$status" "$current_task" "$work_status"
    done
    
    echo "--------------------------------------------------------------------------------"
    echo ""
}

print_tasks() {
    local status_filter=$1
    local label=$2
    
    echo -e "${CYAN}═══ TASKS: $label ═══${NC}"
    
    local tasks=$(cat "$STATE_FILE" 2>/dev/null | jq -c --arg status "$status_filter" \
        '[.tasks[] | select(.status == $status)] | sort_by(.priority, .id)')
    
    if [ "$tasks" = "[]" ] || [ -z "$tasks" ]; then
        echo "No $label tasks"
        return
    fi
    
    echo "$tasks" | jq -r '.[] | "\(.id) | \(.priority) | \(.description[0:60])..."'
    echo ""
}

show_dashboard() {
    clear_screen
    header
    print_status
    
    echo -e "${CYAN}═══ QUICK STATS ═══${NC}"
    local total=$(cat "$STATE_FILE" 2>/dev/null | jq '.tasks | length')
    local completed=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "completed")] | length')
    local in_progress=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "in_progress")] | length')
    local pending=$(cat "$STATE_FILE" 2>/dev/null | jq '[.tasks[] | select(.status == "pending")] | length')
    
    echo "Total: $total | Completed: $completed | In Progress: $in_progress | Pending: $pending"
    echo ""
}

build_check() {
    echo -e "${YELLOW}═══ BUILD CHECK ═══${NC}"
    cd "$WORK_DIR"
    
    local build_output=$(dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release 2>&1)
    local exit_code=$?
    
    if [ $exit_code -eq 0 ]; then
        echo -e "${GREEN}✓ Build successful${NC}"
        
        local warnings=$(echo "$build_output" | grep -c "warning" || echo "0")
        if [ "$warnings" -gt 0 ]; then
            echo -e "${YELLOW}⚠ Found $warnings warnings${NC}"
            echo "$build_output" | grep -i "warning" | head -10
        else
            echo "No warnings"
        fi
    else
        echo -e "${RED}✗ Build failed${NC}"
        echo "$build_output" | tail -30
    fi
    
    return $exit_code
}

fix_build_errors() {
    echo -e "${YELLOW}═══ FIX BUILD ERRORS ═══${NC}"
    
    cd "$WORK_DIR"
    local build_output=$(dotnet build src/Argus.AppHost/Argus.AppHost.csproj --configuration Release 2>&1)
    local exit_code=$?
    
    if [ $exit_code -eq 0 ]; then
        echo -e "${GREEN}✓ No build errors to fix${NC}"
        return 0
    fi
    
    local errors=$(echo "$build_output" | grep -E "^.* error CS\d+:" | head -20)
    echo "Found errors:"
    echo "$errors"
    
    local error_count=$(echo "$errors" | wc -l)
    echo ""
    echo "Creating task to fix $error_count build errors..."
    
    local task_id="B$(date +%m%d%H%M)"
    local error_summary=$(echo "$errors" | head -5 | tr '\n' ' ')
    
    jq ".tasks += [{
        \"id\": \"$task_id\",
        \"description\": \"Fix build errors: $error_summary\",
        \"priority\": \"high\",
        \"assignedTo\": null,
        \"status\": \"pending\",
        \"createdAt\": \"$(date -u +"%Y-%m-%dT%H:%M:%S.%NZ")\"
    }]" "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
    
    echo -e "${GREEN}✓ Created task $task_id${NC}"
}

assign_task_to_idle_agent() {
    echo -e "${CYAN}═══ ASSIGN TASK TO IDLE AGENT ═══${NC}"
    
    local idle_agents=()
    for agent in agent-1 agent-2 agent-3 agent-4 agent-5; do
        local state_json=$(agent_read_state "$agent")
        local status=$(echo "$state_json" | jq -r '.status // "unknown"')
        local work_status=$(echo "$state_json" | jq -r '.workStatus // "idle"')
        local pid=$(echo "$state_json" | jq -r '.pid // empty')
        
        if [ "$status" = "idle" ] || [ "$work_status" = "idle" ]; then
            if [ -z "$pid" ] || [ "$pid" = "null" ] || ! kill -0 "$pid" 2>/dev/null; then
                idle_agents+=("$agent")
            fi
        fi
    done
    
    if [ ${#idle_agents[@]} -eq 0 ]; then
        echo "No idle agents available"
        return
    fi
    
    echo "Idle agents: ${idle_agents[*]}"
    echo ""
    
    echo "Select agent:"
    select agent in "${idle_agents[@]}" "Cancel"; do
        if [ "$agent" = "Cancel" ]; then
            return
        fi
        
        echo "Selected: $agent"
        echo ""
        
        local pending_tasks=$(cat "$STATE_FILE" | jq -c '[.tasks[] | select(.status == "pending")] | sort_by(if .priority == "critical" then 0 elif .priority == "high" then 1 elif .priority == "medium" then 2 else 3 end, .id)')
        
        if [ "$pending_tasks" = "[]" ] || [ -z "$pending_tasks" ]; then
            echo "No pending tasks available"
            return
        fi
        
        echo "Select task:"
        local task_array=($(echo "$pending_tasks" | jq -r '.[] | "\(.id) \(.priority) \(.description[0:50])..."'))
        
        local task_options=()
        for task_info in "${task_array[@]}"; do
            task_options+=("$task_info")
        done
        
        select task_choice in "${task_options[@]}" "Cancel"; do
            if [ "$task_choice" = "Cancel" ]; then
                return
            fi
            
            local task_id=$(echo "$task_choice" | awk '{print $1}')
            
            AGENT_ID="$agent" "$SCRIPT_DIR/agent-coord.sh" take -t "$task_id" -a "$agent"
            echo -e "${GREEN}✓ Assigned task $task_id to $agent${NC}"
            
            local state_json=$(agent_read_state "$agent")
            local description=$(echo "$state_json" | jq -r '.currentTaskDescription // empty')
            
            "$SCRIPT_DIR/auto-run-agents.sh" &
            sleep 2
            
            return
        done
    done
}

force_restart_agent() {
    echo -e "${RED}═══ FORCE RESTART AGENT ═══${NC}"
    
    local agents=("agent-1" "agent-2" "agent-3" "agent-4" "agent-5" "devops-1" "devops-2")
    
    echo "Select agent to restart:"
    select agent in "${agents[@]}" "Cancel"; do
        if [ "$agent" = "Cancel" ]; then
            return
        fi
        
        echo "Restarting $agent..."
        
        local state_json=$(agent_read_state "$agent")
        local current_task=$(echo "$state_json" | jq -r '.currentTaskId // empty')
        local current_status=$(echo "$state_json" | jq -r '.status // "idle"')
        
        if [ "$current_status" = "working" ] && [ -n "$current_task" ] && [ "$current_task" != "null" ]; then
            AGENT_ID="$agent" "$SCRIPT_DIR/agent-coord.sh" release -t "$current_task" -a "$agent" 2>/dev/null || true
        fi
        
        local now=$(agent_now_utc)
        local idle_state=$(echo "$state_json" | jq \
            --arg now "$now" \
            '.currentTaskId = null | .currentTaskDescription = null | .context = null | .status = "idle" | .pid = null | .startedAt = null | .lastHeartbeatAt = $now | .lastRunAt = $now | .updatedAt = $now | .lastError = null')
        agent_write_state "$agent" "$idle_state"
        
        jq --arg agent "$agent_id" \
           --arg now "$now" \
           '.agents |= map(if .id == $agent then .status = "active" | .workStatus = "idle" | .currentTask = null | .currentTaskDescription = null | .pid = null | .lastHeartbeatAt = $now | .lastUpdated = $now | .lastError = null else . end)' \
           "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
        
        echo -e "${GREEN}✓ Reset $agent to idle${NC}"
        return
    done
}

fix_failed_agents() {
    echo -e "${RED}═══ FIX FAILED/UNRESPONSIVE AGENTS ═══${NC}"
    
    local fixed_count=0
    
    for agent in agent-1 agent-2 agent-3 agent-4 agent-5 devops-1 devops-2; do
        local state_json=$(agent_read_state "$agent")
        local status=$(echo "$state_json" | jq -r '.status // "unknown"')
        local pid=$(echo "$state_json" | jq -r '.pid // empty')
        local current_task=$(echo "$state_json" | jq -r '.currentTaskId // empty')
        
        local needs_reset=false
        
        if [ "$status" = "crashed" ] || [ "$status" = "stalled" ]; then
            echo "  $agent: $status → resetting"
            needs_reset=true
        elif [ -n "$pid" ] && [ "$pid" != "null" ] && ! kill -0 "$pid" 2>/dev/null; then
            echo "  $agent: pid $pid dead → resetting"
            needs_reset=true
        fi
        
        if [ "$needs_reset" = true ]; then
            if [ -n "$current_task" ] && [ "$current_task" != "null" ]; then
                AGENT_ID="$agent" "$SCRIPT_DIR/agent-coord.sh" release -t "$current_task" -a "$agent" 2>/dev/null || true
            fi
            
            local now=$(agent_now_utc)
            local idle_state=$(echo "$state_json" | jq \
                --arg now "$now" \
                '.currentTaskId = null | .currentTaskDescription = null | .context = null | .status = "idle" | .pid = null | .startedAt = null | .lastHeartbeatAt = $now | .lastRunAt = $now | .updatedAt = $now | .lastError = null')
            agent_write_state "$agent" "$idle_state"
            
            fixed_count=$((fixed_count + 1))
        fi
    done
    
    echo ""
    echo -e "${GREEN}✓ Fixed $fixed_count agents${NC}"
}

check_devops_errors() {
    echo -e "${YELLOW}═══ DEVOPS: CHECK ERROR LOGS ═══${NC}"
    
    local log_files=(
        "/tmp/auto-run-agents.log"
        "/tmp/agent-supervisor.log"
        "/tmp/watchdog.log"
    )
    
    local error_patterns=(
        "error"
        "ERROR"
        "failed"
        "FAILED"
        "exception"
        "Exception"
        "crashed"
        "CRASHED"
    )
    
    local found_errors=false
    
    for log_file in "${log_files[@]}"; do
        if [ -f "$log_file" ]; then
            for pattern in "${error_patterns[@]}"; do
                local matches=$(grep -c "$pattern" "$log_file" 2>/dev/null || echo "0")
                if [ "$matches" -gt 0 ]; then
                    echo "Found $matches '$pattern' in $log_file"
                    found_errors=true
                fi
            done
        fi
    done
    
    echo ""
    echo "Checking application logs..."
    
    local app_log_dirs=(
        "$WORK_DIR/logs"
        "/var/log/argus"
        "/tmp/argus-logs"
    )
    
    for log_dir in "${app_log_dirs[@]}"; do
        if [ -d "$log_dir" ]; then
            local app_errors=$(find "$log_dir" -name "*.log" -newer /tmp/auto-run-agents.log 2>/dev/null | xargs grep -c "error\|exception" 2>/dev/null || echo "0")
            if [ "$app_errors" -gt 0 ]; then
                echo "Found $app_errors error entries in $log_dir"
                found_errors=true
            fi
        fi
    done
    
    if [ "$found_errors" = false ]; then
        echo -e "${GREEN}✓ No critical errors found${NC}"
        return
    fi
    
    echo ""
    echo "Creating tasks from recent errors..."
    
    local recent_errors=$(tail -100 /tmp/auto-run-agents.log 2>/dev/null | grep -E "error|ERROR|failed|FAILED|exception" | tail -5)
    
    if [ -n "$recent_errors" ]; then
        local task_id="E$(date +%m%d%H%M)"
        local error_summary=$(echo "$recent_errors" | tail -1 | cut -c1-100)
        
        jq ".tasks += [{
            \"id\": \"$task_id\",
            \"description\": \"DevOps: Fix error - $error_summary\",
            \"priority\": \"high\",
            \"assignedTo\": null,
            \"status\": \"pending\",
            \"createdAt\": \"$(date -u +"%Y-%m-%dT%H:%M:%S.%NZ")\"
        }]" "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
        
        echo -e "${GREEN}✓ Created task $task_id${NC}"
    fi
}

create_devops_task() {
    echo -e "${YELLOW}═══ CREATE DEVOPS TASK ═══${NC}"
    
    echo "Task description:"
    read -r description
    
    echo "Priority (critical/high/medium/low):"
    read -r priority
    
    if [ -z "$description" ]; then
        echo "Description required"
        return
    fi
    
    priority=${priority:-medium}
    
    local task_id="D$(date +%m%d%H%M)"
    
    jq ".tasks += [{
        \"id\": \"$task_id\",
        \"description\": \"DevOps: $description\",
        \"priority\": \"$priority\",
        \"assignedTo\": null,
        \"status\": \"pending\",
        \"createdAt\": \"$(date -u +"%Y-%m-%dT%H:%M:%S.%NZ")\"
    }]" "$STATE_FILE" > "${STATE_FILE}.tmp" && mv "${STATE_FILE}.tmp" "$STATE_FILE"
    
    echo -e "${GREEN}✓ Created task $task_id${NC}"
}

reconcile_task_board() {
    echo -e "${CYAN}═══ RECONCILE TASK BOARD ═══${NC}"
    
    cd "$SCRIPT_DIR"
    ./agent-coord.sh reconcile
    
    echo -e "${GREEN}✓ Task board reconciled${NC}"
}

restart_supervisor() {
    echo -e "${YELLOW}═══ RESTART SUPERVISOR ═══${NC}"
    
    local supervisor_pid=$(cat "$SCRIPT_DIR/.agent-supervisor.pid" 2>/dev/null || echo "")
    
    if [ -n "$supervisor_pid" ] && kill -0 "$supervisor_pid" 2>/dev/null; then
        echo "Stopping existing supervisor (PID: $supervisor_pid)..."
        kill "$supervisor_pid" 2>/dev/null || true
        sleep 2
    fi
    
    rm -f "$SCRIPT_DIR/.agent-supervisor.pid"
    
    echo "Starting new supervisor..."
    nohup "$SCRIPT_DIR/agent-watchdog.sh" >/tmp/watchdog.log 2>&1 &
    sleep 3
    
    local new_pid=$(cat "$SCRIPT_DIR/.agent-supervisor.pid" 2>/dev/null || echo "unknown")
    echo -e "${GREEN}✓ Supervisor started (PID: $new_pid)${NC}"
}

spawn_idle_agents() {
    echo -e "${CYAN}═══ SPAWN IDLE AGENTS ═══${NC}"
    
    local count=0
    for agent in agent-1 agent-2 agent-3 agent-4 agent-5; do
        local state_json=$(agent_read_state "$agent")
        local status=$(echo "$state_json" | jq -r '.status // "unknown"')
        local work_status=$(echo "$state_json" | jq -r '.workStatus // "idle"')
        local pid=$(echo "$state_json" | jq -r '.pid // empty')
        
        if [ "$status" = "idle" ] || [ "$work_status" = "idle" ]; then
            if [ -z "$pid" ] || [ "$pid" = "null" ] || ! kill -0 "$pid" 2>/dev/null; then
                local task_id=$(jq -r --arg agent "$agent" \
                    '[.tasks[] | select(.status == "pending")] | sort_by(if .priority == "critical" then 0 elif .priority == "high" then 1 elif .priority == "medium" then 2 else 3 end, .id) | .[0].id // empty' \
                    "$STATE_FILE")
                
                if [ -n "$task_id" ]; then
                    AGENT_ID="$agent" "$SCRIPT_DIR/agent-coord.sh" take -t "$task_id" -a "$agent"
                    echo "  $agent → task $task_id"
                else
                    echo "  $agent: no pending tasks"
                fi
                count=$((count + 1))
            fi
        fi
    done
    
    "$SCRIPT_DIR/auto-run-agents.sh" &
    sleep 2
    
    echo -e "${GREEN}✓ Dispatched $count agents${NC}"
}

show_agent_context() {
    echo -e "${CYAN}═══ AGENT CONTEXT ═══${NC}"
    
    local agents=("agent-1" "agent-2" "agent-3" "agent-4" "agent-5" "devops-1" "devops-2")
    
    echo "Select agent:"
    select agent in "${agents[@]}" "Cancel"; do
        if [ "$agent" = "Cancel" ]; then
            return
        fi
        
        echo ""
        local state_json=$(agent_read_state "$agent")
        echo "$state_json" | jq '.'
        return
    done
}

kill_all_agents() {
    echo -e "${RED}═══ KILL ALL AGENTS ═══${NC}"
    echo -e "${RED}WARNING: This will terminate all running agent processes${NC}"
    echo "Type 'yes' to confirm:"
    read -r confirm
    
    if [ "$confirm" != "yes" ]; then
        echo "Cancelled"
        return
    fi
    
    for agent in agent-1 agent-2 agent-3 agent-4 agent-5 devops-1 devops-2; do
        local state_json=$(agent_read_state "$agent")
        local pid=$(echo "$state_json" | jq -r '.pid // empty')
        
        if [ -n "$pid" ] && [ "$pid" != "null" ]; then
            kill "$pid" 2>/dev/null || true
            echo "  Killed $agent (PID: $pid)"
        fi
        
        local now=$(agent_now_utc)
        local idle_state=$(echo "$state_json" | jq --arg now "$now" \
            '.currentTaskId = null | .currentTaskDescription = null | .context = null | .status = "idle" | .pid = null | .startedAt = null | .lastHeartbeatAt = $now | .lastRunAt = $now | .updatedAt = $now | .lastError = null')
        agent_write_state "$agent" "$idle_state"
    done
    
    echo -e "${GREEN}✓ All agents killed and reset${NC}"
}

export_state() {
    echo -e "${CYAN}═══ EXPORT STATE ═══${NC}"
    echo "Enter output file path (default: /tmp/agent-state-export.json):"
    read -r output_path
    output_path=${output_path:-/tmp/agent-state-export.json}
    
    cat "$STATE_FILE" > "$output_path"
    echo -e "${GREEN}✓ State exported to $output_path${NC}"
}

import_state() {
    echo -e "${YELLOW}═══ IMPORT STATE ═══${NC}"
    echo "Enter state file path:"
    read -r input_path
    
    if [ ! -f "$input_path" ]; then
        echo "File not found: $input_path"
        return
    fi
    
    cp "$STATE_FILE" "${STATE_FILE}.backup.$(date +%s)"
    cat "$input_path" > "$STATE_FILE"
    echo -e "${GREEN}✓ State imported (backup created)${NC}"
}

show_menu() {
    echo ""
    echo -e "${CYAN}╔════════════════════════════════════════════════════════════════╗${NC}"
    echo -e "${CYAN}║                        MAIN MENU                             ║${NC}"
    echo -e "${CYAN}╚════════════════════════════════════════════════════════════════╝${NC}"
    echo ""
    echo "  ${GREEN}[1]${NC} Dashboard view"
    echo "  ${GREEN}[2]${NC} Assign task to idle agent"
    echo "  ${GREEN}[3]${NC} Fix failed/unresponsive agents"
    echo "  ${GREEN}[4]${NC} Force restart specific agent"
    echo "  ${GREEN}[5]${NC} Check devops error logs → create tasks"
    echo "  ${GREEN}[6]${NC} Create devops task"
    echo "  ${GREEN}[7]${NC} Fix build errors → create task if needed"
    echo "  ${GREEN}[8]${NC} Reconcile task board"
    echo "  ${GREEN}[9]${NC} Restart supervisor"
    echo "  ${GREEN}[A]${NC} Spawn all idle agents"
    echo "  ${GREEN}[C]${NC} Show agent context"
    echo "  ${GREEN}[K]${NC} Kill all agents (reset)"
    echo "  ${GREEN}[E]${NC} Export state"
    echo "  ${GREEN}[I]${NC} Import state"
    echo ""
    echo "  ${RED}[Q]${NC} Quit"
    echo ""
}

while true; do
    show_dashboard
    show_menu
    
    echo -n "Select option: "
    read -r choice
    
    case "$choice" in
        1) show_dashboard ;;
        2) assign_task_to_idle_agent; echo ""; echo "Press enter to continue..."; read -r ;;
        3) fix_failed_agents; echo ""; echo "Press enter to continue..."; read -r ;;
        4) force_restart_agent; echo ""; echo "Press enter to continue..."; read -r ;;
        5) check_devops_errors; echo ""; echo "Press enter to continue..."; read -r ;;
        6) create_devops_task; echo ""; echo "Press enter to continue..."; read -r ;;
        7) fix_build_errors; echo ""; echo "Press enter to continue..."; read -r ;;
        8) reconcile_task_board; echo ""; echo "Press enter to continue..."; read -r ;;
        9) restart_supervisor; echo ""; echo "Press enter to continue..."; read -r ;;
        A|a) spawn_idle_agents; echo ""; echo "Press enter to continue..."; read -r ;;
        C|c) show_agent_context; echo ""; echo "Press enter to continue..."; read -r ;;
        K|k) kill_all_agents; echo ""; echo "Press enter to continue..."; read -r ;;
        E|e) export_state; echo ""; echo "Press enter to continue..."; read -r ;;
        I|i) import_state; echo ""; echo "Press enter to continue..."; read -r ;;
        Q|q) echo "Goodbye!"; exit 0 ;;
        *) echo "Invalid option"; sleep 1 ;;
    esac
done