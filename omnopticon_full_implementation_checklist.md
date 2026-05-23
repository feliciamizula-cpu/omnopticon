# Omnopticon Full Implementation Checklist

**Repository:** `feliciamizula-cpu/omnopticon`  
**Architecture target:** event-driven, horizontally scalable bug-bounty recon platform  
**Frontend target:** new dark-mode Blazor/.NET security operations console  
**Important instruction:** do **not** use the existing `Argus.Web` UI as UX/design guidance. Treat it as disposable scaffolding only.

---

## 0. Repository Baseline

The current repository already has useful distributed-platform scaffolding:

- `src/Argus.AppHost`
- `src/Argus.ApiGateway`
- `src/Argus.Web`
- `src/Argus.ServiceDefaults`
- `src/Contracts/Argus.Contracts`
- `src/BuildingBlocks/Argus.BuildingBlocks.EventBus`
- `src/BuildingBlocks/Argus.BuildingBlocks.Workers`
- `src/Services/Argus.AssetService`
- `src/Services/Argus.TaskService`
- `src/Services/Argus.ProgramScopeService`
- `src/Services/Argus.RateLimitService`
- `src/Services/Argus.RealtimeService`
- `src/Services/Argus.ScanOrchestratorService`
- Worker projects:
  - `Argus.Workers.Amass`
  - `Argus.Workers.Subfinder`
  - `Argus.Workers.DnsResolver`
  - `Argus.Workers.HttpProbe`
  - `Argus.Workers.HtmlDomSpider`
  - `Argus.Workers.JsExtractor`
  - `Argus.Workers.WordlistDiscovery`
  - `Argus.Workers.HeadlessSpider`
  - `Argus.Workers.Fingerprint`
  - `Argus.Workers.AssetScoring`

The implementation is incomplete because it still behaves like a task-polling + scan-plan system. The finished product must behave like this:

```text
Asset is created/updated/confirmed/rejected
  -> immutable event is stored
  -> event router evaluates worker subscriptions
  -> deduplicated task runs are created
  -> workers lease tasks
  -> workers emit assets/findings/artifacts/events
  -> emitted events trigger the next subscribed workers
```

---

## 1. Definition of Done for the Entire Project

The project is complete when all of these are true:

- [ ] A target can be created with scope, exclusions, rate limits, safety limits, and scan profile.
- [ ] A root domain or URL can be submitted as an asset.
- [ ] Creating an asset writes an immutable asset event.
- [ ] The event router consumes that event and matches it against enabled worker subscriptions.
- [ ] Matching worker subscriptions create deduplicated task runs.
- [ ] Workers lease tasks atomically under horizontal scale.
- [ ] Workers can run concurrently up to configured limits.
- [ ] Workers can checkpoint long-running work and resume/retry safely.
- [ ] Workers emit new assets, findings, artifacts, logs, metrics, and events.
- [ ] New asset events trigger downstream workers without a central scan orchestrator.
- [ ] ScopeGuard logic prevents out-of-scope processing before task creation and before worker execution.
- [ ] Rate/safety limits are enforced before HTTP/network activity.
- [ ] Asset lineage can be viewed from root target to derived finding.
- [ ] The UI can explore millions of assets using server-side filtering/sorting/cursor pagination.
- [ ] The UI can inspect workers, subscriptions, tasks, events, findings, artifacts, and checkpoints.
- [ ] The UI is dark-mode first, dense, keyboard-friendly, and operator-focused.
- [ ] The current throwaway `Argus.Web/Program.cs` HTML UI has been replaced.
- [ ] `Argus.ScanOrchestratorService` has been removed or renamed/replaced by an event-router/control-plane service.
- [ ] CI builds, tests, and smoke tests pass.
- [ ] Remaining eShop artifacts are removed or renamed.

---

# PHASE 1 — Repository Cleanup and Architectural Pivot

## 1.1 Create implementation branch and baseline verification

- [ ] Create a branch named `feature/event-driven-platform`.
- [ ] Run `dotnet --info` and confirm the SDK version matches `global.json`.
- [ ] Run `dotnet restore`.
- [ ] Run `dotnet build`.
- [ ] Run `dotnet run --project src/Argus.AppHost/Argus.AppHost.csproj`.
- [ ] Record the current service startup state in `docs/implementation-notes/current-baseline.md`.
- [ ] Record any failing projects or SDK incompatibilities before changing code.

## 1.2 Remove inherited eShop artifacts

- [ ] Rename `eShop.slnx` to `Omnopticon.slnx` or `Argus.slnx`.
- [ ] Rename or remove `eShop.Web.slnf`.
- [ ] Remove old eShop images from `img/` unless intentionally reused.
- [ ] Replace `e2e/AddItemTest.spec.ts`, `BrowseItemTest.spec.ts`, `RemoveItemTest.spec.ts`, and old login setup tests with Omnopticon-specific Playwright tests.
- [ ] Review `.github/workflows/pr-validation-maui.yml`; remove if unrelated.
- [ ] Review `.github/workflows/playwright.yml`; update test names, app URL, and expectations.
- [ ] Review `package.json`, `package-lock.json`, and `playwright.config.ts`; remove eShop naming.
- [ ] Update root `README.md` to describe Omnopticon, not inherited eShop content.
- [ ] Add `docs/architecture/event-driven-platform.md`.
- [ ] Add `docs/architecture/ui-control-plane.md`.
- [ ] Add `docs/architecture/worker-subscriptions.md`.

## 1.3 Replace orchestrator terminology

- [ ] Search the repository for `ScanOrchestrator`.
- [ ] Search the repository for `scan plan`, `scan-plans`, `workflow-types`, and `ReconOrchestrator`.
- [ ] Decide whether to delete `src/Services/Argus.ScanOrchestratorService` or replace it in-place with a new event-router service.
- [ ] Preferred: create `src/Services/Argus.EventRouterService`.
- [ ] Preferred: remove `src/Services/Argus.ScanOrchestratorService` from the solution after migration.
- [ ] Update `src/Argus.AppHost/Program.cs` to register `Argus.EventRouterService` instead of `Argus.ScanOrchestratorService`.
- [ ] Update `src/Argus.ApiGateway/Program.cs` to expose `/event-router`, `/worker-types`, `/worker-subscriptions`, and `/event-routes`.
- [ ] Remove `/scan-plans` and `/workflow-types` from the API gateway unless kept as compatibility aliases.
- [ ] Remove `ARGUS_SCAN_ORCHESTRATOR_SERVICE` configuration keys.
- [ ] Add `ARGUS_EVENT_ROUTER_SERVICE` configuration keys.
- [ ] Update `deploy/compose.yaml`.
- [ ] Update `deploy/argus.env.example`.
- [ ] Update `deploy/README.md`.
- [ ] Add migration notes in `docs/migration/scan-orchestrator-to-event-router.md`.

---

# PHASE 2 — Contracts and Shared Domain Model

## 2.1 Expand asset contract model

**Files to modify:**

- `src/Contracts/Argus.Contracts/Assets/AssetContracts.cs`

- [ ] Replace the flat `AssetType` enum dependency in API contracts with registry-backed type identifiers where possible.
- [ ] Keep the enum temporarily only if required for migration.
- [ ] Add `AssetCategory`.
- [ ] Add `AssetSubcategory`.
- [ ] Add `AssetTypeKey`.
- [ ] Add `AssetSubtype`.
- [ ] Add `ScopeStatus` enum:
  - `Unknown`
  - `InScope`
  - `OutOfScope`
  - `Excluded`
  - `NeedsReview`
- [ ] Add `VerificationStatus` enum:
  - `Unverified`
  - `Verified`
  - `Rejected`
  - `Stale`
- [ ] Add `AssetLifecycleStatus` enum:
  - `Discovered`
  - `Confirmed`
  - `Updated`
  - `Rejected`
  - `Archived`
  - `Error`
- [ ] Extend `AssetDto` with:
  - `Category`
  - `Subcategory`
  - `TypeKey`
  - `Subtype`
  - `ScopeStatus`
  - `VerificationStatus`
  - `LifecycleStatus`
  - `Confidence`
  - `RiskScore`
  - `InterestingScore`
  - `HighValue`
  - `SourceWorkerType`
  - `SourceWorkerId`
  - `SourceTaskRunId`
  - `SourceEventId`
  - `CorrelationId`
  - `ParentCount`
  - `ChildCount`
  - `FindingCount`
  - `ArtifactCount`
  - `FirstSeenAt`
  - `LastSeenAt`
  - `LastObservedAt`
  - `Metadata`
  - `Tags`
- [ ] Extend `CreateAssetRequest` with:
  - `Category`
  - `Subcategory`
  - `TypeKey`
  - `Subtype`
  - `Confidence`
  - `SourceWorkerType`
  - `SourceWorkerId`
  - `SourceTaskRunId`
  - `SourceEventId`
  - `CorrelationId`
  - `ParentAssetIds`
  - `Metadata`
  - `Tags`
- [ ] Add `UpdateAssetRequest`.
- [ ] Add `VerifyAssetRequest`.
- [ ] Add `RejectAssetRequest`.
- [ ] Add `MarkHighValueAssetRequest`.
- [ ] Add `AssetBulkActionRequest`.
- [ ] Add `AssetSearchRequest`.
- [ ] Add `AssetSearchResult`.
- [ ] Add `AssetLineageDto`.
- [ ] Add `AssetLineageNodeDto`.
- [ ] Add `AssetLineageEdgeDto`.
- [ ] Add `AssetRelatedGroupDto`.

## 2.2 Add asset type registry contracts

**Files to create:**

- `src/Contracts/Argus.Contracts/AssetTypes/AssetTypeContracts.cs`

- [ ] Create `AssetTypeDefinitionDto`.
- [ ] Include:
  - `AssetTypeId`
  - `Key`
  - `DisplayName`
  - `Category`
  - `Subcategory`
  - `DefaultSubtype`
  - `Description`
  - `ValueKind`
  - `NormalizationStrategy`
  - `NaturalKeyStrategy`
  - `DefaultConfidence`
  - `DefaultInterestingScore`
  - `DefaultRiskScore`
  - `AllowedParentTypes`
  - `AllowedChildTypes`
  - `AllowedWorkerInputs`
  - `IsEnabled`
  - `CreatedAt`
  - `UpdatedAt`
- [ ] Create `CreateAssetTypeDefinitionRequest`.
- [ ] Create `UpdateAssetTypeDefinitionRequest`.
- [ ] Create `AssetTypeSearchRequest`.
- [ ] Create `AssetTypeUsageDto`.
- [ ] Add built-in asset type keys:
  - `program`
  - `scope`
  - `domain`
  - `subdomain`
  - `ip`
  - `cidr`
  - `dns.record`
  - `port`
  - `service`
  - `url`
  - `http.response`
  - `html.page`
  - `javascript.file`
  - `css.file`
  - `json.document`
  - `form`
  - `api.endpoint`
  - `api.spec`
  - `parameter`
  - `technology`
  - `finding.candidate`
  - `secret.candidate`
  - `vulnerability.candidate`
  - `misconfiguration.candidate`
  - `artifact`
  - `observation`
  - `tool.run`

## 2.3 Expand event contracts

**Files to modify:**

- `src/Contracts/Argus.Contracts/Events/IntegrationEventEnvelope.cs`

**Files to create:**

- `src/Contracts/Argus.Contracts/Events/AssetEventContracts.cs`
- `src/Contracts/Argus.Contracts/Events/WorkerEventContracts.cs`
- `src/Contracts/Argus.Contracts/Events/FindingEventContracts.cs`
- `src/Contracts/Argus.Contracts/Events/EventStoreContracts.cs`

- [ ] Keep `IntegrationEventEnvelope<T>` but add:
  - `TenantId` or `WorkspaceId` if multi-tenant support is desired.
  - `TargetId`
  - `ProgramId`
  - `ScopeId`
  - `SubjectType`
  - `SubjectId`
  - `SchemaVersion`
  - `TraceId`
  - `Metadata`.
- [ ] Add immutable event payloads:
  - `AssetDiscovered`
  - `AssetConfirmed`
  - `AssetUpdated`
  - `AssetRejected`
  - `AssetArchived`
  - `AssetRelationshipDiscovered`
  - `FindingCreated`
  - `FindingUpdated`
  - `FindingTriaged`
  - `EvidenceAdded`
  - `ArtifactCreated`
  - `WorkerTypeRegistered`
  - `WorkerSubscriptionCreated`
  - `WorkerSubscriptionUpdated`
  - `WorkerSubscriptionDisabled`
  - `TaskRunRequested`
  - `TaskRunLeased`
  - `TaskRunStarted`
  - `TaskRunProgressed`
  - `TaskRunCheckpointed`
  - `TaskRunCompleted`
  - `TaskRunFailed`
  - `TaskRunRetryScheduled`
  - `TaskRunCancelled`
  - `WorkerHeartbeat`
  - `ScopeChanged`
  - `RateLimitDelayed`
- [ ] Add `EventSearchRequest`.
- [ ] Add `EventSearchResult`.
- [ ] Add `EventDetailDto`.
- [ ] Add `EventChainDto`.
- [ ] Add `EventChainNodeDto`.
- [ ] Add `EventChainEdgeDto`.

## 2.4 Expand worker contracts

**Files to modify:**

- `src/Contracts/Argus.Contracts/Workers/WorkerContracts.cs`

- [ ] Split `WorkerCapabilityDescriptor` into:
  - `WorkerTypeDefinitionDto`
  - `WorkerInstanceDto`
  - `WorkerSubscriptionDto`
  - `WorkerRuntimeMetricsDto`
  - `WorkerConfigurationDto`
- [ ] Add `WorkerCategory`:
  - `Discovery`
  - `Normalization`
  - `Safety`
  - `WebDiscovery`
  - `ClientSideAnalysis`
  - `ApiAnalysis`
  - `CloudDiscovery`
  - `FindingAnalysis`
  - `ArtifactProcessing`
  - `Scoring`
- [ ] Add `WorkerHealthStatus`:
  - `Healthy`
  - `Degraded`
  - `Offline`
  - `Disabled`
  - `Error`
- [ ] Add `CreateWorkerTypeRequest`.
- [ ] Add `UpdateWorkerTypeRequest`.
- [ ] Add `CreateWorkerSubscriptionRequest`.
- [ ] Add `UpdateWorkerSubscriptionRequest`.
- [ ] Add `WorkerSubscriptionMatchRuleDto`.
- [ ] Include match dimensions:
  - `EventTypes`
  - `AssetCategories`
  - `AssetSubcategories`
  - `AssetTypeKeys`
  - `AssetSubtypes`
  - `ScopeStatuses`
  - `VerificationStatuses`
  - `RequiredTags`
  - `ExcludedTags`
  - `MinConfidence`
  - `MaxRiskScore`
  - `OnlyHighValue`
  - `ExpressionJson`.
- [ ] Include subscription behavior:
  - `Enabled`
  - `Priority`
  - `MaxConcurrency`
  - `MaxAttempts`
  - `Timeout`
  - `RetryBackoff`
  - `RateLimitBucket`
  - `ProxyProfile`
  - `CreateTaskWhenAlreadyProcessed`
  - `DedupeWindow`
  - `TaskTypeTemplate`
  - `PayloadTemplateJson`.

## 2.5 Expand task contracts

**Files to modify:**

- `src/Contracts/Argus.Contracts/Tasks/TaskContracts.cs`

- [ ] Rename or alias `ReconTaskDto` to `TaskRunDto`.
- [ ] Keep compatibility only if required by existing workers.
- [ ] Extend task model with:
  - `TaskRunId`
  - `TargetId`
  - `ProgramId`
  - `ScopeId`
  - `InputAssetId`
  - `InputEventId`
  - `WorkerTypeId`
  - `WorkerType`
  - `WorkerSubscriptionId`
  - `WorkerInstanceId`
  - `CorrelationId`
  - `CausationId`
  - `DedupeHash`
  - `Priority`
  - `State`
  - `Attempt`
  - `MaxAttempts`
  - `LeaseOwner`
  - `LeaseExpiresAt`
  - `StartedAt`
  - `CompletedAt`
  - `ProgressPercent`
  - `ProgressMessage`
  - `CheckpointId`
  - `CheckpointJson`
  - `InputPayloadJson`
  - `OutputSummaryJson`
  - `ErrorCode`
  - `ErrorMessage`
  - `RetryAfter`.
- [ ] Add `TaskSearchRequest`.
- [ ] Add `TaskSearchResult`.
- [ ] Add `TaskTimelineDto`.
- [ ] Add `TaskLogEntryDto`.
- [ ] Add `TaskFailureAnalysisDto`.
- [ ] Add `CreateTaskRunRequest`.
- [ ] Add `BulkTaskActionRequest`.

## 2.6 Add finding contracts

**Files to create:**

- `src/Contracts/Argus.Contracts/Findings/FindingContracts.cs`

- [ ] Create `FindingDto`.
- [ ] Add fields:
  - `FindingId`
  - `TargetId`
  - `ProgramId`
  - `Title`
  - `Description`
  - `Severity`
  - `Confidence`
  - `Status`
  - `AffectedAssetId`
  - `SourceAssetId`
  - `SourceWorkerType`
  - `SourceTaskRunId`
  - `SourceEventId`
  - `CorrelationId`
  - `EvidenceArtifactIds`
  - `Tags`
  - `Notes`
  - `FirstSeenAt`
  - `LastUpdatedAt`.
- [ ] Add `FindingSeverity`.
- [ ] Add `FindingStatus`.
- [ ] Add `FindingSearchRequest`.
- [ ] Add `FindingSearchResult`.
- [ ] Add `CreateFindingRequest`.
- [ ] Add `UpdateFindingTriageRequest`.
- [ ] Add `AddFindingNoteRequest`.
- [ ] Add `FindingEvidenceDto`.
- [ ] Add `FindingExportRequest`.

## 2.7 Add artifact contracts

**Files to create:**

- `src/Contracts/Argus.Contracts/Artifacts/ArtifactContracts.cs`

- [ ] Create `ArtifactDto`.
- [ ] Add fields:
  - `ArtifactId`
  - `TargetId`
  - `ProgramId`
  - `AssetId`
  - `TaskRunId`
  - `WorkerType`
  - `ArtifactType`
  - `ContentType`
  - `StorageProvider`
  - `StorageKey`
  - `SizeBytes`
  - `Sha256`
  - `PreviewText`
  - `RedactionStatus`
  - `CreatedAt`.
- [ ] Add `ArtifactType`:
  - `HttpHeaders`
  - `HttpBody`
  - `Screenshot`
  - `Har`
  - `WorkerStdout`
  - `WorkerStderr`
  - `RawDnsAnswer`
  - `JavaScriptBody`
  - `JsonBody`
  - `ToolOutput`.
- [ ] Add `CreateArtifactRequest`.
- [ ] Add `ArtifactSearchRequest`.
- [ ] Add `ArtifactPreviewDto`.

---

# PHASE 3 — Event Bus, Inbox, Outbox, and Event Store

## 3.1 Fix RabbitMQ exchange consistency

**Files to modify:**

- `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/ArgusEventBusOptions.cs`
- `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/RabbitMqIntegrationEventPublisher.cs`
- `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/RabbitMqConsumerService.cs`

- [ ] Open `ArgusEventBusOptions`.
- [ ] Set one canonical exchange name, for example `argus.events`.
- [ ] Ensure the publisher declares and publishes to the canonical exchange.
- [ ] Ensure the consumer binds to the same configured exchange instead of hard-coding a different name.
- [ ] Add a test proving publish and consume use the same exchange value.

## 3.2 Replace current generic RabbitMQ consumer dispatch

**Problem to fix:** the existing consumer deserializes envelopes as `IntegrationEventEnvelope<JsonElement>` and then looks up `IIntegrationEventConsumer<JsonElement>`, which does not naturally dispatch to strongly typed consumers.

- [ ] Create `IIntegrationEventTypeRegistry`.
- [ ] Create `IntegrationEventTypeRegistry`.
- [ ] Register event type name to payload CLR type.
- [ ] Register event payload types from `Argus.Contracts.Events`.
- [ ] Modify `RabbitMqConsumerService<TDbContext>` to:
  - inspect envelope `EventType`,
  - find payload type from registry,
  - deserialize `Payload` to the concrete payload type,
  - build `IntegrationEventEnvelope<TPayload>`,
  - resolve `IIntegrationEventConsumer<TPayload>`,
  - invoke `HandleAsync`.
- [ ] Add fallback handling for unknown event types.
- [ ] Add structured logging for event type, event id, correlation id, and consumer name.
- [ ] Add tests for dispatching:
  - `AssetDiscovered`
  - `AssetUpdated`
  - `TaskRunCompleted`
  - unknown event type.

## 3.3 Consume all declared queues

**Problem to fix:** the current consumer declares queues for many event types but only starts consuming the `AssetDiscovered` queue.

- [ ] Update `RabbitMqConsumerService<TDbContext>` to call `BasicConsumeAsync` for every declared queue.
- [ ] Store consumer tags for graceful shutdown.
- [ ] Add configurable `SubscribedEventTypes` to `ArgusEventBusOptions`.
- [ ] Default to all event types only for services that need all events.
- [ ] Ensure event-router service subscribes to asset/finding/task events.
- [ ] Ensure realtime service can subscribe to all event types or receive events through HTTP ingest.
- [ ] Add integration test with two event types published and consumed.

## 3.4 Add dead-letter and retry behavior

- [ ] Add queue arguments:
  - dead-letter exchange,
  - max delivery count if supported,
  - TTL where useful.
- [ ] On handler failure, increment inbox attempt count.
- [ ] Requeue only retryable failures.
- [ ] Send poison messages to dead-letter queue after max attempts.
- [ ] Add `/event-router/dead-letter` endpoint or admin diagnostics endpoint.
- [ ] Add UI page/filter for dead-lettered events.
- [ ] Add tests for handler failure and dead-letter behavior.

## 3.5 Implement immutable event store

**Preferred project:** create `src/Services/Argus.EventStoreService` or include in `Argus.EventRouterService` if keeping service count smaller.

**Tables to add:**

- `event_store`
- `event_subjects`
- `event_chain_edges`

- [ ] Create `EventStoreDbContext`.
- [ ] Create `EventRecord` with:
  - `EventId`
  - `EventType`
  - `OccurredAt`
  - `SourceService`
  - `SubjectType`
  - `SubjectId`
  - `TargetId`
  - `ProgramId`
  - `ScopeId`
  - `CorrelationId`
  - `CausationId`
  - `SchemaVersion`
  - `PayloadJson`
  - `MetadataJson`.
- [ ] Add indexes:
  - `(EventType, OccurredAt)`
  - `(ProgramId, OccurredAt)`
  - `(TargetId, OccurredAt)`
  - `(SubjectType, SubjectId, OccurredAt)`
  - `(CorrelationId, OccurredAt)`
  - `(CausationId)`
- [ ] Add `IEventStore`.
- [ ] Add `AppendAsync`.
- [ ] Add `FindAsync`.
- [ ] Add `SearchAsync`.
- [ ] Add `GetChainAsync`.
- [ ] Add idempotent append by `EventId`.
- [ ] Make event append part of asset/task/finding workflows.
- [ ] Add endpoint `POST /events/search`.
- [ ] Add endpoint `GET /events/{eventId}`.
- [ ] Add endpoint `GET /events/{eventId}/chain`.
- [ ] Add endpoint `GET /events/by-correlation/{correlationId}`.

## 3.6 Route all integration events into realtime

**Files to modify:**

- `src/Services/Argus.RealtimeService/Program.cs`
- `src/BuildingBlocks/Argus.BuildingBlocks.EventBus/RealtimeIntegrationEventPublisher.cs`

- [ ] Decide whether realtime receives events by HTTP ingest, RabbitMQ subscription, or both.
- [ ] Prefer RabbitMQ subscription for durability.
- [ ] Store recent events in bounded memory for stream UX.
- [ ] Store durable events in event store, not only realtime memory.
- [ ] Add Server-Sent Events or SignalR stream for:
  - all events,
  - target-specific events,
  - worker health,
  - task state changes,
  - asset discoveries,
  - findings.
- [ ] Include correlation id and event id in every realtime message.
- [ ] Add reconnect support with last event id.

---

# PHASE 4 — Event Router Service

## 4.1 Create `Argus.EventRouterService`

**Files/project to create:**

- `src/Services/Argus.EventRouterService/Argus.EventRouterService.csproj`
- `src/Services/Argus.EventRouterService/Program.cs`

- [ ] Copy service defaults pattern from existing services.
- [ ] Reference:
  - `Argus.Contracts`
  - `Argus.ServiceDefaults`
  - `Argus.BuildingBlocks.EventBus`.
- [ ] Add PostgreSQL DbContext.
- [ ] Add RabbitMQ/inbox consumer.
- [ ] Add HTTP clients for:
  - `AssetService`
  - `TaskService`
  - `ProgramScopeService`
  - `RateLimitService` if needed.
- [ ] Register event consumers:
  - `AssetDiscoveredConsumer`
  - `AssetConfirmedConsumer`
  - `AssetUpdatedConsumer`
  - `AssetRejectedConsumer`
  - `FindingCreatedConsumer`
  - `TaskCompletedConsumer`
  - `TaskFailedConsumer`.

## 4.2 Implement worker type registry

**Tables:**

- `worker_types`

- [ ] Create `WorkerTypeRecord`.
- [ ] Add fields:
  - `WorkerTypeId`
  - `WorkerType`
  - `DisplayName`
  - `Category`
  - `Description`
  - `Version`
  - `InputAssetTypesJson`
  - `OutputAssetTypesJson`
  - `RequiredToolsJson`
  - `RequiredServicesJson`
  - `DefaultConfigurationJson`
  - `DefaultConcurrency`
  - `DefaultTimeoutSeconds`
  - `DefaultMaxAttempts`
  - `SupportsCheckpoint`
  - `IsEnabled`
  - `CreatedAt`
  - `UpdatedAt`.
- [ ] Add unique index on `WorkerType`.
- [ ] Seed built-in worker types:
  - `AmassWorker`
  - `SubfinderWorker`
  - `DnsResolverWorker`
  - `HttpProbeWorker`
  - `HtmlDomSpiderWorker`
  - `JsEndpointExtractorWorker`
  - `WordlistDiscoveryWorker`
  - `HeadlessSpiderWorker`
  - `FingerprintWorker`
  - `AssetScoringWorker`
  - `ScopeGuardWorker`
  - `AssetNormalizationWorker`
  - `FindingDeduperWorker`.
- [ ] Add endpoint `GET /worker-types`.
- [ ] Add endpoint `GET /worker-types/{workerType}`.
- [ ] Add endpoint `POST /worker-types`.
- [ ] Add endpoint `PATCH /worker-types/{workerType}`.
- [ ] Add endpoint `POST /worker-types/{workerType}/enable`.
- [ ] Add endpoint `POST /worker-types/{workerType}/disable`.

## 4.3 Implement worker subscription registry

**Tables:**

- `worker_subscriptions`

- [ ] Create `WorkerSubscriptionRecord`.
- [ ] Add fields:
  - `WorkerSubscriptionId`
  - `Name`
  - `WorkerType`
  - `Enabled`
  - `Priority`
  - `EventTypesJson`
  - `AssetTypeKeysJson`
  - `AssetSubtypesJson`
  - `ScopeStatusesJson`
  - `VerificationStatusesJson`
  - `RequiredTagsJson`
  - `ExcludedTagsJson`
  - `MinConfidence`
  - `OnlyHighValue`
  - `ExpressionJson`
  - `TaskTypeTemplate`
  - `PayloadTemplateJson`
  - `MaxAttempts`
  - `TimeoutSeconds`
  - `DedupeWindowSeconds`
  - `CreatedAt`
  - `UpdatedAt`.
- [ ] Add index on `(Enabled, WorkerType)`.
- [ ] Add endpoint `GET /worker-subscriptions`.
- [ ] Add endpoint `GET /worker-subscriptions/{id}`.
- [ ] Add endpoint `POST /worker-subscriptions`.
- [ ] Add endpoint `PATCH /worker-subscriptions/{id}`.
- [ ] Add endpoint `POST /worker-subscriptions/{id}/enable`.
- [ ] Add endpoint `POST /worker-subscriptions/{id}/disable`.
- [ ] Add endpoint `POST /worker-subscriptions/test-match`.
- [ ] Seed default subscriptions:
  - `Domain + AssetDiscovered -> AmassWorker`
  - `Domain + AssetDiscovered -> SubfinderWorker`
  - `Domain/Subdomain + AssetDiscovered -> DnsResolverWorker`
  - `Subdomain/Ip + AssetDiscovered -> HttpProbeWorker`
  - `Url + AssetDiscovered -> HtmlDomSpiderWorker`
  - `Url + AssetConfirmed -> WordlistDiscoveryWorker`
  - `Url + AssetConfirmed -> HeadlessSpiderWorker`
  - `JavaScriptFile + AssetDiscovered -> JsEndpointExtractorWorker`
  - `Url/HttpResponse/HtmlPage + AssetDiscovered -> FingerprintWorker`
  - `Any high-value asset + AssetConfirmed -> AssetScoringWorker`.

## 4.4 Implement subscription matching engine

- [ ] Create `IWorkerSubscriptionMatcher`.
- [ ] Create `WorkerSubscriptionMatcher`.
- [ ] Input: event envelope + asset detail.
- [ ] Output: matching subscriptions in priority order.
- [ ] Match on event type.
- [ ] Match on asset category.
- [ ] Match on asset subcategory.
- [ ] Match on asset type key.
- [ ] Match on asset subtype.
- [ ] Match on scope status.
- [ ] Match on verification status.
- [ ] Match on required/excluded tags.
- [ ] Match on confidence threshold.
- [ ] Match on high-value flag.
- [ ] Add safe expression evaluation only if absolutely needed.
- [ ] Add tests for every match dimension.
- [ ] Add tests proving disabled subscriptions do not match.
- [ ] Add tests proving out-of-scope assets do not trigger active workers unless explicitly configured.

## 4.5 Implement event-to-task routing

- [ ] In `AssetDiscoveredConsumer`, load full asset detail from `AssetService`.
- [ ] Run `ScopeGuard` or confirm stored `ScopeStatus`.
- [ ] Evaluate worker subscriptions.
- [ ] For each match, build task payload:
  - input event id,
  - input asset id,
  - asset value,
  - asset type key,
  - subtype,
  - target/program/scope ids,
  - metadata,
  - source correlation id.
- [ ] Compute dedupe hash:
  - `programId`
  - `scopeId`
  - `inputAssetId`
  - `inputEventId` or event type depending desired behavior
  - `workerType`
  - `workerSubscriptionId`
  - `taskType`
  - normalized payload hash.
- [ ] Call `TaskService` with `CreateTaskRunRequest`.
- [ ] If task already exists, do not create duplicate.
- [ ] Publish `TaskRunRequested`.
- [ ] Write routing decision record.
- [ ] Emit realtime event for route decision.
- [ ] Add endpoint `GET /event-routes`.
- [ ] Add endpoint `GET /events/{eventId}/routing-decisions`.

## 4.6 Implement event-router diagnostics

**Tables:**

- `event_routing_decisions`

- [ ] Record:
  - `RoutingDecisionId`
  - `EventId`
  - `WorkerSubscriptionId`
  - `WorkerType`
  - `Matched`
  - `SkippedReason`
  - `CreatedTaskRunId`
  - `DedupeHash`
  - `CreatedAt`.
- [ ] Skipped reasons:
  - `SubscriptionDisabled`
  - `EventTypeMismatch`
  - `AssetTypeMismatch`
  - `ScopeMismatch`
  - `VerificationMismatch`
  - `ConfidenceBelowThreshold`
  - `RequiredTagMissing`
  - `ExcludedTagPresent`
  - `DuplicateTask`
  - `OutOfScope`
  - `WorkerTypeDisabled`.
- [ ] Add endpoint `GET /event-routing-decisions?eventId=...`.
- [ ] Add endpoint `GET /worker-subscriptions/{id}/recent-decisions`.
- [ ] Surface these decisions in UI event detail.

---

# PHASE 5 — Task Service Completion

## 5.1 Implement real task dedupe

**Files to modify:**

- `src/Services/Argus.TaskService/Program.cs`
- `src/Contracts/Argus.Contracts/Tasks/TaskContracts.cs`

- [ ] Add `DedupeHash` to create task request.
- [ ] Add `DedupeHash` to task DTO.
- [ ] In `TaskMapping.CreateRecord`, set `DedupeHash`.
- [ ] Make `DedupeHash` unique for active/non-terminal tasks.
- [ ] Before creating a task, check by dedupe hash.
- [ ] Return existing task if duplicate.
- [ ] Add endpoint `GET /tasks/by-dedupe/{dedupeHash}`.
- [ ] Add tests:
  - duplicate request returns existing task,
  - different worker creates different task,
  - different payload hash creates different task.

## 5.2 Make EF task leasing atomic

**Problem to fix:** existing EF leasing selects a candidate and then updates it. This can race horizontally.

- [ ] Replace `EfTaskStore.LeaseAsync` with an atomic PostgreSQL lease operation.
- [ ] Use a transaction with `FOR UPDATE SKIP LOCKED` or a conditional update with `RETURNING`.
- [ ] Claim only tasks in:
  - `Requested`
  - `Queued`
  - `RetryPending`.
- [ ] Match by `WorkerCapability`/`WorkerType`.
- [ ] Respect priority.
- [ ] Respect retry `RetryAfter`.
- [ ] Increment attempt in same atomic operation.
- [ ] Set:
  - `State = Leased`
  - `LeaseOwner`
  - `LeaseExpiresAt`
  - `UpdatedAt`.
- [ ] Insert task history in the same transaction.
- [ ] Add concurrency test with many parallel lease calls.
- [ ] Assert only one worker gets a given task.

## 5.3 Add cursor-paginated task search

- [ ] Replace or supplement `GET /tasks` with `POST /tasks/search`.
- [ ] Support filters:
  - target/program/scope,
  - worker type,
  - worker instance,
  - state,
  - input asset type,
  - retry count,
  - failure reason,
  - correlation id,
  - time range.
- [ ] Support cursor pagination.
- [ ] Support sort by:
  - created time,
  - started time,
  - completed time,
  - duration,
  - state,
  - priority.
- [ ] Return total estimate where possible.
- [ ] Add indexes to support common filters.

## 5.4 Implement task logs

- [ ] Create `task_logs` table.
- [ ] Add `TaskLogRecord`.
- [ ] Add endpoint `GET /tasks/{taskRunId}/logs`.
- [ ] Add endpoint `POST /tasks/{taskRunId}/logs`.
- [ ] Add log levels:
  - `Trace`
  - `Debug`
  - `Information`
  - `Warning`
  - `Error`.
- [ ] Modify worker host to submit logs for important lifecycle events.
- [ ] Include timestamp, worker id, task id, correlation id.
- [ ] Add log pagination or streaming for large logs.

## 5.5 Implement task failure analysis fields

- [ ] Extend task failure request with:
  - exception type,
  - exit code,
  - timeout flag,
  - retryable flag,
  - dependency failure,
  - rate-limit state,
  - artifact ids,
  - diagnostic JSON.
- [ ] Save these fields to task record or `task_failures` table.
- [ ] Add endpoint `GET /tasks/{taskRunId}/failure-analysis`.
- [ ] Add generated suggested actions for common failure codes.
- [ ] Add tests for retryable/non-retryable failures.

## 5.6 Implement task history consistently

- [ ] Insert history on create.
- [ ] Insert history on lease.
- [ ] Insert history on start.
- [ ] Insert history on progress.
- [ ] Insert history on checkpoint.
- [ ] Insert history on complete.
- [ ] Insert history on fail.
- [ ] Insert history on cancel.
- [ ] Insert history on retry.
- [ ] Insert history on lease expiry.
- [ ] Add endpoint `GET /tasks/{taskRunId}/timeline`.

---

# PHASE 6 — Worker Host Infrastructure

## 6.1 Add bounded concurrency

**Files to modify:**

- `src/BuildingBlocks/Argus.BuildingBlocks.Workers/ArgusWorkerBackgroundService.cs`

- [ ] Replace single-task loop with bounded concurrency.
- [ ] Use `SemaphoreSlim` or channel-based worker loop.
- [ ] Limit concurrent tasks by:
  - worker instance configured max,
  - worker type config,
  - subscription config,
  - global safety limit.
- [ ] Keep heartbeat reporting actual running task count.
- [ ] Ensure graceful shutdown stops leasing new tasks and waits/cancels running tasks.
- [ ] Add tests for max concurrency.

## 6.2 Add per-task timeout handling

- [ ] Add `CancellationTokenSource` with configured task timeout.
- [ ] On timeout, call task fail endpoint with `WorkerTimeoutException`.
- [ ] Include checkpoint JSON if available.
- [ ] Mark timeout as retryable only if worker/subscription policy allows it.
- [ ] Add tests for timeout behavior.

## 6.3 Add worker checkpoints

- [ ] Expand `WorkerExecutionContext`.
- [ ] Add `SaveCheckpointAsync`.
- [ ] Add `LoadCheckpointAsync`.
- [ ] Create `worker_contexts` or `task_checkpoints` table.
- [ ] Let workers report resumable state.
- [ ] Include checkpoint id in task progress.
- [ ] Add endpoint `GET /workers/contexts`.
- [ ] Add endpoint `GET /workers/contexts/{id}`.
- [ ] Add endpoint `POST /workers/contexts/{id}/resume`.
- [ ] Add tests for checkpoint save/resume.

## 6.4 Enforce scope before publishing produced assets

- [ ] Keep existing scope validation call in worker host.
- [ ] Expand validation to include:
  - domain,
  - wildcard domain,
  - URL,
  - scheme,
  - port,
  - path exclusions,
  - CIDR,
  - explicit exclusions.
- [ ] Tag out-of-scope produced assets if stored.
- [ ] Do not create downstream active tasks for out-of-scope assets.
- [ ] Emit `AssetRejected` or `AssetDiscovered` with `ScopeStatus=OutOfScope` depending desired policy.
- [ ] Add tests for out-of-scope produced assets.

## 6.5 Publish artifacts from workers

- [ ] Extend `WorkerProcessResult` with produced artifacts.
- [ ] Extend `WorkerProducedAsset` to reference artifact ids or inline artifact descriptions.
- [ ] Add artifact publish method to worker host.
- [ ] Upload/store artifact before completing task.
- [ ] Link artifact to:
  - asset,
  - task,
  - worker,
  - event.
- [ ] Add tests for artifact creation.

## 6.6 Upgrade worker registration

- [ ] Worker registration should register:
  - worker instance id,
  - worker type,
  - version,
  - input asset types,
  - output asset types,
  - max concurrency,
  - supports checkpoint,
  - requires HTTP,
  - dependencies,
  - config hash.
- [ ] EventRouter/WorkerType registry should reconcile discovered worker types.
- [ ] Realtime service should show active instances separately from worker types.
- [ ] Add tests for worker heartbeat/offline detection.

---

# PHASE 7 — Asset Service Completion

## 7.1 Expand asset persistence model

**Files to modify:**

- `src/Services/Argus.AssetService/Program.cs`

**Preferred refactor:** split this large file into:

```text
src/Services/Argus.AssetService/
  Program.cs
  Data/AssetDbContext.cs
  Data/AssetRecord.cs
  Data/AssetRelationshipRecord.cs
  Data/AssetObservationRecord.cs
  Stores/IAssetStore.cs
  Stores/EfAssetStore.cs
  Stores/InMemoryAssetStore.cs
  Search/AssetSearchService.cs
  Normalization/AssetNormalizer.cs
  Scoring/AssetScoringService.cs
  Endpoints/AssetEndpoints.cs
```

- [ ] Add fields from expanded `AssetDto`.
- [ ] Add `Category`.
- [ ] Add `Subcategory`.
- [ ] Add `TypeKey`.
- [ ] Add `Subtype`.
- [ ] Add `ScopeStatus`.
- [ ] Add `VerificationStatus`.
- [ ] Add `LifecycleStatus`.
- [ ] Add `HighValue`.
- [ ] Add `SourceWorkerType`.
- [ ] Add `SourceWorkerId`.
- [ ] Add `SourceTaskRunId`.
- [ ] Add `SourceEventId`.
- [ ] Add `CorrelationId`.
- [ ] Add `ParentCount`, `ChildCount`, `FindingCount`, `ArtifactCount` as computed or denormalized fields.
- [ ] Add indexes for:
  - `(ProgramId, TypeKey, NaturalKey)`
  - `(ProgramId, Category)`
  - `(ProgramId, ScopeStatus)`
  - `(ProgramId, VerificationStatus)`
  - `(ProgramId, HighValue)`
  - `(ProgramId, LastSeenAt)`
  - `(ProgramId, InterestingScore)`
  - `(ProgramId, RiskScore)`
  - `CorrelationId`.

## 7.2 Implement asset search endpoint

- [ ] Add `POST /assets/search`.
- [ ] Accept `AssetSearchRequest`.
- [ ] Support filters:
  - target/program,
  - scope,
  - category,
  - subcategory,
  - type key,
  - subtype,
  - lifecycle status,
  - scope status,
  - verification status,
  - confidence min/max,
  - risk min/max,
  - interestingness min/max,
  - high value,
  - source worker,
  - source task,
  - tags,
  - has findings,
  - has artifacts,
  - first seen range,
  - last seen range,
  - text search.
- [ ] Support cursor pagination.
- [ ] Support sort by:
  - last seen,
  - first seen,
  - confidence,
  - interesting score,
  - risk score,
  - value,
  - type.
- [ ] Return `AssetSearchResult`.
- [ ] Add tests for each major filter.
- [ ] Add performance test with large seeded dataset.

## 7.3 Implement asset lineage

- [ ] Add unique relationship index on `(FromAssetId, ToAssetId, EdgeType)`.
- [ ] Add `GET /assets/{assetId}/lineage?direction=parents|children|both&depth=5`.
- [ ] Return nodes and edges.
- [ ] Include source worker/task/event on each edge.
- [ ] Prevent infinite loops.
- [ ] Add graph depth limit.
- [ ] Add tests:
  - parent chain,
  - child chain,
  - cyclic relationship,
  - missing asset.

## 7.4 Implement asset related groups

- [ ] Add `GET /assets/{assetId}/related`.
- [ ] Return groups:
  - parents,
  - children,
  - siblings,
  - same host,
  - same source worker,
  - same tag,
  - related findings,
  - related artifacts.
- [ ] Use compact DTOs.
- [ ] Add tests for each group.

## 7.5 Implement asset actions

- [ ] Add `PATCH /assets/{assetId}`.
- [ ] Add `POST /assets/{assetId}/verify`.
- [ ] Add `POST /assets/{assetId}/reject`.
- [ ] Add `POST /assets/{assetId}/mark-high-value`.
- [ ] Add `POST /assets/{assetId}/tags`.
- [ ] Add `DELETE /assets/{assetId}/tags/{tag}`.
- [ ] Add `POST /assets/bulk`.
- [ ] Publish corresponding events:
  - `AssetConfirmed`
  - `AssetRejected`
  - `AssetUpdated`.
- [ ] Add audit trail for manual operator actions.

## 7.6 Implement asset observations

- [ ] Create `asset_observations` table.
- [ ] Record:
  - `ObservationId`
  - `AssetId`
  - `TaskRunId`
  - `WorkerType`
  - `ObservedAt`
  - `ObservationType`
  - `Status`
  - `Summary`
  - `DataJson`.
- [ ] Add endpoint `GET /assets/{assetId}/observations`.
- [ ] Workers should create observations for HTTP status, DNS resolution, technology detection, screenshot, availability, and changes.
- [ ] Add change detection for repeated observations.

## 7.7 Implement asset type registry service

- [ ] Decide whether asset type registry lives in `AssetService` or separate `AssetTypeService`.
- [ ] Add `asset_type_definitions` table.
- [ ] Seed built-in asset type definitions.
- [ ] Add endpoints:
  - `GET /asset-types`
  - `GET /asset-types/{key}`
  - `POST /asset-types`
  - `PATCH /asset-types/{key}`
  - `POST /asset-types/{key}/enable`
  - `POST /asset-types/{key}/disable`
  - `GET /asset-types/{key}/usage`.
- [ ] Use type registry for normalization and display metadata.
- [ ] Add UI support later.

---

# PHASE 8 — Program/Scope Service Completion

## 8.1 Expand target model

**Current service name:** `Argus.ProgramScopeService`  
**UI concept:** Target

- [ ] Decide whether `Program` maps directly to UI `Target`.
- [ ] If not, add `Target` model:
  - `TargetId`
  - `ProgramId`
  - `Name`
  - `Slug`
  - `Description`
  - `Status`
  - `RootDomains`
  - `AllowedProtocols`
  - `DefaultRateLimitPolicy`
  - `ProxyProfile`
  - `ReconProfile`
  - `CreatedAt`
  - `UpdatedAt`.
- [ ] Add endpoint `GET /targets`.
- [ ] Add endpoint `POST /targets`.
- [ ] Add endpoint `GET /targets/{targetId}`.
- [ ] Add endpoint `PATCH /targets/{targetId}`.
- [ ] Keep `/programs` compatibility if needed.

## 8.2 Improve scope validation

**Files to modify:**

- `src/Services/Argus.ProgramScopeService/Program.cs`

- [ ] Add support for exact domain rules.
- [ ] Add support for wildcard domain rules.
- [ ] Add support for URL rules.
- [ ] Add support for scheme restrictions.
- [ ] Add support for port restrictions.
- [ ] Add support for path exclusions.
- [ ] Add support for CIDR.
- [ ] Add explicit exclusion precedence.
- [ ] Add result reasons:
  - `IncludedByRule`
  - `ExcludedByRule`
  - `NoMatchingInclude`
  - `InvalidAssetType`
  - `UnsupportedProtocol`
  - `PortNotAllowed`
  - `PathExcluded`.
- [ ] Add endpoint `POST /scope-validation/check-batch`.
- [ ] Add tests for every rule type.
- [ ] Add property-based tests for wildcard matching.

## 8.3 Add scope preview and conflict detection

- [ ] Add endpoint `POST /scope-validation/preview`.
- [ ] Input includes include/exclude rules.
- [ ] Output normalized rules and conflicts.
- [ ] Detect include rules completely shadowed by exclusions.
- [ ] Detect duplicate rules.
- [ ] Detect overly broad wildcard rules.
- [ ] Detect invalid CIDR or URL syntax.
- [ ] Use in target creation UI.

## 8.4 Add rate/safety policy association

- [ ] Associate rate limit policies to target/program/scope.
- [ ] Associate safety limits to target/program/scope.
- [ ] Add endpoints:
  - `GET /targets/{id}/rate-limit-policies`
  - `POST /targets/{id}/rate-limit-policies`
  - `GET /targets/{id}/safety-limits`
  - `PATCH /targets/{id}/safety-limits`.

---

# PHASE 9 — Artifact and Finding Services

## 9.1 Create Artifact Service or add artifact module

**Preferred project:**

- `src/Services/Argus.ArtifactService`

- [ ] Create project.
- [ ] Add to solution.
- [ ] Add to AppHost.
- [ ] Add to ApiGateway.
- [ ] Add PostgreSQL reference.
- [ ] Add optional filesystem/S3-compatible storage abstraction.
- [ ] Create `artifacts` table.
- [ ] Create `artifact_blobs` only if storing small blobs in DB.
- [ ] Add endpoints:
  - `POST /artifacts`
  - `GET /artifacts/{artifactId}`
  - `GET /artifacts/{artifactId}/preview`
  - `GET /artifacts/{artifactId}/download`
  - `GET /assets/{assetId}/artifacts`
  - `GET /tasks/{taskRunId}/artifacts`.
- [ ] Add redaction support.
- [ ] Add retention metadata.
- [ ] Publish `ArtifactCreated` and `EvidenceAdded`.

## 9.2 Create Finding Service or add finding module

**Preferred project:**

- `src/Services/Argus.FindingService`

- [ ] Create project.
- [ ] Add to solution.
- [ ] Add to AppHost.
- [ ] Add to ApiGateway.
- [ ] Add PostgreSQL reference.
- [ ] Create `findings` table.
- [ ] Create `finding_notes` table.
- [ ] Create `finding_evidence` table.
- [ ] Create `finding_triage_history` table.
- [ ] Add endpoints:
  - `POST /findings/search`
  - `POST /findings`
  - `GET /findings/{findingId}`
  - `PATCH /findings/{findingId}`
  - `PATCH /findings/{findingId}/triage`
  - `POST /findings/{findingId}/notes`
  - `POST /findings/{findingId}/tags`
  - `POST /findings/export`.
- [ ] Publish:
  - `FindingCreated`
  - `FindingUpdated`
  - `FindingTriaged`.
- [ ] Link findings to source asset, source worker, source task, source event, and evidence artifacts.
- [ ] Add tests for finding triage workflow.

## 9.3 Update AssetScoring and Finding workers

- [ ] Modify `AssetScoringWorker` to create scoring observations rather than only fake finding candidates.
- [ ] Add `FindingCandidateWorker` if needed.
- [ ] Add `FindingDeduperWorker`.
- [ ] Create findings only after dedupe/triage rules.
- [ ] Link finding to source asset and lineage.
- [ ] Add tests for high-value asset finding generation.

---

# PHASE 10 — Worker Implementations

## 10.1 Replace simulated Amass worker

**Project:** `src/Workers/Argus.Workers.Amass`

- [ ] Add configuration for amass executable path or container command.
- [ ] Support disabled state if tool missing.
- [ ] Accept `Domain` input asset.
- [ ] Run only against in-scope domains.
- [ ] Execute amass with safe passive mode by default.
- [ ] Parse stdout/stderr.
- [ ] Emit `Subdomain` assets.
- [ ] Emit `DnsRecord` assets where available.
- [ ] Store raw tool output as artifact.
- [ ] Checkpoint long runs.
- [ ] Respect timeout and cancellation.
- [ ] Add tests using fixture output.

## 10.2 Replace simulated Subfinder worker

**Project:** `src/Workers/Argus.Workers.Subfinder`

- [ ] Add configuration for subfinder executable path.
- [ ] Accept `Domain` input asset.
- [ ] Run passive enumeration by default.
- [ ] Parse discovered subdomains.
- [ ] Emit `Subdomain` assets.
- [ ] Store raw output artifact.
- [ ] Respect scope.
- [ ] Add tests using fixture output.

## 10.3 Complete DnsResolver worker

**Project:** `src/Workers/Argus.Workers.DnsResolver`

- [ ] Replace fixed `203.0.113.10` output.
- [ ] Use .NET DNS APIs or configured resolver library.
- [ ] Resolve A and AAAA records.
- [ ] Resolve CNAME chain.
- [ ] Optionally resolve MX, NS, TXT, CAA.
- [ ] Emit `Ip` assets.
- [ ] Emit `DnsRecord` assets.
- [ ] Store raw DNS observation.
- [ ] Detect wildcard DNS where possible.
- [ ] Add tests with fake resolver abstraction.

## 10.4 Complete HttpProbe worker

**Project:** `src/Workers/Argus.Workers.HttpProbe`

- [ ] Ensure HTTPS first, then HTTP fallback if allowed.
- [ ] Respect allowed protocols from scope/target.
- [ ] Respect rate limits before request.
- [ ] Support configurable timeout.
- [ ] Follow bounded redirects.
- [ ] Emit `Url` asset for reachable URL.
- [ ] Emit `HttpResponse` asset.
- [ ] Emit `HtmlPage`, `JsonDocument`, or other body-type assets where appropriate.
- [ ] Store headers artifact.
- [ ] Store body artifact with size limit.
- [ ] Store redirect chain metadata.
- [ ] Detect title/content type/status.
- [ ] Add tests with local test HTTP server.

## 10.5 Complete HtmlDomSpider worker

**Project:** `src/Workers/Argus.Workers.HtmlDomSpider`

- [ ] Fetch HTML artifact from input asset/task instead of fake links.
- [ ] Parse links from:
  - `a[href]`
  - `script[src]`
  - `link[href]`
  - `form[action]`
  - `iframe[src]`
  - `meta refresh`
  - inline JS URL hints only if safe.
- [ ] Normalize relative URLs.
- [ ] Run ScopeGuard before emitting.
- [ ] Emit `Url` assets.
- [ ] Emit `JavaScriptFile` assets.
- [ ] Emit `CssFile` assets.
- [ ] Emit `Form` assets.
- [ ] Emit `ApiEndpoint` candidates when detected.
- [ ] Add tests with fixture HTML.

## 10.6 Complete JsEndpointExtractor worker

**Project:** `src/Workers/Argus.Workers.JsExtractor`

- [ ] Fetch JS artifact from artifact service or URL with safety limits.
- [ ] Parse endpoint strings.
- [ ] Parse fetch/XHR/axios usage.
- [ ] Parse GraphQL paths.
- [ ] Parse WebSocket URLs.
- [ ] Parse client-side routes.
- [ ] Normalize relative paths against parent URL.
- [ ] Emit `ApiEndpoint`.
- [ ] Emit `Url`.
- [ ] Emit `ClientRoute` if added.
- [ ] Emit redacted `SecretCandidate` only with safe evidence.
- [ ] Store JS parsing observations.
- [ ] Add tests with fixture JS bundles.

## 10.7 Complete WordlistDiscovery worker

**Project:** `src/Workers/Argus.Workers.WordlistDiscovery`

- [ ] Accept `Url` input.
- [ ] Use bounded wordlist.
- [ ] Respect per-host rate limits.
- [ ] Respect target max requests.
- [ ] Probe paths with HEAD/GET policy.
- [ ] Emit discovered `Url` assets.
- [ ] Emit `FindingCandidate` for interesting status/path only if safe.
- [ ] Checkpoint current wordlist position.
- [ ] Add tests with local HTTP server.

## 10.8 Complete HeadlessSpider worker

**Project:** `src/Workers/Argus.Workers.HeadlessSpider`

- [ ] Add Playwright or browser automation dependency.
- [ ] Run only where enabled by safety policy.
- [ ] Navigate to URL.
- [ ] Capture network requests.
- [ ] Capture rendered DOM links.
- [ ] Capture screenshot artifact.
- [ ] Emit `Url`, `ApiEndpoint`, `JavaScriptFile`, `HtmlPage`.
- [ ] Respect max depth and max runtime.
- [ ] Respect robots/safety policy if desired.
- [ ] Add tests using local test page.

## 10.9 Complete Fingerprint worker

**Project:** `src/Workers/Argus.Workers.Fingerprint`

- [ ] Detect server headers.
- [ ] Detect framework markers.
- [ ] Detect CMS markers.
- [ ] Detect JS libraries from script names/content.
- [ ] Detect GraphQL/OpenAPI where present.
- [ ] Emit `Technology` assets.
- [ ] Include confidence and evidence metadata.
- [ ] Add tests with fixture responses.

## 10.10 Add new required workers

- [ ] Add `src/Workers/Argus.Workers.ScopeGuard`.
- [ ] Add `src/Workers/Argus.Workers.AssetNormalization`.
- [ ] Add `src/Workers/Argus.Workers.CertificateTransparency`.
- [ ] Add `src/Workers/Argus.Workers.RobotsSitemap`.
- [ ] Add `src/Workers/Argus.Workers.SecurityHeaders`.
- [ ] Add `src/Workers/Argus.Workers.CookieAnalyzer`.
- [ ] Add `src/Workers/Argus.Workers.FormExtractor`.
- [ ] Add `src/Workers/Argus.Workers.OpenApiDiscovery`.
- [ ] Add `src/Workers/Argus.Workers.GraphQlDiscovery`.
- [ ] Add `src/Workers/Argus.Workers.SourceMapDiscovery`.
- [ ] Add `src/Workers/Argus.Workers.SecretCandidate`.
- [ ] Add `src/Workers/Argus.Workers.Screenshot`.
- [ ] Add `src/Workers/Argus.Workers.FindingDeduper`.
- [ ] Add each worker to solution.
- [ ] Add each worker to AppHost.
- [ ] Register default worker type definitions.
- [ ] Register default subscriptions.
- [ ] Add fixture-based tests per worker.

---

# PHASE 11 — API Gateway

## 11.1 Replace route map

**File:** `src/Argus.ApiGateway/Program.cs`

- [ ] Keep forwarding for:
  - `/programs`
  - `/scopes`
  - `/scope-validation`
  - `/targets`
  - `/assets`
  - `/asset-types`
  - `/tasks`
  - `/workers`
  - `/worker-types`
  - `/worker-subscriptions`
  - `/events`
  - `/event-router`
  - `/event-routes`
  - `/findings`
  - `/artifacts`
  - `/rate-limits`
  - `/settings`.
- [ ] Remove `/scan-plans`.
- [ ] Remove `/workflow-types`.
- [ ] Add root `/` endpoint describing updated API routes.
- [ ] Add health forwarding diagnostics.
- [ ] Add CORS policy for local UI if needed.

## 11.2 Add API gateway typed route tests

- [ ] Add tests ensuring each public route forwards to correct service.
- [ ] Add tests ensuring deleted scan plan routes are not advertised.
- [ ] Add smoke test through gateway:
  - create target,
  - create asset,
  - search assets,
  - view events.

---

# PHASE 12 — Replace the Web UI

## 12.1 Replace `Argus.Web/Program.cs`

**File/project:** `src/Argus.Web`

- [ ] Delete the current single-file static HTML implementation from `Program.cs`.
- [ ] Convert `Argus.Web` into a proper Blazor app or a clean .NET-hosted SPA.
- [ ] Keep:
  - service defaults,
  - health endpoints,
  - configuration,
  - HTTP client setup.
- [ ] Create structure:

```text
src/Argus.Web/
  Program.cs
  App.razor
  Routes.razor
  _Imports.razor
  wwwroot/
    css/
      tokens.css
      app.css
  Layout/
    AppShell.razor
    SidebarNav.razor
    TopBar.razor
    InspectorDrawerHost.razor
  DesignSystem/
  Components/
  Features/
  ApiClients/
  Realtime/
  State/
```

- [ ] Route `/` to `/command-center`.
- [ ] Add not-found page.
- [ ] Add app loading shell.

## 12.2 Implement dark design system

- [ ] Create `wwwroot/css/tokens.css`.
- [ ] Add color tokens:
  - background base,
  - elevated panels,
  - subtle/strong borders,
  - primary/secondary/muted text,
  - success/warning/danger/info,
  - severity colors,
  - scope colors,
  - verification colors.
- [ ] Add typography tokens.
- [ ] Add spacing tokens.
- [ ] Add border-radius tokens.
- [ ] Add density tokens.
- [ ] Add table tokens.
- [ ] Add focus ring styles.
- [ ] Add accessible contrast checks.

## 12.3 Implement app shell

- [ ] Create `Layout/AppShell.razor`.
- [ ] Create `Layout/SidebarNav.razor`.
- [ ] Create `Layout/TopBar.razor`.
- [ ] Create `Layout/PageHeader.razor`.
- [ ] Create `Layout/Breadcrumbs.razor`.
- [ ] Create `Layout/InspectorDrawerHost.razor`.
- [ ] Add left nav:
  - Command Center
  - Targets
  - Assets
  - Asset Types
  - Workers
  - Worker Types
  - Subscriptions
  - Tasks / Runs
  - Contexts / Checkpoints
  - Findings
  - Events
  - Settings.
- [ ] Add top search/command palette trigger.
- [ ] Add current target selector.
- [ ] Add environment/status indicator.
- [ ] Add notifications.
- [ ] Add user/settings menu.
- [ ] Persist sidebar collapsed state.

## 12.4 Implement shared UI components

Create components under `src/Argus.Web/Components`.

- [ ] `VirtualizedDataGrid`.
- [ ] `ColumnChooser`.
- [ ] `SavedViewSelector`.
- [ ] `FilterBar`.
- [ ] `FilterChip`.
- [ ] `BulkActionBar`.
- [ ] `InspectorDrawer`.
- [ ] `Tabs`.
- [ ] `StatusBadge`.
- [ ] `SeverityBadge`.
- [ ] `ScopeStatusBadge`.
- [ ] `VerificationBadge`.
- [ ] `ConfidenceMeter`.
- [ ] `HighValueBadge`.
- [ ] `JsonViewer`.
- [ ] `LogViewer`.
- [ ] `Timeline`.
- [ ] `EventStream`.
- [ ] `ArtifactPreview`.
- [ ] `AssetLineageGraph`.
- [ ] `WorkerDependencyGraph`.
- [ ] `CommandPalette`.
- [ ] `ConfirmDialog`.
- [ ] `ToastStack`.
- [ ] `EmptyState`.
- [ ] `LoadingSkeleton`.
- [ ] `DiagnosticErrorPanel`.
- [ ] `CopyButton`.
- [ ] `ContextMenu`.

## 12.5 Implement API clients

Create clients under `src/Argus.Web/ApiClients`.

- [ ] `TargetsApiClient`.
- [ ] `AssetsApiClient`.
- [ ] `AssetTypesApiClient`.
- [ ] `WorkersApiClient`.
- [ ] `WorkerTypesApiClient`.
- [ ] `WorkerSubscriptionsApiClient`.
- [ ] `TasksApiClient`.
- [ ] `EventsApiClient`.
- [ ] `FindingsApiClient`.
- [ ] `ArtifactsApiClient`.
- [ ] `RateLimitsApiClient`.
- [ ] `SettingsApiClient`.
- [ ] Each client must:
  - use typed request/response DTOs,
  - support cancellation tokens,
  - normalize API errors,
  - avoid hardcoded fake data,
  - call API Gateway unless service-direct calls are intentional.

## 12.6 Implement UI route map

- [ ] `/command-center`
- [ ] `/targets`
- [ ] `/targets/new`
- [ ] `/targets/{targetId}`
- [ ] `/targets/{targetId}/scope`
- [ ] `/assets`
- [ ] `/assets/{assetId}`
- [ ] `/assets/graph`
- [ ] `/asset-types`
- [ ] `/asset-types/{assetTypeKey}`
- [ ] `/workers`
- [ ] `/workers/{workerId}`
- [ ] `/worker-types`
- [ ] `/worker-types/{workerType}`
- [ ] `/worker-subscriptions`
- [ ] `/worker-subscriptions/{subscriptionId}`
- [ ] `/tasks`
- [ ] `/tasks/{taskRunId}`
- [ ] `/contexts`
- [ ] `/contexts/{contextId}`
- [ ] `/findings`
- [ ] `/findings/{findingId}`
- [ ] `/events`
- [ ] `/events/{eventId}`
- [ ] `/settings`
- [ ] `/settings/rate-limits`
- [ ] `/settings/safety-limits`
- [ ] `/settings/proxies`
- [ ] `/settings/retention`
- [ ] `/settings/notifications`.

---

# PHASE 13 — UI Screens

## 13.1 Command Center

- [ ] Build `Features/CommandCenter/CommandCenterPage.razor`.
- [ ] Add health summary cards:
  - System Health
  - Active Targets
  - Queued Tasks
  - Running Tasks
  - Failed Tasks
  - Event Throughput
  - High Findings
  - Worker Fleet.
- [ ] Add active targets/scans table.
- [ ] Add worker health panel.
- [ ] Add queue depth chart.
- [ ] Add event throughput chart.
- [ ] Add recent high-value findings.
- [ ] Add failed tasks needing attention.
- [ ] Add live event stream.
- [ ] Add quick actions:
  - Add target
  - Add seed asset
  - Open Asset Explorer
  - View failed tasks
  - View findings
  - Open subscription matrix.
- [ ] Use realtime updates.
- [ ] Add loading, empty, and error states.

## 13.2 Targets

- [ ] Build `TargetsPage`.
- [ ] Build `TargetCreatePage`.
- [ ] Build `TargetDetailPage`.
- [ ] Build `ScopeEditor`.
- [ ] Target list columns:
  - Name
  - Status
  - Scope summary
  - Asset count
  - Finding count
  - Failed tasks
  - Last activity
  - Actions.
- [ ] Target create form sections:
  - Identity
  - Include scope
  - Exclusions
  - Protocols/ports
  - Rate limits
  - Safety limits
  - Proxy profile
  - Seed assets
  - Review.
- [ ] Target detail panels:
  - Scope summary
  - Event-driven processing summary
  - Asset counts by type
  - Finding summary
  - Active tasks
  - Recent events
  - Enabled subscriptions.
- [ ] Controls:
  - Add seed asset
  - Pause target processing
  - Resume target processing
  - Edit scope
  - Open assets
  - Open events
  - Open subscriptions.

## 13.3 Asset Explorer

- [ ] Build `AssetsPage`.
- [ ] Add dense virtualized grid.
- [ ] Add server-side search.
- [ ] Add advanced query syntax help.
- [ ] Add filters:
  - target,
  - category,
  - subcategory,
  - type,
  - subtype,
  - lifecycle status,
  - scope status,
  - verification status,
  - confidence,
  - source worker,
  - discovery time,
  - tags,
  - high value,
  - has findings,
  - has artifacts.
- [ ] Add saved views:
  - All in-scope assets
  - New unverified assets
  - High-value assets
  - Admin/login surfaces
  - JavaScript with endpoints
  - Assets with findings
  - Out-of-scope discoveries
  - Failed processing inputs.
- [ ] Add columns:
  - Type
  - Value
  - Scope
  - Verified
  - Confidence
  - High Value
  - Source Worker
  - Parents
  - Children
  - Findings
  - Tags
  - First Seen
  - Last Seen
  - Status
  - Actions.
- [ ] Selecting row opens asset detail drawer.
- [ ] Drawer tabs:
  - Summary
  - Lineage
  - Related
  - Worker Runs
  - Events
  - Artifacts
  - Metadata
  - Notes.
- [ ] Actions:
  - Mark verified
  - Reject
  - Tag
  - Mark high value
  - Open URL
  - View evidence
  - Export
  - Send to worker
  - Reprocess.

## 13.4 Asset Types

- [ ] Build `AssetTypesPage`.
- [ ] Build `AssetTypeDetailPage`.
- [ ] Show type registry table.
- [ ] Columns:
  - Key
  - Display name
  - Category
  - Subcategory
  - Enabled
  - Asset count
  - Producing workers
  - Consuming workers.
- [ ] Detail tabs:
  - Definition
  - Normalization
  - Relationships
  - Workers
  - Usage
  - Configuration.
- [ ] Allow enabling/disabling custom asset types.
- [ ] Prevent disabling built-in types if active assets depend on them.

## 13.5 Workers and Worker Types

- [ ] Build `WorkersPage`.
- [ ] Build `WorkerDetailPage`.
- [ ] Build `WorkerTypesPage`.
- [ ] Build `WorkerTypeDetailPage`.
- [ ] Worker catalog columns:
  - Worker type
  - Health
  - Enabled
  - Instances
  - Input types
  - Output types
  - Throughput
  - Error rate
  - Avg duration
  - Last heartbeat.
- [ ] Worker detail tabs:
  - Overview
  - Inputs/Outputs
  - Dependencies
  - Runtime Metrics
  - Configuration
  - Tasks
  - Errors
  - Logs.
- [ ] Actions:
  - Enable/disable worker type
  - Edit configuration
  - View related tasks
  - Open subscriptions
  - Open dependency graph.

## 13.6 Worker Subscription Matrix

- [ ] Build `WorkerSubscriptionsPage`.
- [ ] Show matrix:
  - rows = asset type/event type combinations,
  - columns = worker types,
  - cells = enabled subscription/rule status.
- [ ] Add list/table view for dense editing.
- [ ] Subscription table columns:
  - Name
  - Enabled
  - Event types
  - Asset types
  - Worker type
  - Priority
  - Max attempts
  - Timeout
  - Last matched
  - Recent task count.
- [ ] Build create/edit subscription drawer.
- [ ] Include match-rule builder.
- [ ] Include payload template editor.
- [ ] Include test-match panel.
- [ ] Include recent routing decisions.
- [ ] Add dangerous-action confirmation for disabling high-impact subscriptions.

## 13.7 Tasks / Runs

- [ ] Build `TasksPage`.
- [ ] Build `TaskDetailPage`.
- [ ] Task filters:
  - state,
  - target,
  - worker type,
  - worker instance,
  - input asset type,
  - retry count,
  - failure reason,
  - time range,
  - correlation id.
- [ ] Task columns:
  - Task ID
  - State
  - Worker
  - Target
  - Input asset
  - Progress
  - Attempt
  - Duration
  - Started
  - Completed
  - Error
  - Actions.
- [ ] Detail sections:
  - Summary
  - Progress
  - Timeline
  - Logs
  - Input asset
  - Output assets
  - Artifacts
  - Failure analysis
  - Raw payload.
- [ ] Actions:
  - Retry
  - Cancel
  - Mark failed
  - View worker
  - View input asset
  - View output assets
  - Copy correlation id.

## 13.8 Worker Contexts / Checkpoints

- [ ] Build `WorkerContextsPage`.
- [ ] Build `WorkerContextDetailPage`.
- [ ] Columns:
  - Context ID
  - Worker
  - Target
  - Input asset
  - Status
  - Checkpoint age
  - Resume available
  - Last task
  - Actions.
- [ ] Detail sections:
  - Summary
  - Current checkpoint JSON
  - Progress timeline
  - Related tasks
  - Related assets
  - Resume/retry controls.
- [ ] Add action to resume checkpoint.
- [ ] Add action to discard checkpoint with confirmation.

## 13.9 Findings

- [ ] Build `FindingsPage`.
- [ ] Build `FindingDetailPage`.
- [ ] Add dashboard cards by severity.
- [ ] Add triage status summary.
- [ ] Findings grid columns:
  - Severity
  - Title
  - Confidence
  - Status
  - Affected target
  - Source asset
  - Source worker
  - Evidence
  - First seen
  - Updated
  - Tags
  - Actions.
- [ ] Detail drawer sections:
  - Summary
  - Evidence preview
  - Affected target
  - Source asset
  - Source worker
  - Discovery chain
  - Related assets
  - Notes
  - Triage history.
- [ ] Actions:
  - Confirm
  - False positive
  - Needs review
  - Add note
  - Tag
  - Export
  - Open source asset
  - View discovery chain.

## 13.10 Events

- [ ] Build `EventsPage`.
- [ ] Build `EventDetailPage`.
- [ ] Add live stream mode.
- [ ] Add pause/resume stream.
- [ ] Add filters:
  - event type,
  - target,
  - asset,
  - worker,
  - task,
  - finding,
  - correlation id,
  - causation id,
  - time range.
- [ ] Event columns:
  - Timestamp
  - Type
  - Subject
  - Target
  - Worker
  - Task
  - Correlation ID
  - Summary.
- [ ] Detail drawer:
  - Event metadata
  - Payload JSON
  - Related asset/task/worker/finding
  - Routing decisions
  - Causality chain.
- [ ] Implement event chain timeline.
- [ ] Implement event causality graph.

## 13.11 Settings

- [ ] Build settings shell.
- [ ] Build global scan settings page.
- [ ] Build worker defaults page.
- [ ] Build proxy pools page.
- [ ] Build API keys/tool paths page.
- [ ] Build rate limits page.
- [ ] Build safety limits page.
- [ ] Build notification settings page.
- [ ] Build data retention page.
- [ ] Build export settings page.
- [ ] Add audit for settings changes.
- [ ] Add confirmation for dangerous changes.

---

# PHASE 14 — Realtime UX

## 14.1 Implement UI realtime client

- [ ] Add `RealtimeClient`.
- [ ] Use SignalR or SSE.
- [ ] Subscribe by current route.
- [ ] Support channels:
  - system health,
  - target events,
  - asset discovered,
  - task state changed,
  - worker heartbeat,
  - finding created,
  - event stream,
  - routing decision.
- [ ] Add reconnect with backoff.
- [ ] Add visible connection state.
- [ ] Add last-event-id resume if using SSE.
- [ ] Add tests for reconnect.

## 14.2 Buffer high-volume updates

- [ ] Do not auto-insert asset rows during selection.
- [ ] Show banner: `127 new assets available`.
- [ ] Allow user to refresh current query.
- [ ] Update summary counts separately.
- [ ] Add throttling/debouncing for event stream.
- [ ] Add max retained event count per page.

## 14.3 Add notification center

- [ ] Add notification icon in top bar.
- [ ] Notify on:
  - high finding,
  - worker offline,
  - task retry exhausted,
  - event router errors,
  - target processing paused,
  - rate limit saturation,
  - dead-lettered event.
- [ ] Support mark read/unread.
- [ ] Link notifications to objects.

---

# PHASE 15 — Security, Safety, Audit

## 15.1 Enforce scope at every boundary

- [ ] Validate target seed assets before insertion.
- [ ] Validate asset emissions before storage.
- [ ] Validate asset event before routing.
- [ ] Validate task before leasing.
- [ ] Validate worker before network activity.
- [ ] Store rejected/out-of-scope reason.
- [ ] Add tests proving out-of-scope assets do not trigger active workers.

## 15.2 Enforce rate limits

- [ ] Ensure every HTTP/network worker calls `RateLimitService`.
- [ ] Add worker-specific rate limit bucket.
- [ ] Add target/program/scope/host bucket.
- [ ] Add proxy bucket if using proxies.
- [ ] Add rate-limit delayed events.
- [ ] Show rate-limit delays in UI.

## 15.3 Add safety limits

- [ ] Global max concurrent tasks.
- [ ] Target max concurrent tasks.
- [ ] Worker type max concurrency.
- [ ] Host max requests per minute.
- [ ] Max crawl depth.
- [ ] Max body size.
- [ ] Max JS size.
- [ ] Max artifact size.
- [ ] Max retries.
- [ ] Max redirect depth.
- [ ] Stop conditions.
- [ ] UI editing and validation.

## 15.4 Add audit trail

- [ ] Create `audit_log` table or service.
- [ ] Audit:
  - target create/update/delete,
  - scope changes,
  - worker enable/disable,
  - subscription changes,
  - task retry/cancel,
  - finding triage,
  - settings changes,
  - export actions.
- [ ] Include:
  - user id,
  - action,
  - object type,
  - object id,
  - old value,
  - new value,
  - timestamp,
  - correlation id.
- [ ] Add UI audit tab where useful.

## 15.5 Add authn/authz

- [ ] Decide auth approach:
  - local dev no-auth,
  - OIDC,
  - API key,
  - enterprise auth.
- [ ] Add authorization policies:
  - read assets,
  - manage targets,
  - manage workers,
  - retry/cancel tasks,
  - triage findings,
  - manage settings,
  - export data.
- [ ] Add user menu.
- [ ] Add unauthorized and forbidden pages.

---

# PHASE 16 — Testing

## 16.1 Create test project structure

- [ ] Add `tests/Argus.Contracts.Tests`.
- [ ] Add `tests/Argus.EventBus.Tests`.
- [ ] Add `tests/Argus.AssetService.Tests`.
- [ ] Add `tests/Argus.TaskService.Tests`.
- [ ] Add `tests/Argus.ProgramScopeService.Tests`.
- [ ] Add `tests/Argus.EventRouterService.Tests`.
- [ ] Add `tests/Argus.Workers.Tests`.
- [ ] Add `tests/Argus.Web.Tests` if using bUnit.
- [ ] Add `tests/Argus.E2E.Tests`.

## 16.2 Add backend unit tests

- [ ] Asset normalization tests.
- [ ] Asset natural key/deduplication tests.
- [ ] Asset search filter tests.
- [ ] Asset lineage tests.
- [ ] Scope validation tests.
- [ ] Worker subscription matcher tests.
- [ ] Event router dedupe tests.
- [ ] Task leasing concurrency tests.
- [ ] Task retry/expiry tests.
- [ ] Worker checkpoint tests.
- [ ] Artifact redaction tests.
- [ ] Finding triage tests.

## 16.3 Add integration tests

- [ ] Use testcontainers for PostgreSQL.
- [ ] Use testcontainers for RabbitMQ.
- [ ] Use testcontainers for Redis.
- [ ] Test full event flow:
  - create target,
  - create domain asset,
  - asset event stored,
  - event router creates tasks,
  - worker leases task,
  - worker emits subdomain,
  - downstream task created.
- [ ] Test out-of-scope asset flow.
- [ ] Test duplicate event does not create duplicate task.
- [ ] Test worker failure and retry.
- [ ] Test task lease race under parallel workers.

## 16.4 Add UI component tests

- [ ] Test badges.
- [ ] Test virtualized grid query behavior.
- [ ] Test filter chips.
- [ ] Test saved views.
- [ ] Test inspector drawer.
- [ ] Test JSON viewer.
- [ ] Test log viewer.
- [ ] Test timeline component.
- [ ] Test command palette.

## 16.5 Replace Playwright tests

- [ ] Delete eShop flow tests.
- [ ] Add test: Command Center loads.
- [ ] Add test: create target.
- [ ] Add test: edit scope.
- [ ] Add test: add seed asset.
- [ ] Add test: asset appears in Asset Explorer.
- [ ] Add test: open asset drawer.
- [ ] Add test: view lineage.
- [ ] Add test: worker catalog loads.
- [ ] Add test: subscription matrix loads.
- [ ] Add test: failed task detail loads.
- [ ] Add test: finding triage action.

---

# PHASE 17 — DevOps and Local Developer Experience

## 17.1 Update AppHost

**File:** `src/Argus.AppHost/Program.cs`

- [ ] Add `Argus.EventRouterService`.
- [ ] Add `Argus.ArtifactService` if created.
- [ ] Add `Argus.FindingService` if created.
- [ ] Remove `Argus.ScanOrchestratorService`.
- [ ] Wire service references correctly:
  - EventRouter -> argusdb, rabbitmq, asset, task, program-scope, realtime.
  - Artifact -> argusdb, rabbitmq, realtime.
  - Finding -> argusdb, rabbitmq, asset, artifact, realtime.
- [ ] Add new workers to AppHost.
- [ ] Ensure all workers wait for TaskService, AssetService, RateLimitService, RealtimeService.
- [ ] Ensure services expose health checks.

## 17.2 Update Docker Compose

**File:** `deploy/compose.yaml`

- [ ] Add event-router service.
- [ ] Add artifact service.
- [ ] Add finding service.
- [ ] Remove scan-orchestrator service.
- [ ] Add environment variables.
- [ ] Add volume for artifact storage if local filesystem provider is used.
- [ ] Add health checks.
- [ ] Add dependency ordering.

## 17.3 Update seed data

**File:** `tools/seed-demo-data.sh`

- [ ] Seed demo target.
- [ ] Seed scope rules.
- [ ] Seed asset type definitions.
- [ ] Seed worker type definitions.
- [ ] Seed worker subscriptions.
- [ ] Seed rate/safety limits.
- [ ] Seed root domain asset.
- [ ] Seed realistic events.
- [ ] Seed realistic task runs.
- [ ] Seed findings and artifacts for UI demo.
- [ ] Remove old scan-plan seeding.

## 17.4 Update CI

- [ ] CI runs `dotnet restore`.
- [ ] CI runs `dotnet build`.
- [ ] CI runs unit tests.
- [ ] CI runs integration tests conditionally if Docker available.
- [ ] CI runs Playwright tests.
- [ ] CI runs markdownlint.
- [ ] CI runs secret scan if configured.
- [ ] CI publishes test results.

---

# PHASE 18 — Documentation

## 18.1 Architecture docs

- [ ] `docs/architecture/event-driven-platform.md`.
- [ ] `docs/architecture/event-router.md`.
- [ ] `docs/architecture/asset-taxonomy.md`.
- [ ] `docs/architecture/worker-subscriptions.md`.
- [ ] `docs/architecture/task-leasing.md`.
- [ ] `docs/architecture/scope-and-safety.md`.
- [ ] `docs/architecture/artifacts-and-findings.md`.
- [ ] `docs/architecture/ui-control-plane.md`.

## 18.2 Operator docs

- [ ] `docs/operators/add-target.md`.
- [ ] `docs/operators/configure-scope.md`.
- [ ] `docs/operators/create-worker-subscription.md`.
- [ ] `docs/operators/monitor-scan.md`.
- [ ] `docs/operators/investigate-asset.md`.
- [ ] `docs/operators/triage-finding.md`.
- [ ] `docs/operators/debug-task.md`.
- [ ] `docs/operators/manage-rate-limits.md`.

## 18.3 Developer docs

- [ ] `docs/developers/add-asset-type.md`.
- [ ] `docs/developers/add-worker.md`.
- [ ] `docs/developers/add-event-type.md`.
- [ ] `docs/developers/add-ui-screen.md`.
- [ ] `docs/developers/testing.md`.
- [ ] `docs/developers/local-development.md`.

---

# Recommended Ticket Order

## Milestone 1 — Pivot and infrastructure

- [ ] 1.1 Baseline verification.
- [ ] 1.2 eShop cleanup.
- [ ] 2.1 Expand asset contracts.
- [ ] 2.3 Expand event contracts.
- [ ] 2.4 Expand worker contracts.
- [ ] 3.1 Fix RabbitMQ exchange consistency.
- [ ] 3.2 Typed event dispatch.
- [ ] 3.3 Consume all queues.
- [ ] 5.1 Real task dedupe.
- [ ] 5.2 Atomic task leasing.

## Milestone 2 — Event-router MVP

- [ ] 4.1 Create EventRouterService.
- [ ] 4.2 Worker type registry.
- [ ] 4.3 Worker subscription registry.
- [ ] 4.4 Subscription matcher.
- [ ] 4.5 Event-to-task routing.
- [ ] 7.2 Asset search endpoint.
- [ ] 7.3 Asset lineage.
- [ ] 8.2 Scope validation improvements.

## Milestone 3 — Worker runtime MVP

- [ ] 6.1 Bounded concurrency.
- [ ] 6.2 Task timeout handling.
- [ ] 6.3 Worker checkpoints.
- [ ] 6.4 Scope enforcement.
- [ ] 10.3 DnsResolver real implementation.
- [ ] 10.4 HttpProbe completion.
- [ ] 10.5 HtmlDomSpider completion.
- [ ] 10.6 JsEndpointExtractor completion.

## Milestone 4 — New UI foundation

- [ ] 12.1 Replace Argus.Web.
- [ ] 12.2 Dark design system.
- [ ] 12.3 App shell.
- [ ] 12.4 Shared UI components.
- [ ] 12.5 API clients.
- [ ] 13.3 Asset Explorer.
- [ ] 13.6 Worker Subscription Matrix.
- [ ] 13.7 Tasks / Runs.
- [ ] 13.10 Events.

## Milestone 5 — Findings, artifacts, and operator workflows

- [ ] 9.1 Artifact service.
- [ ] 9.2 Finding service.
- [ ] 13.9 Findings UI.
- [ ] 13.8 Worker contexts UI.
- [ ] 13.2 Targets UI.
- [ ] 13.1 Command Center.
- [ ] 14.1 Realtime client.
- [ ] 14.2 Realtime buffering.

## Milestone 6 — Production readiness

- [ ] 15.1 Scope enforcement everywhere.
- [ ] 15.2 Rate limits everywhere.
- [ ] 15.3 Safety limits.
- [ ] 15.4 Audit trail.
- [ ] 15.5 Authn/authz.
- [ ] 16.3 Full integration tests.
- [ ] 16.5 Playwright replacement.
- [ ] 17.2 Docker Compose update.
- [ ] 18.x Documentation.

---

# Final Completion Checklist

The implementation is done only when:

- [ ] `dotnet build` passes.
- [ ] All unit tests pass.
- [ ] All integration tests pass.
- [ ] Playwright tests pass.
- [ ] Aspire local startup works.
- [ ] Docker Compose startup works.
- [ ] A seed domain triggers event-driven worker task creation.
- [ ] Workers emit downstream assets.
- [ ] Downstream assets trigger downstream workers.
- [ ] Asset Explorer shows discovered assets with lineage.
- [ ] Worker Subscription Matrix controls event routing.
- [ ] Tasks page can debug failed work.
- [ ] Findings page supports triage.
- [ ] Events page shows raw events and causality.
- [ ] Scope/rate/safety enforcement is proven by tests.
- [ ] No central scan orchestrator is required for discovery progression.
- [ ] Existing garbage web UI has been replaced completely.
