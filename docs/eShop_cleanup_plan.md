# eShop Cleanup Plan - Remove Obsolete Commerce Code

## Overview

This document outlines the obsolete eShop commerce projects that can be safely removed from the Argus codebase, as they are not referenced by any Argus bug-bounty component.

---

## Projects to Remove

### Services

| Project | Reason |
|---------|--------|
| `src/Ordering.API` | E-commerce order management - not used |
| `src/Ordering.Domain` | Ordering domain models - not used |
| `src/Ordering.Infrastructure` | Ordering data access - not used |
| `src/OrderProcessor` | Order processing worker - not used |
| `src/PaymentProcessor` | Payment processing - not used |
| `src/Basket.API` | Shopping basket - not used |
| `src/Catalog.API` | Product catalog - not used |
| `src/Identity.API` | User identity/auth - not used |

### Web Apps

| Project | Reason |
|---------|--------|
| `src/WebApp` | E-commerce web UI - not used |
| `src/WebAppComponents` | WebApp components - not used |

### Event Infrastructure

| Project | Reason |
|---------|--------|
| `src/EventBus` | Generic event bus - superseded by `Argus.BuildingBlocks.EventBus` |
| `src/EventBusRabbitMQ` | RabbitMQ event bus - superseded by `Argus.BuildingBlocks.EventBus` |
| `src/IntegrationEventLogEF` | Integration event log for outbox - not used |

### Webhooks

| Project | Reason |
|---------|--------|
| `src/Webhooks.API` | Webhook management - not used |
| `src/WebhookClient` | Webhook client - not used |

### eShop Infrastructure

| Project | Reason |
|---------|--------|
| `src/eShop.AppHost` | eShop Aspire orchestrator - replaced by `Argus.AppHost` |
| `src/eShop.ServiceDefaults` | eShop defaults - replaced by `Argus.ServiceDefaults` |
| `src/HybridApp` | Hybrid app - not used |

### Client App

| Project | Reason |
|---------|--------|
| `src/ClientApp` | Old client application - not used |

---

## Projects to Keep

These projects are actively used by Argus:

| Project | Reason |
|---------|--------|
| `src/Argus.AppHost` | Main Aspire orchestrator for Argus |
| `src/Argus.ApiGateway` | API gateway for Argus |
| `src/Argus.Web` | Web command center UI |
| `src/Argus.ServiceDefaults` | Shared telemetry, health, resilience |
| `src/Contracts/Argus.Contracts` | Shared DTOs and contracts |
| `src/BuildingBlocks/Argus.BuildingBlocks.EventBus` | Event publishing infrastructure |
| `src/BuildingBlocks/Argus.BuildingBlocks.Workers` | Worker host infrastructure |
| `src/Services/Argus.AssetService` | Asset storage |
| `src/Services/Argus.TaskService` | Task orchestration |
| `src/Services/Argus.RateLimitService` | Rate limiting |
| `src/Services/Argus.ProgramScopeService` | Program/scope management |
| `src/Services/Argus.RealtimeService` | Real-time event streaming |
| `src/Services/Argus.ScanOrchestratorService` | Scan workflow orchestration |
| `src/Workers/*` | All Argus workers |

---

## Test Projects to Remove

| Project | Reason |
|---------|--------|
| `tests/Basket.UnitTests` | Tests for removed Basket.API |
| `tests/Catalog.FunctionalTests` | Tests for removed Catalog.API |
| `tests/Ordering.FunctionalTests` | Tests for removed Ordering.API |
| `tests/Ordering.UnitTests` | Tests for removed Ordering domain |
| `tests/ClientApp.UnitTests` | Tests for removed ClientApp |

---

## Files to Remove

```
src/
  eShop.AppHost/
  eShop.ServiceDefaults/
  Ordering.API/
  Ordering.Domain/
  Ordering.Infrastructure/
  OrderProcessor/
  PaymentProcessor/
  Basket.API/
  Catalog.API/
  Identity.API/
  WebApp/
  WebAppComponents/
  Webhooks.API/
  WebhookClient/
  HybridApp/
  EventBus/
  EventBusRabbitMQ/
  IntegrationEventLogEF/
  ClientApp/

tests/
  Basket.UnitTests/
  Catalog.FunctionalTests/
  Ordering.FunctionalTests/
  Ordering.UnitTests/
  ClientApp.UnitTests/

src/ClientApp/ClientApp.sln
tests/ClientApp.UnitTests/ClientApp.UnitTests.sln
```

---

## Steps to Execute

1. **Backup** - Ensure git state is clean or backed up

2. **Remove from solution files** - If there are any solution files referencing removed projects, update them

3. **Delete directories** - Remove all listed directories

4. **Verify build** - Run `dotnet build` to ensure Argus still compiles

5. **Run tests** - Run `dotnet test` to verify no regressions

---

## Post-Cleanup

After cleanup, verify:
- `src/Argus.AppHost` builds and runs
- All Argus services start correctly
- Docker Compose deployment works