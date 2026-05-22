# Argus Deployment

This folder gives Argus a repeatable container deployment path for a fresh environment. Aspire remains the preferred local development orchestrator, but this Compose setup is useful when you want a self-contained stack outside the Aspire dashboard.

## Prerequisites

- Docker Engine or Docker Desktop with Compose v2.
- Access to the repository root.

## First Deploy

From the repository root:

```bash
cp deploy/argus.env.example deploy/argus.env
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

Open:

```text
http://localhost:8080
```

The API gateway is exposed at:

```text
http://localhost:8081
```

The realtime service is exposed at:

```text
http://localhost:8082
```

Seed demo data:

```bash
tools/seed-demo-data.sh
```

## Configuration

Edit `deploy/argus.env` before first use:

- `ARGUS_POSTGRES_PASSWORD`: required for PostgreSQL.
- `ARGUS_RABBITMQ_PASSWORD`: required for RabbitMQ event publishing.
- `ARGUS_WEB_PORT`: host port for `Argus.Web`.
- `ARGUS_GATEWAY_PORT`: host port for `Argus.ApiGateway`.
- `ARGUS_REALTIME_PORT`: host port for direct event-stream access.
- `ARGUS_EXPOSE_HEALTH_ENDPOINTS`: set to `true` for `/health` and `/alive`.

Internal service URLs are injected by `deploy/compose.yaml` through `ARGUS_*_SERVICE` variables. PostgreSQL, Redis, and RabbitMQ are supplied through `ConnectionStrings__argusdb`, `ConnectionStrings__redis`, and `ConnectionStrings__eventbus`.

## Health Checks

Each Argus service maps:

```text
/health
/alive
```

when `ARGUS_EXPOSE_HEALTH_ENDPOINTS=true`.

## Update an Environment

```bash
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

## Stop

```bash
docker compose --env-file deploy/argus.env -f deploy/compose.yaml down
```

To remove persisted PostgreSQL, Redis, and RabbitMQ data:

```bash
docker compose --env-file deploy/argus.env -f deploy/compose.yaml down -v
```

## Notes

- The current workers are adapter shells for the MVP pipeline. Replace their internals with real tool integrations as those adapters mature.
- PostgreSQL tables are created by the services on startup through EF Core `EnsureCreated`.
- RabbitMQ is included because it is part of the target topology; the current event path also publishes to the realtime service for UI visibility.
