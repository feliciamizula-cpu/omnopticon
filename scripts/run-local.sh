#!/usr/bin/env bash
# Launch the Argus core topology from prebuilt binaries (no Aspire/DCP, no per-service build).
# Services are wired together with explicit Aspire-style service-discovery env vars.
set -u

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

REPO=/workspaces/omnopticon
BIN="$REPO/artifacts/bin"
LOGS=/tmp/argus-logs
mkdir -p "$LOGS"

# Shared infra connection strings (native services on localhost).
export ConnectionStrings__argusdb="Host=localhost;Port=5432;Database=argusdb;Username=argus;Password=argus;"
export ConnectionStrings__eventbus="amqp://guest:guest@localhost:5672/"
export ConnectionStrings__redis="localhost:6379"
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ENVIRONMENT=Development

# name:project:port  (service-discovery name = resource name)
SERVICES=(
  "realtime-service:Argus.RealtimeService:5101"
  "asset-service:Argus.AssetService:5102"
  "artifact-service:Argus.ArtifactService:5103"
  "finding-service:Argus.FindingService:5104"
  "task-service:Argus.TaskService:5105"
  "program-scope-service:Argus.ProgramScopeService:5106"
  "rate-limit-service:Argus.RateLimitService:5107"
  "scan-orchestrator-service:Argus.ScanOrchestratorService:5108"
  "event-router-service:Argus.EventRouterService:5109"
  "proxy-registry-service:Argus.ProxyRegistryService:5110"
  "request-tool-service:Argus.RequestToolService:5111"
  "agent-service:Argus.AgentService:5112"
)

# Build the service-discovery env block once; every process gets the full map (harmless extras).
DISCOVERY=()
for entry in "${SERVICES[@]}"; do
  name="${entry%%:*}"; rest="${entry#*:}"; port="${rest#*:}"
  DISCOVERY+=("services__${name}__http__0=http://localhost:${port}")
done

launch() { # name project port
  local name="$1" proj="$2" port="$3"
  local dll="$BIN/$proj/debug/$proj.dll"
  if [ ! -f "$dll" ]; then echo "MISSING $dll"; return 1; fi
  env "${DISCOVERY[@]}" \
      ASPNETCORE_URLS="http://0.0.0.0:${port}" \
      ASPNETCORE_HTTP_PORTS="${port}" \
      dotnet "$dll" > "$LOGS/${name}.log" 2>&1 &
  echo "started $name (pid $!) on :$port"
}

for entry in "${SERVICES[@]}"; do
  name="${entry%%:*}"; rest="${entry#*:}"; proj="${rest%%:*}"; port="${rest#*:}"
  launch "$name" "$proj" "$port"
  sleep 1   # stagger to avoid a thundering-herd of migrations
done

# Web UI — calls services directly via the discovery map; self-calls on 8082.
env "${DISCOVERY[@]}" \
    ASPNETCORE_URLS="http://0.0.0.0:8082" \
    ASPNETCORE_HTTP_PORTS="8082" \
    dotnet "$BIN/Argus.Web/debug/Argus.Web.dll" > "$LOGS/argus-web.log" 2>&1 &
echo "started argus-web (pid $!) on :8082"

echo "logs in $LOGS"
