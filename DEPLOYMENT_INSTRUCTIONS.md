# Deployment Instructions

Argus deploys to Google Kubernetes Engine using:

- Aspire AppHost for the source topology.
- Aspirate for Kubernetes manifest generation and container builds.
- Terraform for GCP/GKE infrastructure, manifest application, and worker autoscaling.

## Prerequisites

- GCP project access with permissions for GKE, Artifact Registry, IAM, and Workload Identity.
- Terraform 1.7 or newer.
- .NET SDK from `global.json`.
- Docker authenticated to Artifact Registry.
- `gcloud` authenticated to the target project.

## Bootstrap Infrastructure

```bash
terraform -chdir=deploy/terraform init
terraform -chdir=deploy/terraform apply \
  -var='project_id=project-30b3b95e-ed2b-4573-98a'
```

After the first apply:

```bash
gcloud container clusters get-credentials argus-gke \
  --region us-central1 \
  --project project-30b3b95e-ed2b-4573-98a
```

## Generate Kubernetes Manifests With Aspirate

```bash
dotnet tool restore
cd src/Argus.AppHost
aspirate init
aspirate generate --non-interactive --disable-secrets
```

The default output is:

```text
src/Argus.AppHost/aspirate-output
```

For CI, `ARGUS_CONTAINER_REGISTRY` and `ARGUS_CONTAINER_TAG` are passed into the AppHost project so generated image references use Artifact Registry and the current commit SHA.

## Apply App And Autoscaling

```bash
terraform -chdir=deploy/terraform apply \
  -var='project_id=project-30b3b95e-ed2b-4573-98a' \
  -var='apply_aspirate_manifests=true' \
  -var='aspirate_manifest_dir=../../src/Argus.AppHost/aspirate-output'
```

Terraform creates HPAs for the continuous background workers. Ephemeral worker hosts are deployed by the generated manifests but are not part of the continuous worker HPA set.

## CI/CD

Pushing to `main` runs `.github/workflows/cd-gcp.yml`, which provisions GKE, runs Aspirate, applies the generated manifests, and reports deployments/HPAs in the `argus` namespace.

Required GitHub secrets:

- `WIF_PROVIDER`
- `WIF_SERVICE_ACCOUNT`

## Verification

```bash
kubectl -n argus get pods
kubectl -n argus get deploy
kubectl -n argus get hpa
kubectl -n argus logs deploy/argus-web
```

Use Terraform output to get the cluster credential command:

```bash
terraform -chdir=deploy/terraform output get_credentials_command
```
