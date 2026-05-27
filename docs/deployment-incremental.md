# Incremental Deployment Guide

This repository now supports service-map driven incremental builds and deployments.

## Source of truth

`.ci/service-map.yml` defines every independently deployable service or worker:

- project path
- assembly name
- image name
- Docker Compose service name
- Kubernetes Deployment and container name
- mapped test projects

When adding a service, update this file first.

## Detect impacted services locally

```bash
python -m pip install pyyaml
BASE_SHA=$(git merge-base main HEAD)
HEAD_SHA=$(git rev-parse HEAD)
BASE_SHA=$BASE_SHA HEAD_SHA=$HEAD_SHA python scripts/ci/detect-impacted-services.py
```

Example for an `AssetService`-only change:

```json
{
  "count": 1,
  "matrix": {
    "include": [
      {
        "key": "asset-service"
      }
    ]
  }
}
```

## Validate the service map

```bash
python scripts/ci/validate-service-map.py
```

The validator checks that mapped projects and tests exist and warns when a project is not represented.

## Local incremental Compose deployment

Start infrastructure once:

```bash
docker compose --env-file deploy/argus.env \
  -f deploy/compose.infra.yaml \
  up -d postgres redis rabbitmq minio
```

Build and restart one service without restarting dependencies:

```bash
docker compose --env-file deploy/argus.env \
  -f deploy/compose.infra.yaml \
  -f deploy/compose.apps.yaml \
  -f deploy/compose.build.yaml \
  build asset-service

docker compose --env-file deploy/argus.env \
  -f deploy/compose.infra.yaml \
  -f deploy/compose.apps.yaml \
  -f deploy/compose.build.yaml \
  up -d --no-deps asset-service
```

Run the full local topology when topology or shared infrastructure changes:

```bash
docker compose --env-file deploy/argus.env \
  -f deploy/compose.infra.yaml \
  -f deploy/compose.apps.yaml \
  -f deploy/compose.build.yaml \
  up -d --build
```

## GKE deployment behavior

`.github/workflows/cd-gcp.yml` now has two deployment paths.

### Service-only change

1. Detect impacted services.
2. Build and push only impacted images.
3. Patch only the impacted Kubernetes Deployment image:

```bash
kubectl -n argus set image deployment/asset-service \
  asset-service="$REGISTRY/asset-service:$GITHUB_SHA"

kubectl -n argus rollout status deployment/asset-service --timeout=180s
```

### Topology/global change

The workflow still performs the slower full path for changes to AppHost, Terraform, Compose topology, Dockerfile, package versions, or the service map.

## Pull request validation

`.github/workflows/incremental-ci.yml` runs on pull requests and:

1. validates `.ci/service-map.yml`;
2. detects impacted services;
3. restores/builds only mapped impacted projects;
4. runs mapped tests for those projects;
5. runs full solution and Compose validation only for topology/global changes.

## Rollback one service

```bash
kubectl -n argus rollout undo deployment/asset-service
kubectl -n argus rollout status deployment/asset-service --timeout=180s
```

## Add a new service

1. Create the .NET project.
2. Add it to `eShop.slnx`.
3. Add an entry to `.ci/service-map.yml`.
4. Add it to `deploy/compose.apps.yaml`.
5. Add a matching build override to `deploy/compose.build.yaml`.
6. Add it to `src/Argus.AppHost/Program.cs` if it should be in Aspire/GKE topology.
7. Add mapped test projects.
8. Run `python scripts/ci/validate-service-map.py`.
