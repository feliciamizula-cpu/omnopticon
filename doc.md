Absolutely — here’s a cleaner, more structured version of the same implementation brief, with clearer sections and a more usable checklist. This updates the earlier orchestrator-based product direction into a purely event-driven worker system. 

---

# Event-Driven Bug Bounty Recon Platform

## Blazor / .NET Core Web App Implementation Specification

## 1. Core Product Direction

This platform should **not** use a central recon orchestrator.

Instead, the system is fully event-driven.

Workers react to asset lifecycle events such as:

* Asset discovered
* Asset confirmed
* Asset updated
* Asset rejected
* Finding created
* Evidence added

A user should be able to configure:

* Which worker types exist
* Which worker types are enabled
* Which asset types each worker processes
* Which event types trigger each worker
* Whether a worker responds to discovery events, confirmation events, or both
* Worker retry behavior
* Worker concurrency
* Worker checkpoints and resumable progress

The system should behave like this:

```text
Asset Event Created
        ↓
Event Router Finds Matching Worker Subscriptions
        ↓
Task Runs Are Queued
        ↓
Worker Instance Claims Task
        ↓
Worker Processes Asset
        ↓
Worker Emits New Assets / Findings / Artifacts / Events
        ↓
More Workers React Automatically
```

There should be **no fixed workflow**, **no central scan controller**, and **no orchestrator deciding what happens next**.

---

# 2. Top-Level Navigation

The Blazor app should use a persistent shell with:

* Left sidebar
* Top global search / command palette
* Current target selector
* Environment status indicator
* Notifications
* User/settings menu
* Optional right-side inspector drawer

## Main Navigation

| Section              | Purpose                                 |
| -------------------- | --------------------------------------- |
| Command Center       | Global system overview                  |
| Targets              | Target and scope management             |
| Assets               | High-performance asset explorer         |
| Asset Types          | Configure custom asset types            |
| Workers              | Runtime worker instances                |
| Worker Types         | Worker catalog and configuration        |
| Worker Subscriptions | Configure event-driven worker behavior  |
| Tasks / Runs         | Track worker task execution             |
| Worker Contexts      | View checkpoints and resumable progress |
| Findings             | Triage security-relevant results        |
| Events               | Raw event stream and event chains       |
| Settings             | Global configuration                    |

Remove all references to:

* Orchestrators
* ReconOrchestrator
* Scan phases
* Central workflow controllers

---

# 3. Core Domain Objects

## Target

A target defines the scan boundary and safety context.

Required fields:

* Target ID
* Name
* Root domains
* Included domains
* Excluded domains
* Allowed protocols
* Scope rules
* Rate limits
* Proxy profile
* Safety limits
* Status
* Created / updated timestamps

Targets should not contain orchestration logic.

---

## Asset

An asset is anything discovered, processed, confirmed, enriched, or derived.

Examples:

* Domain
* Subdomain
* Host
* URL
* Page
* JavaScript file
* API endpoint
* Parameter
* Form
* Technology
* Screenshot
* HTTP response
* Artifact
* Finding
* Secret candidate

Required fields:

* Asset ID
* Target ID
* Asset type ID
* Category
* Subcategory
* Type
* Subtype
* Canonical value
* Display value
* State
* Scope status
* Verification status
* Confidence
* High-value flag
* Tags
* Parent asset IDs
* Child asset IDs
* Producing worker type
* Producing worker instance
* Producing task run
* Source event
* Discovery chain ID
* Metadata JSON
* First seen
* Last seen
* Created / updated timestamps

### Asset States

| State            | Meaning                           |
| ---------------- | --------------------------------- |
| Discovered       | Newly found but not yet processed |
| Normalized       | Canonicalized and deduplicated    |
| Confirmed        | Verified as real or reachable     |
| Rejected         | Rejected by user or worker        |
| Archived         | No longer active                  |
| FailedValidation | Invalid or unusable               |
| Duplicate        | Duplicate of existing asset       |
| OutOfScope       | Blocked by scope rules            |

---

## Asset Type

Asset types must be configurable by users.

Examples:

* Domain
* Subdomain
* URL
* Page
* JavaScriptFile
* Form
* ApiEndpoint
* Parameter
* HttpResponse
* Screenshot
* Technology
* Finding
* Artifact
* SecretCandidate

Required fields:

* Asset type ID
* Name
* Category
* Subcategory
* Description
* Schema JSON
* Canonicalization rules JSON
* Deduplication strategy
* Default confidence
* Allowed states
* Allowed events
* System/custom flag
* Enabled flag
* Created / updated timestamps

The UI must allow users to:

* View asset types
* Create asset types
* Edit custom asset types
* Import asset type definitions
* Export asset type definitions
* Disable custom asset types
* View consuming workers
* View producing workers

---

## Asset Event

Events are immutable records of asset or worker activity.

Required event types:

* AssetDiscovered
* AssetNormalized
* AssetConfirmed
* AssetRejected
* AssetUpdated
* AssetTagged
* AssetHighValueMarked
* AssetOutOfScope
* AssetDuplicateDetected
* AssetEvidenceAdded
* FindingCreated
* FindingUpdated
* WorkerTaskQueued
* WorkerTaskStarted
* WorkerTaskCheckpointed
* WorkerTaskCompleted
* WorkerTaskFailed
* WorkerTaskRetried
* WorkerTaskAbandoned
* WorkerContextClaimed
* WorkerContextReleased
* WorkerContextResumed

Required fields:

* Event ID
* Event type
* Target ID
* Asset ID
* Asset type ID
* Worker type ID
* Worker instance ID
* Task run ID
* Correlation ID
* Causation ID
* Discovery chain ID
* Payload JSON
* Severity
* Created timestamp

---

## Worker Type

A worker type defines a reusable processing capability.

Examples:

* SubfinderWorker
* AmassWorker
* ScopeGuardWorker
* NormalizationWorker
* HttpProbeWorker
* DomSpiderWorker
* JsLinkExtractorWorker
* PathGuessingWorker
* TechnologyDetectionWorker
* HighValueDetectorWorker
* SecretCandidateWorker
* ScreenshotWorker

Required fields:

* Worker type ID
* Name
* Display name
* Description
* Category
* Version
* Package source
* System/custom flag
* Enabled globally flag
* Input asset type IDs
* Output asset type IDs
* Subscribed event types
* Default config JSON
* Current config JSON
* Requirements JSON
* Capabilities JSON
* Concurrency settings JSON
* Retry policy JSON
* Timeout settings JSON
* Safety settings JSON
* Created / updated timestamps

The UI must allow users to:

* View all worker types
* Group workers by category
* Enable or disable worker types
* Configure processed asset types
* Configure trigger events
* Configure output asset types
* Import worker types
* Create worker types manually
* Edit worker settings
* View active worker instances
* View recent task runs
* View error rates
* View queue depth
* View resumable contexts

---

## Worker Subscription

A worker subscription controls when a worker receives work.

Required fields:

* Worker subscription ID
* Worker type ID
* Target ID, nullable
* Asset type ID
* Event type
* Enabled flag
* Priority
* Filter expression
* Max concurrency
* Rate limit policy
* Created / updated timestamps

Rules:

* Subscriptions may be global or target-specific.
* Target-specific settings override global settings.
* Disabled worker types receive no new work.
* Disabled subscriptions receive no new work.
* A worker may subscribe to multiple asset types.
* A worker may subscribe to multiple event types.
* Discovery and confirmation events must be configurable separately.

Example filters:

```text
scopeStatus == InScope
verificationStatus == Verified
confidence >= 80
tags contains "api"
value contains "/admin"
```

---

## Worker Instance

A worker instance is a running process for a worker type.

Required fields:

* Worker instance ID
* Worker type ID
* Hostname
* Process ID
* Status
* Started timestamp
* Last heartbeat timestamp
* Current task run ID
* Active lease count
* Version
* Metadata JSON

Statuses:

* Starting
* Healthy
* Busy
* Degraded
* Draining
* Stopped
* Failed
* Lost

---

## Task Run

A task run is a single worker execution attempt against an asset event.

Required fields:

* Task run ID
* Worker type ID
* Worker instance ID
* Target ID
* Asset ID
* Asset type ID
* Trigger event ID
* State
* Progress percent
* Current step
* Attempt number
* Max attempts
* Lease owner worker instance ID
* Lease expiration
* Started timestamp
* Completed timestamp
* Duration
* Error type
* Error message
* Retryable flag
* Input snapshot JSON
* Output summary JSON
* Last checkpoint ID
* Correlation ID
* Created / updated timestamps

States:

* Queued
* Leased
* Running
* Checkpointed
* Completed
* Failed
* RetryScheduled
* Abandoned
* Cancelled
* DeadLettered

---

## Worker Context / Checkpoint

Worker progress must be persisted.

If a worker dies halfway through processing, another worker instance of the same worker type must be able to resume from the last checkpoint.

Required fields:

* Worker context ID
* Worker type ID
* Task run ID
* Asset ID
* Target ID
* Context state
* Progress percent
* Current step
* Cursor JSON
* Partial results JSON
* Lock owner worker instance ID
* Lock expiration
* Resume token
* Checkpoint version
* Created / updated timestamps

Context states:

* Open
* Claimed
* Checkpointed
* Completed
* Failed
* Abandoned
* Expired

Rules:

* Long-running workers must checkpoint progress.
* Checkpoints must be idempotent.
* Expired leases must be reclaimable.
* Only another worker instance of the same worker type can resume context.
* Resumed workers must not duplicate emitted assets.
* Emitted assets must be deduplicated by asset type and canonical value.

---

# 4. Required Screens

## Command Center

The Command Center should show global system health.

Required sections:

* System health cards
* Active targets
* Worker health
* Queue depth
* Event throughput
* Recent high-value findings
* Failed tasks
* Dead-lettered tasks
* Abandoned worker contexts
* Live event stream

Quick actions:

* Add target
* Open asset explorer
* Open worker subscriptions
* View failed tasks
* View abandoned contexts
* View findings
* Import worker type
* Create asset type

---

## Targets

Required screens:

* Target list
* Add target
* Edit target
* Target detail

Target detail must show:

* Scope summary
* Included domains
* Excluded domains
* Allowed protocols
* Rate limits
* Proxy profile
* Safety limits
* Asset counts
* Finding summary
* Worker subscriptions for this target
* Active tasks
* Failed tasks
* Abandoned contexts
* Recent events

Target actions:

* Edit scope
* Open assets
* Open findings
* Enable event processing
* Disable event processing
* Pause new work creation
* Resume new work creation
* Drain active tasks
* Cancel queued tasks
* Export target data

Do not include scan start, pause, resume, or orchestrator controls.

---

## Assets

The Asset Explorer is one of the most important screens.

Required features:

* Dense virtualized grid
* Server-side search
* Server-side filtering
* Server-side sorting
* Server-side pagination
* Saved filter views
* Advanced query syntax
* Column chooser
* Bulk actions
* Split-pane detail panel
* Asset lineage
* Parent/child assets
* Producing worker
* Trigger event
* Worker task history
* Related events
* Artifacts
* Raw metadata JSON
* Tags and notes
* Scope badges
* Verification badges
* High-value badges

Required filters:

* Target
* Asset type
* Category
* Subcategory
* State
* Scope status
* Verification status
* Confidence
* Producing worker type
* Trigger event type
* Discovery time
* Tags
* Has findings
* Has artifacts

Asset actions:

* Mark verified
* Reject
* Tag
* Mark high value
* Open URL
* View evidence
* Export
* Send to worker manually
* Re-emit discovery event
* Re-emit confirmation event
* Copy asset ID
* Copy canonical value

---

## Asset Types

Required features:

* Asset type list
* Create asset type
* Edit asset type
* Import asset type
* Export asset type
* Schema viewer/editor
* Canonicalization rules editor
* Deduplication strategy editor
* Consuming worker list
* Producing worker list
* Example assets

Asset type list columns:

| Column            |
| ----------------- |
| Name              |
| Category          |
| Subcategory       |
| Enabled           |
| System/custom     |
| Consuming workers |
| Producing workers |
| Asset count       |
| Last seen         |
| Actions           |

---

## Workers

The Workers screen focuses on runtime worker instances.

Required features:

* Worker instance list
* Worker health dashboard
* Worker instance detail
* Runtime status
* Active task
* Heartbeat
* Hostname
* Version
* Lease count
* Recent logs
* Recent failures

Worker instance actions:

* Drain
* Stop accepting leases
* View active task
* View logs
* View worker type
* Release stale leases
* Mark lost

---

## Worker Types

The Worker Types screen manages worker capabilities and configuration.

Required features:

* Worker type catalog
* Enable/disable worker type
* Create worker type
* Import worker type
* Edit worker type
* Configure subscriptions
* Configure processed asset types
* Configure event triggers
* Configure output asset types
* Configure concurrency
* Configure retry policy
* Configure timeouts
* Configure rate limits
* Configure safety limits
* View task history
* View failed tasks
* View abandoned contexts
* View package/source metadata

Worker type detail tabs:

* Overview
* Subscriptions
* Input / Output Asset Types
* Event Triggers
* Runtime Metrics
* Configuration
* Requirements
* Instances
* Tasks
* Errors
* Checkpoints
* Package Definition

---

## Worker Subscription Matrix

This is the primary configuration surface for event-driven behavior.

Rows:

* Worker types

Columns:

* Asset types grouped by category

Cell states:

* Disabled
* On discovery
* On confirmation
* On update
* On rejection
* Custom event
* Multiple events

Each cell should show:

* Enabled state
* Event subscriptions
* Target override indicator
* Filter expression indicator
* Queue depth
* Recent failures

Clicking a cell opens a subscription drawer.

Subscription drawer fields:

* Worker type
* Asset type
* Target override
* Enabled flag
* Event types
* Priority
* Filter expression
* Max concurrency
* Rate limit policy
* Retry override
* Safety rules
* Test match
* Save
* Disable

---

## Tasks / Runs

Required features:

* Task list
* Active tasks
* Queued tasks
* Failed tasks
* Dead-lettered tasks
* Retry-scheduled tasks
* Abandoned tasks
* Task detail page

Task detail must show:

* Task state
* Worker type
* Worker instance
* Target
* Input asset
* Trigger event
* Progress
* Current step
* Started/completed timestamps
* Duration
* Retry count
* Lease owner
* Lease expiration
* Logs
* Output assets
* Artifacts
* Failure reason
* Timeline
* Checkpoints
* Resume history

Actions:

* Retry
* Cancel
* Mark failed
* Dead-letter
* Release lease
* Resume from checkpoint
* View worker type
* View worker instance
* View input asset
* View output assets
* View trigger event

---

## Worker Contexts / Checkpoints

Required features:

* Context list
* Abandoned context view
* Context detail drawer
* Resume history
* Checkpoint timeline

Context list columns:

| Column          |
| --------------- |
| State           |
| Worker type     |
| Target          |
| Asset           |
| Task run        |
| Progress        |
| Current step    |
| Lock owner      |
| Lock expiration |
| Updated         |
| Actions         |

Actions:

* View context
* Release lock
* Mark abandoned
* Resume
* Dead-letter
* Copy resume token

---

## Findings

Required features:

* Findings table
* Severity filter
* Confidence filter
* Triage status filter
* Evidence preview
* Source asset
* Source worker type
* Trigger event
* Affected target
* Discovery chain
* Notes
* Tags
* Export actions

Triage actions:

* Mark confirmed
* Mark false positive
* Mark needs review
* Add note
* Tag
* Export
* Open source asset
* View discovery chain
* View producing task
* View source event

---

## Events

Required features:

* Raw event stream
* Event type filters
* Asset lifecycle events
* Worker lifecycle events
* Task events
* Error events
* Correlation ID search
* Causation ID search
* Event detail drawer
* Event chain view

Event table columns:

| Column         |
| -------------- |
| Timestamp      |
| Event type     |
| Target         |
| Asset          |
| Asset type     |
| Worker type    |
| Task run       |
| Severity       |
| Correlation ID |
| Message        |

---

# 5. Design System

## Visual Direction

The UI should feel like:

* Security operations console
* Cloud infrastructure control panel
* Observability dashboard
* Data-heavy developer tool
* Recon workflow manager

Avoid:

* Neon hacker visuals
* Generic SaaS dashboards
* Toy data layouts
* Low-density card-only designs

Use:

* Dark background
* Muted borders
* Dense tables
* Split panes
* Drawer detail panels
* Status badges
* Monospace IDs and logs
* Meaningful color only
* Compact spacing

## Color Palette

| Token                | Color     |
| -------------------- | --------- |
| Background primary   | `#080B12` |
| Background secondary | `#0D111A` |
| Surface              | `#111827` |
| Surface elevated     | `#151C2C` |
| Surface hover        | `#1B2436` |
| Border muted         | `#273244` |
| Border strong        | `#3A465C` |
| Text primary         | `#E5E7EB` |
| Text secondary       | `#A9B4C4` |
| Text muted           | `#6B7280` |
| Accent blue          | `#38BDF8` |
| Accent purple        | `#A78BFA` |
| Accent green         | `#34D399` |
| Accent amber         | `#FBBF24` |
| Accent red           | `#F87171` |
| Accent orange        | `#FB923C` |

## Status Colors

| State    | Color      |
| -------- | ---------- |
| Healthy  | Green      |
| Running  | Blue       |
| Queued   | Purple     |
| Warning  | Amber      |
| Failed   | Red        |
| Paused   | Gray       |
| Disabled | Slate      |
| Critical | Red        |
| High     | Orange-red |
| Medium   | Amber      |
| Low      | Blue       |
| Info     | Gray       |

## Typography

| Use           | Style                               |
| ------------- | ----------------------------------- |
| UI font       | Inter, system-ui, sans-serif        |
| Monospace     | JetBrains Mono, Consolas, monospace |
| Page title    | 22px / 600                          |
| Section title | 15px / 600                          |
| Body          | 13px / 400                          |
| Dense table   | 12px / 400                          |
| Metadata      | 11px / 500                          |
| Logs/code     | 12px monospace                      |

---

# 6. Blazor Implementation Structure

```text
/Components
  /Shell
  /Common
  /DataGrid
  /Drawers
  /Forms
  /Charts
  /Logs
  /Json
  /Domain
    /Targets
    /Assets
    /AssetTypes
    /Workers
    /WorkerTypes
    /Tasks
    /Events
    /Findings
    /Settings

/Pages
  CommandCenter.razor
  Targets.razor
  TargetDetail.razor
  Assets.razor
  AssetTypes.razor
  AssetTypeDetail.razor
  Workers.razor
  WorkerInstanceDetail.razor
  WorkerTypes.razor
  WorkerTypeDetail.razor
  WorkerSubscriptionMatrix.razor
  Tasks.razor
  TaskDetail.razor
  WorkerContexts.razor
  Findings.razor
  FindingDetail.razor
  Events.razor
  Settings.razor

/Services
  TargetApiClient.cs
  AssetApiClient.cs
  AssetTypeApiClient.cs
  WorkerApiClient.cs
  WorkerTypeApiClient.cs
  WorkerSubscriptionApiClient.cs
  TaskRunApiClient.cs
  WorkerContextApiClient.cs
  EventApiClient.cs
  FindingApiClient.cs
  RealtimeEventClient.cs
  CommandPaletteService.cs
  SavedViewService.cs
  ToastService.cs
  RightInspectorService.cs

/Models
  TargetDto.cs
  AssetDto.cs
  AssetTypeDto.cs
  AssetEventDto.cs
  WorkerTypeDto.cs
  WorkerSubscriptionDto.cs
  WorkerInstanceDto.cs
  TaskRunDto.cs
  WorkerContextDto.cs
  FindingDto.cs
```

---

# 7. Required Reusable Components

## Shell Components

* AppShell
* SideNav
* TopBar
* Breadcrumbs
* CommandPalette
* EnvironmentIndicator
* NotificationBell
* UserMenu
* RightInspectorHost
* ToastHost

## Data Components

* VirtualizedDataGrid
* ServerFilterBar
* SavedViewSelector
* ColumnChooser
* BulkActionBar
* DensePagination
* ResizableSplitPane

## Domain Components

* AssetExplorerGrid
* AssetDetailDrawer
* AssetLineageGraph
* AssetTypeEditor
* WorkerTypeCatalog
* WorkerTypeEditor
* WorkerSubscriptionMatrix
* WorkerHealthPanel
* WorkerContextPanel
* TaskTimeline
* FindingTriageDrawer
* EventStream
* EventChainViewer

## Utility Components

* StatusBadge
* SeverityBadge
* ConfidenceBadge
* ScopeBadge
* VerificationBadge
* CopyButton
* JsonViewer
* LogViewer
* Timeline
* ArtifactPreview
* EmptyState
* LoadingSkeleton
* ErrorDiagnosticPanel
* ConfirmDialog

---

# 8. Backend API Requirements

## Assets

```text
GET    /api/assets
GET    /api/assets/{assetId}
POST   /api/assets
PATCH  /api/assets/{assetId}
POST   /api/assets/{assetId}/verify
POST   /api/assets/{assetId}/reject
POST   /api/assets/{assetId}/tags
POST   /api/assets/{assetId}/emit-discovery-event
POST   /api/assets/{assetId}/emit-confirmation-event
GET    /api/assets/{assetId}/lineage
GET    /api/assets/{assetId}/events
GET    /api/assets/{assetId}/tasks
GET    /api/assets/{assetId}/artifacts
```

## Asset Types

```text
GET    /api/asset-types
GET    /api/asset-types/{assetTypeId}
POST   /api/asset-types
PATCH  /api/asset-types/{assetTypeId}
POST   /api/asset-types/import
GET    /api/asset-types/{assetTypeId}/export
GET    /api/asset-types/{assetTypeId}/workers
```

## Worker Types

```text
GET    /api/worker-types
GET    /api/worker-types/{workerTypeId}
POST   /api/worker-types
PATCH  /api/worker-types/{workerTypeId}
POST   /api/worker-types/import
GET    /api/worker-types/{workerTypeId}/export
POST   /api/worker-types/{workerTypeId}/enable
POST   /api/worker-types/{workerTypeId}/disable
GET    /api/worker-types/{workerTypeId}/tasks
GET    /api/worker-types/{workerTypeId}/errors
GET    /api/worker-types/{workerTypeId}/contexts
```

## Worker Subscriptions

```text
GET    /api/worker-subscriptions
POST   /api/worker-subscriptions
PATCH  /api/worker-subscriptions/{subscriptionId}
DELETE /api/worker-subscriptions/{subscriptionId}
POST   /api/worker-subscriptions/test-match
GET    /api/worker-subscriptions/matrix
```

## Tasks

```text
GET    /api/tasks
GET    /api/tasks/{taskRunId}
POST   /api/tasks/{taskRunId}/retry
POST   /api/tasks/{taskRunId}/cancel
POST   /api/tasks/{taskRunId}/dead-letter
POST   /api/tasks/{taskRunId}/release-lease
POST   /api/tasks/{taskRunId}/resume
GET    /api/tasks/{taskRunId}/logs
GET    /api/tasks/{taskRunId}/checkpoints
```

## Worker Contexts

```text
GET    /api/worker-contexts
GET    /api/worker-contexts/{workerContextId}
POST   /api/worker-contexts/{workerContextId}/release-lock
POST   /api/worker-contexts/{workerContextId}/resume
POST   /api/worker-contexts/{workerContextId}/mark-abandoned
POST   /api/worker-contexts/{workerContextId}/dead-letter
```

## Events

```text
GET    /api/events
GET    /api/events/{eventId}
GET    /api/events/{eventId}/chain
GET    /api/events/stream
```

## Findings

```text
GET    /api/findings
GET    /api/findings/{findingId}
PATCH  /api/findings/{findingId}
POST   /api/findings/{findingId}/confirm
POST   /api/findings/{findingId}/false-positive
POST   /api/findings/{findingId}/needs-review
POST   /api/findings/{findingId}/notes
POST   /api/findings/{findingId}/tags
GET    /api/findings/{findingId}/export
```

---

# 9. Implementation Checklist

## Foundation

* [ ] Remove all Orchestrator and ReconOrchestrator concepts.
* [ ] Remove orchestration routes, screens, menus, models, and services.
* [ ] Define Target model.
* [ ] Define Asset model.
* [ ] Define AssetType model.
* [ ] Define AssetEvent model.
* [ ] Define WorkerType model.
* [ ] Define WorkerSubscription model.
* [ ] Define WorkerInstance model.
* [ ] Define TaskRun model.
* [ ] Define WorkerContext model.
* [ ] Define WorkerCheckpoint model.
* [ ] Define Finding model.
* [ ] Define Artifact model.

## Event System

* [ ] Implement immutable event store.
* [ ] Implement asset lifecycle events.
* [ ] Implement worker lifecycle events.
* [ ] Implement task lifecycle events.
* [ ] Implement correlation IDs.
* [ ] Implement causation IDs.
* [ ] Implement discovery chain IDs.
* [ ] Implement event chain lookup API.
* [ ] Implement live event stream.

## Worker Routing

* [ ] Implement worker type registry.
* [ ] Implement worker subscription registry.
* [ ] Implement event router.
* [ ] Match events to enabled worker subscriptions.
* [ ] Match by asset type.
* [ ] Match by event type.
* [ ] Match by target override.
* [ ] Match by filter expression.
* [ ] Queue matching task runs.
* [ ] Prevent disabled workers from receiving work.
* [ ] Prevent disabled subscriptions from receiving work.

## Worker Execution

* [ ] Implement task queue.
* [ ] Implement task leases.
* [ ] Implement worker heartbeat.
* [ ] Implement worker instance registration.
* [ ] Implement worker health status.
* [ ] Implement task claim API.
* [ ] Implement task completion API.
* [ ] Implement task failure API.
* [ ] Implement retry policy.
* [ ] Implement dead-letter policy.
* [ ] Implement stale lease recovery.
* [ ] Implement abandoned task detection.

## Checkpointing and Resume

* [ ] Implement WorkerContext persistence.
* [ ] Implement WorkerCheckpoint persistence.
* [ ] Implement checkpoint write API.
* [ ] Implement checkpoint read API.
* [ ] Implement context locking.
* [ ] Implement context lock expiration.
* [ ] Implement lock release.
* [ ] Implement resume from checkpoint.
* [ ] Ensure only the same worker type can resume context.
* [ ] Prevent duplicate assets during resume.
* [ ] Show context history in UI.

## Asset Types

* [ ] Build Asset Types list page.
* [ ] Build Asset Type detail page.
* [ ] Build Create Asset Type form.
* [ ] Build Edit Asset Type form.
* [ ] Build Import Asset Type flow.
* [ ] Build Export Asset Type flow.
* [ ] Build schema JSON editor.
* [ ] Build canonicalization rules editor.
* [ ] Build deduplication strategy editor.
* [ ] Show consuming workers.
* [ ] Show producing workers.

## Worker Types

* [ ] Build Worker Types catalog.
* [ ] Build Worker Type detail page.
* [ ] Build Create Worker Type form.
* [ ] Build Import Worker Type flow.
* [ ] Build Export Worker Type flow.
* [ ] Implement enable worker type.
* [ ] Implement disable worker type.
* [ ] Build worker configuration editor.
* [ ] Build input asset type selector.
* [ ] Build output asset type selector.
* [ ] Build event trigger selector.
* [ ] Build concurrency settings editor.
* [ ] Build retry policy editor.
* [ ] Build timeout settings editor.
* [ ] Build safety settings editor.
* [ ] Build requirements panel.
* [ ] Build package definition viewer.

## Worker Subscription Matrix

* [ ] Build matrix with worker types as rows.
* [ ] Build asset types as columns.
* [ ] Group asset type columns by category.
* [ ] Show discovery event subscriptions.
* [ ] Show confirmation event subscriptions.
* [ ] Show update event subscriptions.
* [ ] Show disabled state.
* [ ] Show target override indicator.
* [ ] Show filter expression indicator.
* [ ] Build subscription drawer.
* [ ] Implement subscription create/edit.
* [ ] Implement test match action.
* [ ] Implement per-target overrides.

## Command Center

* [ ] Build system health cards.
* [ ] Build active targets table.
* [ ] Build worker health panel.
* [ ] Build queue depth chart.
* [ ] Build event throughput chart.
* [ ] Build failed tasks panel.
* [ ] Build abandoned contexts panel.
* [ ] Build recent findings panel.
* [ ] Build live event stream.
* [ ] Add quick action for importing worker types.
* [ ] Add quick action for creating asset types.
* [ ] Add quick action for opening the subscription matrix.

## Assets

* [ ] Build virtualized Asset Explorer grid.
* [ ] Implement server-side asset search.
* [ ] Implement server-side asset filtering.
* [ ] Implement server-side asset sorting.
* [ ] Implement saved asset views.
* [ ] Implement column chooser.
* [ ] Implement bulk actions.
* [ ] Build asset detail drawer.
* [ ] Build asset lineage view.
* [ ] Build parent/child asset view.
* [ ] Build worker task history tab.
* [ ] Build related events tab.
* [ ] Build artifacts tab.
* [ ] Build raw JSON tab.
* [ ] Implement mark verified.
* [ ] Implement reject.
* [ ] Implement tag.
* [ ] Implement mark high value.
* [ ] Implement send to worker.
* [ ] Implement re-emit discovery event.
* [ ] Implement re-emit confirmation event.

## Tasks

* [ ] Build task list.
* [ ] Build task detail page.
* [ ] Show task state.
* [ ] Show worker type.
* [ ] Show worker instance.
* [ ] Show target.
* [ ] Show input asset.
* [ ] Show trigger event.
* [ ] Show progress.
* [ ] Show current step.
* [ ] Show lease owner.
* [ ] Show lease expiration.
* [ ] Show retry count.
* [ ] Show logs.
* [ ] Show output assets.
* [ ] Show artifacts.
* [ ] Show failure reason.
* [ ] Show checkpoints.
* [ ] Show resume history.
* [ ] Implement retry.
* [ ] Implement cancel.
* [ ] Implement dead-letter.
* [ ] Implement release lease.
* [ ] Implement resume from checkpoint.

## Findings

* [ ] Build findings table.
* [ ] Build finding detail drawer.
* [ ] Implement severity filter.
* [ ] Implement confidence filter.
* [ ] Implement triage status filter.
* [ ] Show evidence preview.
* [ ] Show source asset.
* [ ] Show source worker type.
* [ ] Show source event.
* [ ] Show affected target.
* [ ] Show discovery chain.
* [ ] Implement mark confirmed.
* [ ] Implement mark false positive.
* [ ] Implement mark needs review.
* [ ] Implement notes.
* [ ] Implement tags.
* [ ] Implement export.

## Events

* [ ] Build event stream page.
* [ ] Implement event type filters.
* [ ] Implement target filter.
* [ ] Implement asset filter.
* [ ] Implement worker type filter.
* [ ] Implement correlation ID search.
* [ ] Implement causation ID search.
* [ ] Build event detail drawer.
* [ ] Build event chain view.
* [ ] Show payload JSON.
* [ ] Show resulting events.

## Real-Time Updates

* [ ] Implement SignalR or equivalent real-time updates.
* [ ] Stream command center summaries.
* [ ] Stream worker health changes.
* [ ] Stream queue depth changes.
* [ ] Stream target-scoped events.
* [ ] Stream task state changes.
* [ ] Stream new findings.
* [ ] Avoid disrupting active grids during live updates.
* [ ] Show “new updates available” indicators.

## Security and Safety

* [ ] Enforce scope before worker execution.
* [ ] Enforce target safety limits.
* [ ] Enforce rate limits.
* [ ] Enforce proxy restrictions.
* [ ] Audit worker configuration changes.
* [ ] Audit worker enable/disable changes.
* [ ] Audit manual event re-emission.
* [ ] Audit task retry/resume actions.
* [ ] Require confirmation for destructive actions.
* [ ] Validate imported worker definitions.
* [ ] Validate imported asset type definitions.

---

# 10. Acceptance Criteria

The app is complete only when:

* [ ] No orchestration concept exists.
* [ ] Workers are driven entirely by asset events.
* [ ] Users can create/import worker types.
* [ ] Users can create/import asset types.
* [ ] Users can enable/disable worker types.
* [ ] Users can configure which asset types each worker processes.
* [ ] Users can configure which event types trigger each worker.
* [ ] Discovery-event and confirmation-event behavior are configured separately.
* [ ] Failed worker progress is checkpointed.
* [ ] Another worker instance of the same type can resume abandoned work.
* [ ] Asset lineage shows events, workers, tasks, and parent/child relationships.
* [ ] The Asset Explorer handles large datasets with server-side querying.
* [ ] The Worker Subscription Matrix makes event-driven behavior understandable.
* [ ] The Command Center shows health, queues, failures, events, and findings.
* [ ] The UI is dark-mode first, dense, professional, and operator-focused.
