#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORK_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OPENCODE_BIN="${OPENCODE_BIN:-opencode}"
INTERVAL="${1:-15}"
REVIEW_LOG_FILE="$SCRIPT_DIR/reviews/REVIEW_LOG.md"
REVIEW_MARKER_FILE="$SCRIPT_DIR/.reviewed-commits"
LOG_FILE="${REVIEW_LOG_FILE_LOG:-/tmp/review-agents.log}"
RECENT_LIMIT="${REVIEW_RECENT_LIMIT:-20}"
REVIEWERS=("reviewer-1" "reviewer-2")

log() {
    echo "[$(date +'%Y-%m-%dT%H:%M:%S')] $*" >> "$LOG_FILE"
}

now_utc() {
    date -u +"%Y-%m-%dT%H:%M:%S.%3NZ"
}

ensure_review_docs() {
    mkdir -p "$SCRIPT_DIR/reviews"
    if [ ! -f "$REVIEW_LOG_FILE" ]; then
        cat > "$REVIEW_LOG_FILE" <<'EOF'
# Review Log

This file is maintained by the reviewer agent loop. Each batch records the commits reviewed, reviewer documents, and follow-up status.

EOF
    fi
}

reviewer_state_file() {
    local reviewer="$1"
    echo "$SCRIPT_DIR/.reviewer-state-$reviewer.json"
}

read_reviewer_state() {
    local reviewer="$1"
    local state_file
    state_file="$(reviewer_state_file "$reviewer")"
    if [ -s "$state_file" ] && jq -e . "$state_file" >/dev/null 2>&1; then
        cat "$state_file"
    else
        jq -n \
            --arg reviewer "$reviewer" \
            --arg now "$(now_utc)" \
            '{reviewerId: $reviewer, status: "idle", pid: null, currentBatch: null, currentReviewFile: null, lastReviewAt: null, updatedAt: $now, lastError: null}'
    fi
}

write_reviewer_state() {
    local reviewer="$1"
    local state_json="$2"
    local state_file tmp
    state_file="$(reviewer_state_file "$reviewer")"
    tmp="$(mktemp "${state_file}.tmp.XXXXXX")"
    printf '%s\n' "$state_json" > "$tmp"
    mv "$tmp" "$state_file"
}

pid_running() {
    local pid="$1"
    [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" >/dev/null 2>&1
}

reviewer_runtime_status() {
    local reviewer="$1"
    local state pid status
    state="$(read_reviewer_state "$reviewer")"
    pid="$(echo "$state" | jq -r '.pid // empty')"
    status="$(echo "$state" | jq -r '.status // "idle"')"
    if [ "$status" = "reviewing" ] && pid_running "$pid"; then
        echo "reviewing"
    elif [ "$status" = "reviewing" ]; then
        echo "crashed"
    else
        echo "$status"
    fi
}

sync_reviewer_runtime() {
    local reviewer="$1"
    local state pid status updated
    state="$(read_reviewer_state "$reviewer")"
    pid="$(echo "$state" | jq -r '.pid // empty')"
    status="$(echo "$state" | jq -r '.status // "idle"')"
    if [ "$status" = "reviewing" ] && ! pid_running "$pid"; then
        updated="$(echo "$state" | jq \
            --arg now "$(now_utc)" \
            '.status = "crashed" | .pid = null | .lastError = "reviewer process exited unexpectedly" | .updatedAt = $now')"
        write_reviewer_state "$reviewer" "$updated"
        log "$reviewer marked crashed"
    fi
}

head_sha() {
    git -C "$WORK_DIR" rev-parse HEAD 2>/dev/null
}

commit_batch() {
    local marker head
    head="$(head_sha)"
    if [ -z "$head" ]; then
        return
    fi

    if [ -s "$REVIEW_MARKER_FILE" ]; then
        marker="$(cat "$REVIEW_MARKER_FILE")"
        if git -C "$WORK_DIR" merge-base --is-ancestor "$marker" HEAD >/dev/null 2>&1; then
            git -C "$WORK_DIR" log --reverse --format='%H %s' "$marker..HEAD" 2>/dev/null
            return
        fi
    fi

    git -C "$WORK_DIR" log --reverse --format='%H %s' -"${RECENT_LIMIT}" 2>/dev/null
}

batch_id_for_commits() {
    local commits="$1"
    printf '%s' "$commits" | sha1sum | awk '{print substr($1, 1, 12)}'
}

write_review_seed_doc() {
    local reviewer="$1"
    local batch_id="$2"
    local commits="$3"
    local review_file="$SCRIPT_DIR/reviews/$(date +'%Y%m%d-%H%M%S')-$batch_id-$reviewer.md"

    cat > "$review_file" <<EOF
# Code Review

Reviewer: $reviewer
Batch: $batch_id
Started: $(now_utc)
Status: in_progress

## Commits

$commits

## Findings

_Reviewer output pending._

## Follow-Up

_Reviewer output pending._
EOF

    echo "$review_file"
}

append_review_log_start() {
    local batch_id="$1"
    local commits="$2"
    {
        echo "## $(now_utc)"
        echo ""
        echo "Batch: $batch_id"
        echo "Status: in_progress"
        echo ""
        echo "Commits:"
        echo "$commits" | sed 's/^/- /'
        echo ""
    } >> "$REVIEW_LOG_FILE"
}

append_review_log_finish() {
    local batch_id="$1"
    local reviewer="$2"
    local review_file="$3"
    {
        echo "Reviewer: $reviewer"
        echo "Document: $review_file"
        echo "Completed: $(now_utc)"
        echo ""
    } >> "$REVIEW_LOG_FILE"
}

mark_batch_reviewed_if_complete() {
    local batch_id="$1"
    local head
    head="$(head_sha)"

    for reviewer in "${REVIEWERS[@]}"; do
        local state status current_batch
        state="$(read_reviewer_state "$reviewer")"
        status="$(echo "$state" | jq -r '.status // "idle"')"
        current_batch="$(echo "$state" | jq -r '.currentBatch // empty')"
        if [ "$current_batch" != "$batch_id" ] || [ "$status" != "idle" ]; then
            return
        fi
    done

    if [ -n "$head" ]; then
        echo "$head" > "$REVIEW_MARKER_FILE"
        log "Marked batch $batch_id reviewed through $head"
    fi
}

spawn_reviewer() {
    local reviewer="$1"
    local batch_id="$2"
    local commits="$3"
    local review_file
    review_file="$(write_review_seed_doc "$reviewer" "$batch_id" "$commits")"

    (
        local runtime_pid state updated exit_code prompt
        runtime_pid="${BASHPID:-$$}"
        state="$(read_reviewer_state "$reviewer")"
        updated="$(echo "$state" | jq \
            --arg reviewer "$reviewer" \
            --arg batch "$batch_id" \
            --arg file "$review_file" \
            --arg now "$(now_utc)" \
            --argjson pid "$runtime_pid" \
            '.reviewerId = $reviewer | .status = "reviewing" | .pid = $pid | .currentBatch = $batch | .currentReviewFile = $file | .updatedAt = $now | .lastError = null')"
        write_reviewer_state "$reviewer" "$updated"
        log "$reviewer started batch $batch_id"

        prompt="You are $reviewer, a dedicated code reviewer.

Review every commit in this batch:
$commits

Write your review into this file:
$review_file

Rules:
1. Do not edit source code.
2. Review behavior, regressions, missing tests, build risks, operational risks, and security issues.
3. Include exact file paths and line numbers when you can.
4. Keep the document durable: replace the pending sections with Findings, Open Questions, and Follow-Up.
5. If there are no findings, say that explicitly and mention residual risk.
6. At the end, set Status: complete in the document.

Use git show, git diff, and local file reads as needed. Do not commit anything."

        set +e
        printf '%s\n' "$prompt" | "$OPENCODE_BIN" run --dir "$WORK_DIR" 2>&1 | while IFS= read -r line; do
            log "[$reviewer] $line"
        done
        exit_code=${PIPESTATUS[1]}
        set -e

        state="$(read_reviewer_state "$reviewer")"
        if [ "$exit_code" -eq 0 ]; then
            updated="$(echo "$state" | jq \
                --arg now "$(now_utc)" \
                '.status = "idle" | .pid = null | .lastReviewAt = $now | .updatedAt = $now | .lastError = null')"
            append_review_log_finish "$batch_id" "$reviewer" "$review_file"
            log "$reviewer completed batch $batch_id"
        else
            updated="$(echo "$state" | jq \
                --arg now "$(now_utc)" \
                --arg error "reviewer exited with code '"$exit_code"'" \
                '.status = "crashed" | .pid = null | .updatedAt = $now | .lastError = $error')"
            log "$reviewer failed batch $batch_id with exit $exit_code"
        fi
        write_reviewer_state "$reviewer" "$updated"
        mark_batch_reviewed_if_complete "$batch_id"
    ) &

    log "$reviewer background PID: $!"
}

run_once() {
    ensure_review_docs

    for reviewer in "${REVIEWERS[@]}"; do
        sync_reviewer_runtime "$reviewer"
    done

    local commits batch_id runtime
    commits="$(commit_batch || true)"
    if [ -z "$commits" ]; then
        log "No commits awaiting review"
        return
    fi

    batch_id="$(batch_id_for_commits "$commits")"
    if ! grep -q "Batch: $batch_id" "$REVIEW_LOG_FILE" 2>/dev/null; then
        append_review_log_start "$batch_id" "$commits"
    fi

    for reviewer in "${REVIEWERS[@]}"; do
        local state current_batch status
        state="$(read_reviewer_state "$reviewer")"
        current_batch="$(echo "$state" | jq -r '.currentBatch // empty')"
        status="$(echo "$state" | jq -r '.status // "idle"')"
        if [ "$current_batch" = "$batch_id" ] && [ "$status" = "idle" ]; then
            log "$reviewer already completed batch $batch_id"
            continue
        fi

        runtime="$(reviewer_runtime_status "$reviewer")"
        if [ "$runtime" = "idle" ] || [ "$runtime" = "crashed" ]; then
            spawn_reviewer "$reviewer" "$batch_id" "$commits"
        else
            log "$reviewer is $runtime; skipping batch $batch_id this cycle"
        fi
    done

    mark_batch_reviewed_if_complete "$batch_id"
}

log "========================================"
log "Review agents starting (interval: ${INTERVAL}s)"
log "Reviewers: ${REVIEWERS[*]}"
log "Review log: $REVIEW_LOG_FILE"

while true; do
    run_once
    sleep "$INTERVAL"
done
