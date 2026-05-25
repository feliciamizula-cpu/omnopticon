# Deployment Instructions

This document explains how to deploy the Argus application via GitHub Actions and manually using Docker.

---

## 1. Deploy via GitHub Actions (CD)

### Overview
Pushing to the `main` branch automatically triggers the CD pipeline which builds the Docker images and deploys them to GCP Cloud Run staging.

### Trigger
- Push to `main` branch: `cd-gcp.yml` workflow runs automatically
- Manual trigger: Navigate to the workflow in GitHub UI → click "Run workflow"

### What happens during the workflow
1. **Build and Push**: Docker image is built with Git SHA and timestamp labels, then pushed to GCP Artifact Registry at `us-central1-docker.pkg.dev/project-30b3b95e-ed2b-4573-98a/argus-staging/argus-web:latest`
2. **Deploy to Cloud Run**: The image is deployed to the `argus-web-staging` Cloud Run service in `us-central1`

### Required Secrets
The workflow requires the following GitHub Secrets configured:
- `WIF_PROVIDER` — Workload Identity Federation provider
- `WIF_SERVICE_ACCOUNT` — Workload Identity service account email
- `GCP_PROJECT_ID` — GCP project ID
- `GCP_SA_KEY` — GCP service account key (for deploy step)

### Check the workflow status
1. Go to the repository on GitHub
2. Navigate to **Actions** tab
3. Look for "CD - Deploy to GCP Staging"
4. Check the status of the most recent run

### See also
- `.github/workflows/cd-gcp.yml` — the workflow definition

---

## 2. Deploy Manually with Docker

### Local Docker Compose (entire stack)

```bash
# From project root
cd deploy

# Set required environment variables
export ARGUS_POSTGRES_PASSWORD="your-secure-password"
export ARGUS_RABBITMQ_PASSWORD="your-secure-password"
export ARGUS_MINIO_ACCESS_KEY="your-access-key"
export ARGUS_MINIO_SECRET_KEY="your-secret-key"

# Optionally set API keys for agent service
export ANTHROPIC_API_KEY="sk-ant-..."
export OPENAI_API_KEY="sk-..."

# Start all services
docker compose up -d

# The web app will be available at http://localhost:8082
```

### Build and push Docker images manually

```bash
# From project root
cd deploy

# Build all images
docker compose build

# Push to a registry (edit compose.yaml to change registry)
docker compose push
```

### Run just the web app container

```bash
# Build the web app image
docker build -f deploy/Dockerfile.service \
  --build-arg GIT_SHA=$(git rev-parse --short HEAD) \
  --build-arg BUILD_DATE=$(date -u +"%Y-%m-%dT%H:%M:%SZ") \
  -t argus-web:latest .

# Run the container
docker run -d \
  -p 8082:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__argusdb="Host=postgres;Port=5432;Database=argusdb;Username=postgres;Password=argus-dev-password" \
  argus-web:latest
```

### GCP Artifact Registry (manual push)

```bash
# Authenticate to GCP
gcloud auth configure-docker us-central1-docker.pkg.dev

# Tag the image
docker tag argus-web:latest \
  us-central1-docker.pkg.dev/project-30b3b95e-ed2b-4573-98a/argus-staging/argus-web:latest

# Push to Artifact Registry
docker push us-central1-docker.pkg.dev/project-30b3b95e-ed2b-4573-98a/argus-staging/argus-web:latest
```

### Deploy to GCP Cloud Run (manual)

```bash
# Set project and region
gcloud config set project project-30b3b95e-ed2b-4573-98a
gcloud config set compute/region us-central1

# Deploy to Cloud Run
gcloud run deploy argus-web-staging \
  --image us-central1-docker.pkg.dev/project-30b3b95e-ed2b-4573-98a/argus-staging/argus-web:latest \
  --platform managed \
  --region us-central1 \
  --allow-unauthenticated \
  --port 8080 \
  --memory 512Mi \
  --cpu 1
```

---

## 3. Current Deployment Status

- **Production URL**: Not currently deployed to production
- **Staging URL**: http://34.63.95.161:8082/ (Blazor app on port 8082)
- **GCP Project**: project-30b3b95e-ed2b-4573-98a
- **GKE Cluster**: argus-cluster (us-central1, RUNNING, 3 nodes) — empty, no workloads
- **Artifact Registry**: `us-central1-docker.pkg.dev/project-30b3b95e-ed2b-4573-98a/argus-staging/argus-web`

---

## 4. Quick Deploy Checklist

1. **GitHub Actions (automated)**:
   - [ ] Make changes and push to `main`
   - [ ] Monitor Actions tab for build/deploy status
   - [ ] Verify at http://34.63.95.161:8082/

2. **Manual Docker**:
   - [ ] `cd deploy`
   - [ ] `docker compose build`
   - [ ] `docker compose push` (if using remote registry)
   - [ ] Verify running containers: `docker compose ps`