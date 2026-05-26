# Argus Deployment

Argus now deploys to GKE through Terraform plus Kubernetes manifests generated from the Aspire AppHost by Aspirate.

## Layout

- `terraform/` provisions GKE, Artifact Registry, the Argus namespace, KEDA, generated manifests, and worker HPAs.
- `aspirate/` documents the manifest generation step.
- `Dockerfile.service` remains available for direct image builds when needed by local or fallback workflows.

## First Deploy

```bash
terraform -chdir=deploy/terraform init
terraform -chdir=deploy/terraform apply \
  -var='project_id=project-30b3b95e-ed2b-4573-98a'
```

Then generate manifests from Aspire:

```bash
dotnet tool restore
cd src/Argus.AppHost
aspirate init
aspirate generate --non-interactive --disable-secrets
```

Finally apply the app manifests and autoscaling resources:

```bash
terraform -chdir=deploy/terraform apply \
  -var='project_id=project-30b3b95e-ed2b-4573-98a' \
  -var='apply_aspirate_manifests=true' \
  -var='aspirate_manifest_dir=../../src/Argus.AppHost/aspirate-output'
```

## Worker Runtime Classes

- Continuous background workers: task-lease recon workers such as `http-probe-worker`, `dns-resolver-worker`, `asset-scoring-worker`, and the other durable pipeline workers. Terraform creates HPAs for these deployments.
- Ephemeral worker hosts: `http-worker` and `asset-storage-worker`.
- Validation service: `validation-worker`, which still exposes HTTP endpoints for promotion/dismissal workflows.

## CI/CD

`.github/workflows/cd-gcp.yml` now runs the same flow:

1. Authenticate to GCP.
2. Provision/update Terraform infrastructure.
3. Generate and push Kubernetes manifests with Aspirate.
4. Re-apply Terraform with generated manifest application enabled.

The old Cloud Run path is no longer the primary deployment path.
