#!/bin/bash
# Grant IAM roles to GitHub Actions Workload Identity service account
#
# Before running this, get the WIF_SERVICE_ACCOUNT value:
#   1. Go to https://github.com/anomalyco/omnopticon/settings/secrets/actions
#   2. Find WIF_SERVICE_ACCOUNT and copy its value
#   3. Replace the placeholder below with that value

WIF_SA="PASTE_WIF_SERVICE_ACCOUNT_HERE"
PROJECT_ID="project-30b3b95e-ed2b-4573-98a"

if [ "$WIF_SA" = "PASTE_WIF_SERVICE_ACCOUNT_HERE" ]; then
    echo "ERROR: You need to set WIF_SA first!"
    echo "Go to https://github.com/anomalyco/omnopticon/settings/secrets/actions"
    echo "Find WIF_SERVICE_ACCOUNT, copy the value, and paste it in this script."
    exit 1
fi

echo "Granting roles to: $WIF_SA"

# Container Developer - access GKE clusters, deploy workloads
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/container.developer" \
  --quiet

# Artifact Registry Writer - push Docker images
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/artifactregistry.writer" \
  --quiet

# Service Usage Consumer - list/check API services
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/serviceusage.serviceUsageConsumer" \
  --quiet

# IAM Service Account Admin - create service accounts for GKE nodes
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/iam.serviceAccountAdmin" \
  --quiet

# Compute Admin - for managing GKE node pools
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/compute.admin" \
  --quiet

# Storage Admin - create GCS bucket for Terraform state and manage objects
gcloud projects add-iam-policy-binding $PROJECT_ID \
  --member="serviceAccount:$WIF_SA" \
  --role="roles/storage.admin" \
  --quiet

echo ""
echo "Done! Roles granted."
echo "Now re-run the workflow at:"
echo "https://github.com/anomalyco/omnopticon/actions/workflows/cd-gcp.yml"