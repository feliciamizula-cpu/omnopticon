# Argus Recon Platform

Argus is a distributed reconnaissance platform for scoped bug-bounty asset discovery. It is being rebuilt from the eShop/Aspire foundation into a service-and-worker system that can ingest programs, enforce scope, normalize discovered assets, queue durable work, stream operational events, and show a dense real-time command UI.

## Current Shape

The current implementation includes:

- Aspire orchestration through `src/Argus.AppHost`.
- Shared service defaults for telemetry, health checks, resilience, and service discovery.
- Core services for programs/scopes, assets, tasks, rate limits, scan orchestration, and realtime events.
- Shared contracts for assets, tasks, programs, workers, and integration events.
- Worker host infrastructure with registration, heartbeat, task leasing, progress reporting, rate-limit checks, scope validation, and produced asset publishing.
- MVP worker projects for Amass, Subfinder, DNS resolution, HTTP probing, HTML DOM spidering, JavaScript endpoint extraction, wordlist discovery, headless spidering, fingerprinting, and asset scoring.
- A compact web command center with assets, tasks, workers, events, rate limits, scan plans, and programs.
- A container deployment path in `deploy/`.

The workers are currently adapter shells for proving the end-to-end architecture. Their internals are ready to be replaced with real tool integrations as each adapter matures.

## Local Development

Prerequisites:

- .NET 10 SDK.
- Docker Engine or Docker Desktop for Aspire-managed infrastructure.

Run the Aspire AppHost:

```bash
dotnet run --project src/Argus.AppHost/Argus.AppHost.csproj
```

The console output will include the Aspire dashboard URL. Use that dashboard for local service URLs, logs, traces, metrics, and health status.

## Container Deployment

For a fresh environment outside Aspire:

```bash
cp deploy/argus.env.example deploy/argus.env
docker compose --env-file deploy/argus.env -f deploy/compose.yaml up --build -d
```

Default exposed endpoints:

- Argus Web: `http://localhost:8080`
- API Gateway: `http://localhost:8081`
- Realtime Service: `http://localhost:8082`

See [deploy/README.md](deploy/README.md) for configuration, health checks, update commands, and teardown commands.

## MVP Phase Status

- Phase 1, eShop foundation: mostly complete. Argus AppHost, ServiceDefaults, Web, ApiGateway, Contracts, and BuildingBlocks are in place; remaining work is cleanup of old eShop residue and polish.
- Phase 2, core domain services: mostly complete for the MVP slice. Durable/in-memory implementations exist for program scope, assets, tasks, rate limits, scan orchestration, and realtime state.
- Phase 3, event bus and outbox: partially complete. Event contracts, publisher interfaces, inbox/outbox records, RabbitMQ publishing, and realtime forwarding exist; full transactional outbox dispatch and durable consumer inbox processing remain.
- Phase 4, worker host: mostly complete for the MVP slice. Shared worker behavior and worker projects exist; real Amass/subfinder/headless/tool execution is still pending.
- Phase 5, UI: partially complete. The dense command UI and live refresh are present; deeper grid ergonomics such as column chooser, saved views, keyboard workflows, and asset detail panels remain.

## First End-To-End Target

The first milestone remains:

```text
Create program
  -> Add in-scope domain
  -> Emit DomainAssetDiscovered
  -> Run subfinder/amass tasks
  -> Save subdomains
  -> Emit SubdomainAssetDiscovered
  -> HTTP probe subdomains under rate limit
  -> Save URL and HTTP response assets
  -> Extract links from HTML
  -> Save new URL assets
  -> Show events/assets/tasks live in UI
```

That path is structurally wired. The main remaining work is replacing simulated worker outputs with real tool execution and completing the transactional event/outbox reliability layer.
