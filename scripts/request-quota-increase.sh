#!/bin/bash
#
# Request increase for GCP regional CPU quota to enable larger GKE autoscaling.
# Run with: scripts/request-quota-increase.sh
#

set -euo pipefail

# Validate gcloud authentication
if ! gcloud auth list --filter=status:ACTIVE --format="value(account)" | grep -q "@"; then
  echo "Error: Not authenticated to GCP. Run 'gcloud auth login' first."
  exit 1
fi

PROJECT_ID=$(gcloud config get-value project)
REGION=${1:-us-central1}
QUOTA_NAME="CPUS_ALL_REGIONS"
# Target: 48 vCPUs — supports 12 × e2-standard-2 nodes or 24 × e2-medium nodes
TARGET_QUOTA=48
# Adjustment: +16 vCPUs from typical 32-vCPU default
ADJUSTMENT=$(($TARGET_QUOTA - 32))

echo "Project: $PROJECT_ID"
echo "Region:  $REGION"
echo "Quota:   $QUOTA_NAME (all regions)"
echo "Target:  $TARGET_QUOTA vCPUs (increase by $ADJUSTMENT from default 32)"

echo -e "\n=== Current quota ==="
gcloud compute regions describe "$REGION" --project "$PROJECT_ID" --format='table(quotas:format="name: \"\"{quotas.name}\"\"\tlimit: {quotas.limit}\tusage: {quotas.usage}")' | grep -E "(CPUS|NAME)"

echo -e "\n=== Request quota increase ==="
read -p "Open quota request in browser? (Y/n): " CONFIRM
echo

if [[ "$CONFIRM" =~ [yY] ]]; then
  # Get service ID for compute.googleapis.com
  SERVICE_ID=$(gcloud services list --format=json | jq -r '.[] | select(.config.name == "compute.googleapis.com") | .serviceName')

  # Construct quota request URL
  URL="https://console.cloud.google.com/iam-admin/quotas/details?project=${PROJECT_ID}&service=${SERVICE_ID}&metric=LABEL_CPUS_ALL_REGIONS"

  echo "Opening browser to request quota increase..."
  xdg-open "$URL" 2>/dev/null || echo "Please open this URL in your browser:"
  echo -e "  🔗 $URL"
  echo 
echo "=== INSTRUCTIONS ==="
echo "1. Set 'Locations' to 'All Regions'"
echo "2. Set 'Quota limit' to $TARGET_QUOTA"
echo "3. Justification: GKE autoscaling for {TEAM_NAME} workloads — scale worker pool from 5 to 12 nodes"
echo "4. Submit request"
fi

echo -e "\n=== Next Steps ==="
echo "• After quota approval, increase 'worker_max_node_count' and 'core_max_node_count' in deploy/terraform/variables.tf"
echo "• Set worker_max_node_count = 12  # 12 × e2-medium = 12 vCPUs"
echo "• Set core_max_node_count = 2    # 2 × e2-standard-2 = 4 vCPUs"
echo "                              # Total = 16 vCPUs (within default 32 limit)"
echo "• Scale beyond 32 vCPUs after quota approval"
