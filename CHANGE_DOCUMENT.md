# Argus Application Fix Report

## Scope

I reviewed and patched the uploaded .NET solution with a focus on the end-to-end reconnaissance pipeline, worker execution reliability, event dispatch, asset/task handoffs, and obvious stubbed or unsafe implementation points.

The intended flow now has concrete handoffs for:

`Target created → Domain asset seeded → AssetDiscovered(Domain) → subdomain enumeration tasks → Subdomain assets → spider/http tasks → URL/HTML/JS/API assets → HTTP confirmation → AssetConfirmed → regex/JS/spider/fingerprint follow-up workers → FindingCandidate assets`

## Key functional changes

### Target and domain seeding

- Added target persistence and target endpoints to `Argus.ProgramScopeService`.
- When a target is created or updated, active root domains are normalized, in-scope rules are created as needed, and corresponding `Domain` assets are posted into `Argus.AssetService`.
- This gives the event pipeline a concrete starting point when users add targets.

### Asset service pipeline support

- Added asset confirmation support with `POST /assets/{assetId}/confirm`.
- Confirmation marks assets as confirmed/verified and emits `AssetConfirmed`.
- Added store methods for asset confirmation and last-scanned updates.
- Removed a duplicate, unused `AssetEndpoints` implementation from `Program.cs` so routes only live in the real endpoint module.
- Added `Form` as a first-class asset type because `HtmlDomSpider` already produced form assets; without this, form outputs were discarded as unknown asset types.
- Added normalization, category/type-key/subcategory, and staleness behavior for form assets.

### Event bus fixes

- Fixed RabbitMQ consumer typed dispatch. The consumer previously deserialized into a generic `JsonElement` envelope but invoked typed handlers with the wrong envelope type, which prevented event handlers from running correctly.
- Expanded the event type registry/consumer routing to include missing events such as rate-limit backpressure, proxy events, artifacts, evidence, and findings.
- Registered `IEventTypeRegistry` consistently for all event bus modes.
- Made retry header parsing resilient to RabbitMQ header representations and avoided sending normal redeliveries directly to the dead-letter queue.

### Task pipeline orchestration

- Added an `AssetPipelineConsumer` in `Argus.TaskService`.
- `AssetDiscovered(Domain)` now queues `SubfinderWorker` and `AmassWorker`.
- Discovered web-relevant assets queue HTTP probe, DOM spider, and regex scanner work as appropriate.
- `AssetConfirmed(Url/HtmlPage)` queues regex scan, DOM spider, headless spider, and fingerprint tasks.
- `AssetConfirmed(JavaScriptFile)` queues JS extraction.
- Fixed in-memory task leasing to honor subscribed asset types the same way the EF/PostgreSQL store does.
- Fixed PostgreSQL task leasing SQL parameterization for subscribed asset types.

### Worker reliability fixes

- Fixed shutdown-drain behavior in polling and event-driven worker services so stop operations do not consume a semaphore slot and hang.
- Made background worker task execution catch cancellation and pre-task failures safely.
- Hardened heartbeat loops so transient heartbeat failures do not fault worker tasks or block cleanup.
- Made worker payload helper methods tolerate malformed JSON rather than throwing.

### HTTP probe fixes

- HTTP probing now confirms the input asset when a target responds successfully.
- Invalid TLS certificate acceptance is opt-in through `ARGUS_HTTP_PROBE_ALLOW_INVALID_TLS`; the previous behavior accepted invalid TLS by default.
- Removed ad-hoc debug output and added proper structured logging for best-effort confirmation failures.

### Regex scanner worker

- Added a new `Argus.Workers.RegexScanner` worker project and registered it in the solution/AppHost.
- The worker scans asset value, subtype, metadata, and tags for reportable bug-bounty indicators:
  - AWS access keys
  - Google API keys
  - Slack tokens
  - private key material
  - JWTs
  - generic secret assignments
  - exposed `.env` paths
  - exposed source-control folders
  - S3 bucket URLs
  - sensitive backup/config/credential paths
- Matches are emitted as `FindingCandidate` assets with redacted evidence snippets and reporting metadata.

### Replaced stubbed worker behavior

- `SubfinderWorker` no longer emits fake `www`/`staging` subdomains. It invokes the configured `subfinder` executable, validates results, handles timeouts, and returns an empty result if the tool is unavailable or disabled.
- `JsExtractorWorker` no longer emits synthetic endpoints. It fetches real JavaScript, extracts absolute/relative endpoints, classifies URLs/API endpoints/JS files, and emits finding candidates for secret-like assignments.
- `HeadlessSpiderWorker` no longer emits synthetic routes. Because no browser automation package is configured in the project, it now performs deterministic HTTP extraction of DOM links and inline endpoints and records that mode in the output summary.

## Modified files

```text
eShop.slnx
src/Argus.AppHost/Argus.AppHost.csproj
src/Argus.AppHost/Program.cs
src/BuildingBlocks/Argus.BuildingBlocks.EventBus/EventTypeRegistry.cs
src/BuildingBlocks/Argus.BuildingBlocks.EventBus/RabbitMqConsumerService.cs
src/BuildingBlocks/Argus.BuildingBlocks.EventBus/ServiceCollectionExtensions.cs
src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusEventDrivenWorkerService.cs
src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusWorkerBackgroundService.cs
src/BuildingBlocks/Argus.BuildingBlocks.Workers/WorkerHelpers.cs
src/Contracts/Argus.Contracts/Assets/AssetContracts.cs
src/Services/Argus.AssetService/Data/AssetRecord.cs
src/Services/Argus.AssetService/Endpoints/AssetEndpoints.cs
src/Services/Argus.AssetService/Normalization/AssetNormalizer.cs
src/Services/Argus.AssetService/Program.cs
src/Services/Argus.AssetService/Stores/EfAssetStore.cs
src/Services/Argus.AssetService/Stores/IAssetStore.cs
src/Services/Argus.AssetService/Stores/InMemoryAssetStore.cs
src/Services/Argus.ProgramScopeService/Program.cs
src/Services/Argus.TaskService/Program.cs
src/Workers/Argus.Workers.HeadlessSpider/Program.cs
src/Workers/Argus.Workers.HtmlDomSpider/Program.cs
src/Workers/Argus.Workers.HttpProbe/Program.cs
src/Workers/Argus.Workers.JsExtractor/Program.cs
src/Workers/Argus.Workers.Subfinder/Program.cs
```

## Added files

```text
src/Workers/Argus.Workers.RegexScanner/Argus.Workers.RegexScanner.csproj
src/Workers/Argus.Workers.RegexScanner/Program.cs
CHANGE_DOCUMENT.md
```

## Validation notes

The sandbox does not have the .NET SDK installed, so I could not run `dotnet build`, `dotnet test`, or migrations locally. I performed static review and kept the patch scoped to the smallest set of files needed to restore the event-driven reconnaissance pipeline and remove obvious stubs/antipatterns.
