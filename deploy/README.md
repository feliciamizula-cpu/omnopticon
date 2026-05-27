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

`.github/workflows/cd-gcp.yml` is now incremental by default.

For service-only changes it:

1. detects impacted services from `.ci/service-map.yml`;
2. builds and pushes only those service images;
3. patches only the matching Kubernetes Deployment images with `kubectl set image`;
4. waits only for the impacted rollouts.

For topology or global changes it still uses the full path:

1. provision/update Terraform infrastructure;
2. regenerate Kubernetes manifests from Aspire;
3. apply manifests through Terraform;
4. wait for the public service endpoints.

See `docs/deployment-incremental.md` for local Compose commands, detector usage, and rollback instructions.
