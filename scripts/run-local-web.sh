#!/usr/bin/env bash
set -e

# Local development helper for Argus.Web.
# Runs the UI against staging backend.
# Override STAGING_GATEWAY if the staging IP changes.
STAGING_GATEWAY=http://34.63.95.161:8081

# Export environment variables used by Argus.Web to locate backend services.
export ARGUS_PROGRAM_SCOPE_SERVICE=$STAGING_GATEWAY
export ARGUS_ASSET_SERVICE=$STAGING_GATEWAY
export ARGUS_ARTIFACT_SERVICE=$STAGING_GATEWAY
export ARGUS_FINDING_SERVICE=$STAGING_GATEWAY
export ARGUS_TASK_SERVICE=$STAGING_GATEWAY
export ARGUS_AGENT_SERVICE=$STAGING_GATEWAY
export ARGUS_RATE_LIMIT_SERVICE=$STAGING_GATEWAY
export ARGUS_REALTIME_SERVICE=$STAGING_GATEWAY
export ARGUS_EVENT_ROUTER_SERVICE=$STAGING_GATEWAY

# Optional: override the UI listening port (default 5173).
export ASPNETCORE_URLS=http://localhost:5173

# Run the web UI with hot-reload support.
# Use `dotnet watch` to automatically rebuild on source changes.
# For a plain run without hot-reload, replace with:
#   dotnet run --project src/Argus.Web/Argus.Web.csproj

dotnet watch run --project src/Argus.Web/Argus.Web.csproj
