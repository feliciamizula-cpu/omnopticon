#!/usr/bin/env bash
set -euo pipefail

ARGUS_API_BASE="${ARGUS_API_BASE:-http://localhost:${ARGUS_GATEWAY_PORT:-8081}}"
DEMO_PROGRAM_NAME="${ARGUS_DEMO_PROGRAM_NAME:-Acme Demo Recon}"
DEMO_DOMAIN="${ARGUS_DEMO_DOMAIN:-acme-demo.test}"

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required tool: $1" >&2
    exit 1
  fi
}

api_url() {
  printf '%s%s' "${ARGUS_API_BASE%/}" "$1"
}

get_json() {
  curl -fsS "$(api_url "$1")"
}

post_json() {
  local path="$1"
  local payload="$2"

  curl -fsS \
    -H "content-type: application/json" \
    -d "$payload" \
    "$(api_url "$path")"
}

wait_for_gateway() {
  echo "Waiting for Argus API gateway at ${ARGUS_API_BASE}..."

  for _ in $(seq 1 60); do
    if curl -fsS "$(api_url /)" >/dev/null 2>&1; then
      return 0
    fi

    sleep 2
  done

  echo "Argus API gateway did not become ready at ${ARGUS_API_BASE}." >&2
  exit 1
}

create_program() {
  local existing_id
  existing_id="$(get_json /programs | jq -r --arg name "$DEMO_PROGRAM_NAME" '.[] | select(.name == $name) | .programId' | head -n 1)"

  if [[ -n "$existing_id" ]]; then
    echo "$existing_id"
    return
  fi

  post_json /programs "$(jq -cn \
    --arg name "$DEMO_PROGRAM_NAME" \
    --arg source "demo" \
    --arg externalUrl "https://hackerone.com/acme-demo" \
    '{name:$name,source:$source,externalUrl:$externalUrl}')" \
    | jq -r '.programId'
}

ensure_scope() {
  local program_id="$1"
  local scope_type="$2"
  local pattern="$3"
  local action="$4"
  local notes="$5"
  local existing_id

  existing_id="$(get_json /scopes | jq -r \
    --arg programId "$program_id" \
    --arg pattern "$pattern" \
    --arg action "$action" \
    '.[] | select(.programId == $programId and .pattern == $pattern and .action == $action) | .scopeId' \
    | head -n 1)"

  if [[ -n "$existing_id" ]]; then
    echo "$existing_id"
    return
  fi

  post_json "/programs/${program_id}/scopes" "$(jq -cn \
    --arg programId "$program_id" \
    --arg scopeType "$scope_type" \
    --arg pattern "$pattern" \
    --arg action "$action" \
    --arg notes "$notes" \
    '{programId:$programId,scopeType:$scopeType,pattern:$pattern,action:$action,notes:$notes}')" \
    | jq -r '.scopeId'
}

create_asset() {
  local program_id="$1"
  local scope_id="$2"
  local type="$3"
  local value="$4"
  local subtype="$5"
  local metadata="$6"
  local tags="$7"

  post_json /assets "$(jq -cn \
    --arg programId "$program_id" \
    --arg scopeId "$scope_id" \
    --arg type "$type" \
    --arg value "$value" \
    --arg subtype "$subtype" \
    --argjson metadata "$metadata" \
    --argjson tags "$tags" \
    '{
      programId:$programId,
      scopeId:($scopeId | if length > 0 then . else null end),
      type:$type,
      value:$value,
      subtype:($subtype | if length > 0 then . else null end),
      discoveredByTaskId:null,
      metadata:$metadata,
      tags:$tags
    }')" \
    | jq -r '.assetId'
}

create_relationship() {
  local from_asset_id="$1"
  local to_asset_id="$2"
  local edge_type="$3"

  post_json /assets/relationships "$(jq -cn \
    --arg fromAssetId "$from_asset_id" \
    --arg toAssetId "$to_asset_id" \
    --arg edgeType "$edge_type" \
    '{fromAssetId:$fromAssetId,toAssetId:$toAssetId,edgeType:$edgeType,discoveredByTaskId:null}')" \
    >/dev/null
}

register_worker() {
  local worker_id="$1"
  local worker_type="$2"
  local requires_http="$3"
  local running_tasks="$4"
  local max_concurrency="$5"

  post_json /workers/register "$(jq -cn \
    --arg workerId "$worker_id" \
    --arg workerType "$worker_type" \
    --argjson requiresHttp "$requires_http" \
    --argjson maxConcurrency "$max_concurrency" \
    '{
      workerId:$workerId,
      capability:{
        workerType:$workerType,
        subscribedAssetTypes:["Domain","Subdomain","Url","HtmlPage","JavaScriptFile"],
        producedAssetTypes:["Subdomain","Url","HttpResponse","ApiEndpoint","FindingCandidate"],
        requiresHttp:$requiresHttp,
        supportsCheckpoint:true,
        maxConcurrency:$maxConcurrency
      },
      version:"demo"
    }')" \
    >/dev/null

  post_json /workers/heartbeat "$(jq -cn \
    --arg workerId "$worker_id" \
    --arg workerType "$worker_type" \
    --argjson runningTasks "$running_tasks" \
    --argjson maxConcurrency "$max_concurrency" \
    --arg seenAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    '{workerId:$workerId,workerType:$workerType,runningTasks:$runningTasks,maxConcurrency:$maxConcurrency,seenAt:$seenAt}')" \
    >/dev/null
}

progress_first_task_for_capability() {
  local capability="$1"
  local worker_id="$2"
  local progress="$3"
  local message="$4"
  local task_id

  task_id="$(get_json /tasks | jq -r \
    --arg capability "$capability" \
    '.[] | select(.workerCapability == $capability and (.state == "Requested" or .state == "Queued")) | .taskId' \
    | head -n 1)"

  if [[ -z "$task_id" ]]; then
    return
  fi

  post_json /tasks/lease "$(jq -cn \
    --arg workerId "$worker_id" \
    --arg capability "$capability" \
    '{workerId:$workerId,workerCapability:$capability,leaseDuration:"00:05:00"}')" \
    >/dev/null

  post_json "/tasks/${task_id}/start?workerId=${worker_id}" '{}' >/dev/null
  post_json "/tasks/${task_id}/progress" "$(jq -cn \
    --argjson progress "$progress" \
    --arg message "$message" \
    '{progressPercent:$progress,progressMessage:$message,checkpointJson:null}')" \
    >/dev/null
}

complete_first_running_task_for_capability() {
  local capability="$1"
  local task_id

  task_id="$(get_json /tasks | jq -r \
    --arg capability "$capability" \
    '.[] | select(.workerCapability == $capability and .state == "Running") | .taskId' \
    | head -n 1)"

  if [[ -z "$task_id" ]]; then
    return
  fi

  post_json "/tasks/${task_id}/complete" '{"partiallySucceeded":false,"outputSummaryJson":"{\"demo\":true,\"producedAssets\":4}"}' >/dev/null
}

touch_rate_limits() {
  local program_id="$1"
  local scope_id="$2"

  for worker_type in HttpProbeWorker HtmlDomSpiderWorker JsEndpointExtractorWorker; do
    post_json /rate-limits/check "$(jq -cn \
      --arg programId "$program_id" \
      --arg scopeId "$scope_id" \
      --arg host "app.${DEMO_DOMAIN}" \
      --arg registeredDomain "$DEMO_DOMAIN" \
      --arg workerType "$worker_type" \
      '{
        programId:$programId,
        scopeId:$scopeId,
        host:$host,
        registeredDomain:$registeredDomain,
        ip:null,
        workerType:$workerType,
        proxyId:null,
        permitCount:1
      }')" \
      >/dev/null
  done
}

require_tool curl
require_tool jq
wait_for_gateway

program_id="$(create_program)"
domain_scope_id="$(ensure_scope "$program_id" "domain" "$DEMO_DOMAIN" "Include" "Primary demo apex domain")"
wildcard_scope_id="$(ensure_scope "$program_id" "wildcard-domain" "*.${DEMO_DOMAIN}" "Include" "Demo wildcard coverage")"
ensure_scope "$program_id" "wildcard-domain" "*.internal.${DEMO_DOMAIN}" "Exclude" "Excluded internal-only hosts" >/dev/null

echo "Program: ${DEMO_PROGRAM_NAME} (${program_id})"
echo "Scope: ${DEMO_DOMAIN} (${domain_scope_id})"

post_json /scan-plans/domain-discovery "$(jq -cn \
  --arg programId "$program_id" \
  --arg scopeId "$domain_scope_id" \
  --arg domain "$DEMO_DOMAIN" \
  '{programId:$programId,scopeId:$scopeId,domain:$domain}')" \
  >/dev/null

domain_asset_id="$(create_asset "$program_id" "$domain_scope_id" Domain "$DEMO_DOMAIN" "" '{"source":"demo-seed"}' '["root","demo"]')"
app_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" Subdomain "app.${DEMO_DOMAIN}" "" '{"source":"subfinder","confidence":"high"}' '["live","demo"]')"
api_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" Subdomain "api.${DEMO_DOMAIN}" "" '{"source":"amass","confidence":"medium"}' '["api","demo"]')"
ip_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" Ip "203.0.113.42" "" '{"asn":"AS64500","cloud":"demo-cloud"}' '["network"]')"
url_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" Url "https://app.${DEMO_DOMAIN}/login" LoginPage '{"httpStatus":"200","contentType":"text/html","size":"18422"}' '["login","interesting"]')"
admin_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" Url "https://app.${DEMO_DOMAIN}/admin" AdminRoute '{"httpStatus":"403","contentType":"text/html","size":"8931"}' '["admin","review"]')"
js_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" JavaScriptFile "https://app.${DEMO_DOMAIN}/static/app.bundle.js" "" '{"sha256":"demo-js-bundle","size":"241991"}' '["javascript"]')"
endpoint_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" ApiEndpoint "https://api.${DEMO_DOMAIN}/graphql" GraphQL '{"method":"POST","authHint":"bearer"}' '["graphql","api"]')"
finding_asset_id="$(create_asset "$program_id" "$wildcard_scope_id" FindingCandidate "Possible API key in app.bundle.js" SecretCandidate '{"file":"app.bundle.js","pattern":"AKIA-like token","confidence":"low"}' '["candidate","secret"]')"

create_relationship "$domain_asset_id" "$app_asset_id" has_subdomain
create_relationship "$domain_asset_id" "$api_asset_id" has_subdomain
create_relationship "$app_asset_id" "$ip_asset_id" resolves_to
create_relationship "$app_asset_id" "$url_asset_id" exposes_url
create_relationship "$url_asset_id" "$admin_asset_id" links_to
create_relationship "$url_asset_id" "$js_asset_id" loads_script
create_relationship "$js_asset_id" "$endpoint_asset_id" references_endpoint
create_relationship "$js_asset_id" "$finding_asset_id" contains_candidate

register_worker demo-http-01 HttpProbeWorker true 2 50
register_worker demo-html-01 HtmlDomSpiderWorker true 1 25
register_worker demo-js-01 JsEndpointExtractorWorker false 0 20

progress_first_task_for_capability HttpProbeWorker demo-http-01 65 "Probing seeded demo hosts"
progress_first_task_for_capability HtmlDomSpiderWorker demo-html-01 35 "Extracting links from seeded login page"
progress_first_task_for_capability JsEndpointExtractorWorker demo-js-01 80 "Extracting API endpoints from bundle"
complete_first_running_task_for_capability JsEndpointExtractorWorker
touch_rate_limits "$program_id" "$domain_scope_id"

echo "Seeded demo assets, relationships, tasks, workers, rate-limit buckets, and events."
echo "Open Argus Web and use the Asset Explorer, Scope Explorer, Task Monitor, Worker Fleet, Live Events, Rate Limits, and Scan Plans tabs."
