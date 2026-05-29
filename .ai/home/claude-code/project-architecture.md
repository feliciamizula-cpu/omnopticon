# Project Architecture for claude-code

Document insights about the project's overall architecture, components, data flow, and design decisions.

## Blazor Render Mode (2026-05-29)

The app uses Blazor 8 Web App with **global Interactive Server** rendering. The render mode is set once on `<Routes />` in `App.razor` (`InteractiveServerRenderMode(prerender: false)`), which puts the entire routing tree — AppShell layout and all pages — in the same interactive server circuit. Individual page files do NOT set `@rendermode`; they inherit from the Routes ancestor.

This matters because MudBlazor services (PopoverService, DialogService, SnackbarService) are circuit-scoped. Providers in AppShell (`<MudPopoverProvider />`, `<MudDialogProvider />`, `<MudSnackbarProvider />`) must be in the same circuit as the components that use them.

## Layout

- **AppShell** (`Layout/AppShell.razor`) — root layout; contains MudBlazor theme + providers, sidebar nav, top bar.
- **SidebarNav**, **TopBar** — layout sub-components.

## Shared Components

- **ArgusGrid** (`Components/ArgusGrid.razor`) — typed data grid wrapping `MudDataGrid` with built-in right-click context menu (`MudMenu`). Used on Operations, Assets, and other pages.

## Pages

All pages: Operations, Assets, Agents, AgentTasks, AgentSchedules, Todos, CodeReviews, SystemReports, Environments, ProviderUsage, CommandCenter.

## Real-time

`DevelopmentRealtimeClient` / `DevelopmentRealtimeNotifier` + `ArgusHub` (SignalR) push live updates to pages (e.g., new assets on Operations page).

## Backend-for-Frontend

`Program.cs` acts as a BFF — all `/ui/*` endpoints proxy to downstream microservices (asset-service, agent-service, program-scope-service, realtime-service, etc.) configured via `ARGUS_*_SERVICE` env vars.

## Database schema provisioning (IMPORTANT, 2026-05-29)

All services share ONE Postgres database `argusdb`. Most services call `EnsureCreatedAsync()` at startup, which **no-ops once the database already exists** (EF Core only creates tables when the whole DB is absent). AgentService creates `argusdb` first via `MigrateAsync()` (`__EFMigrationsHistory` present), so every other service's `EnsureCreatedAsync()` silently skipped table creation — asset/task/finding tables never existed, and queries failed with `42P01 relation does not exist`.

Fix pattern (asset-service, 2026-05-29): `AssetSchemaInitializer.EnsureAssetSchemaCreatedAsync` generates DDL from the EF model via `GenerateCreateScript()` and rewrites `CREATE TABLE/INDEX` → `IF NOT EXISTS`, run on every startup. The inbox/outbox tables already used this idempotent pattern (`OutboxDatabaseInitializer`). **Other Postgres-backed services (task, finding, etc.) have the same latent bug** — but task/finding are being retired, so only asset-service was fixed.

Connection strings come from per-service `*-env` configmaps (`ConnectionStrings__argusdb`). Some services have none and fall back to in-memory stores (ephemeral) — e.g. program-scope-service, which is why programs survive only until pod restart.

## Asset / artifact storage (current state, 2026-05-29)

- Asset **metadata** → Postgres (asset-service `assets` table).
- Raw artifact **bytes** → currently `EfArtifactStore` (Postgres) in artifact-service. The bucket-capable `S3ArtifactStore` building block exists (content-addressable, SHA-256, presigned URLs, GCS-compatible via S3 endpoint) but is **NOT wired in**, and no bucket is provisioned. Moving bytes to a bucket + lifecycle tiering is stream E (not yet done).
- Asset lifecycle is event-driven: `AssetConfirmed` → `AssetStorageWorker.StoreAssetAsync` (tasks have been retired).

## Realtime gap (stream D, not yet done)

Web app does NOT consume the RabbitMQ integration event bus. SignalR (`ArgusHub` at `/hubs/argus`) only pushes `DevelopmentDataChanged{area,reason}` from the web's own BFF mutation endpoints. Backend-driven changes (worker asset discovery) never reach the grids live. To make grids live, bridge integration events → SignalR with batching + Redis caching.

## Web BFF gateway error handling (2026-05-29)

`ArgusUiGateway.GetJsonAsync` fabricates empty fallbacks on downstream failure (silent). Use `GetJsonOrThrowAsync` (logs + rethrows) where the UI should surface a real error. `/ui/ops/assets` now returns 502 on asset-service failure; Operations.razor shows a distinct error banner vs an empty state.
