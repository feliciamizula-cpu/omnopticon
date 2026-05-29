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
