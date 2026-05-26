using Argus.ServiceDefaults;
using System.Text.Json.Nodes;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/ui/state", async (IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);

    var assetsTask = gateway.GetJsonAsync(endpoints.Asset, "/assets?pageSize=200", cancellationToken);
    var tasksTask = gateway.GetJsonAsync(endpoints.Task, "/tasks", cancellationToken);
    var programsTask = gateway.GetJsonAsync(endpoints.ProgramScope, "/programs", cancellationToken);
    var scopesTask = gateway.GetJsonAsync(endpoints.ProgramScope, "/scopes", cancellationToken);
    var eventsTask = gateway.GetJsonAsync(endpoints.Realtime, "/events?take=80", cancellationToken);
    var workersTask = gateway.GetJsonAsync(endpoints.Realtime, "/workers", cancellationToken);
    var rateLimitsTask = gateway.GetJsonAsync(endpoints.RateLimit, "/rate-limits", cancellationToken);
    var scanPlansTask = gateway.GetJsonAsync(endpoints.ScanOrchestrator, "/scan-plans", cancellationToken);

    await Task.WhenAll(assetsTask, tasksTask, programsTask, scopesTask, eventsTask, workersTask, rateLimitsTask, scanPlansTask);

    return Results.Json(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        assets = assetsTask.Result,
        tasks = tasksTask.Result,
        programs = programsTask.Result,
        scopes = scopesTask.Result,
        events = eventsTask.Result,
        workers = workersTask.Result,
        rateLimits = rateLimitsTask.Result,
        scanPlans = scanPlansTask.Result
    });
});

app.MapGet("/ui/assets/{assetId:guid}/relationships", async (
    Guid assetId,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    var relationships = await gateway.GetJsonAsync(endpoints.Asset, $"/assets/{assetId}/relationships", cancellationToken);

    return Results.Json(relationships ?? new JsonArray());
});

app.MapPost("/ui/programs", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);

    var programResponse = await gateway.PostJsonRawAsync(endpoints.ProgramScope, "/programs", payload, cancellationToken);

    if (!programResponse.IsSuccessStatusCode)
    {
        return await gateway.ResultFromResponse(programResponse);
    }

    var body = await programResponse.Content.ReadAsStringAsync(cancellationToken);
    var program = JsonNode.Parse(body);

    if (program?["programId"]?.GetValue<string>() is string programId &&
        payload["source"]?.GetValue<string>() is string source &&
        source != "custom")
    {
        var createScopePayload = new JsonObject
        {
            ["programId"] = programId,
            ["scopeType"] = "domain",
            ["action"] = "Include",
            ["pattern"] = source,
            ["notes"] = "Auto-created from program source"
        };

        try
        {
            var scopeResponse = await gateway.PostJsonRawAsync(endpoints.ProgramScope, $"/programs/{programId}/scopes", createScopePayload, cancellationToken);

            if (scopeResponse.IsSuccessStatusCode)
            {
                var scopeBody = await scopeResponse.Content.ReadAsStringAsync(cancellationToken);
                var scope = JsonNode.Parse(scopeBody);

                if (scope?["scopeId"]?.GetValue<string>() is string scopeId)
                {
                    var discoverPayload = new JsonObject
                    {
                        ["programId"] = programId,
                        ["scopeId"] = scopeId,
                        ["domain"] = source
                    };

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await gateway.PostJsonAsync(endpoints.ScanOrchestrator, "/scan-plans/domain-discovery", discoverPayload, cancellationToken);
                        }
                        catch { }
                    }, cancellationToken);
                }
            }
        }
        catch { }
    }

    return await gateway.ResultFromResponse(programResponse);
});

app.MapPost("/ui/programs/{programId:guid}/scopes", async (
    Guid programId,
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    payload["programId"] = programId;
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.ProgramScope, $"/programs/{programId}/scopes", payload, cancellationToken);
});

app.MapPost("/ui/scan-plans/domain-discovery", async (
    JsonObject payload,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    return await gateway.PostJsonAsync(endpoints.ScanOrchestrator, "/scan-plans/domain-discovery", payload, cancellationToken);
});

app.MapGet("/ui/events/stream", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    var endpoints = ArgusServiceEndpoints.From(app.Configuration);
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    string? lastEventId = null;

    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await gateway.GetJsonAsync(endpoints.Realtime, "/events?take=1", cancellationToken);
        var eventId = events is JsonArray { Count: > 0 } eventArray
            ? eventArray[0]?["eventId"]?.GetValue<string>()
            : null;

        if (!string.IsNullOrWhiteSpace(eventId) && eventId != lastEventId)
        {
            lastEventId = eventId;
            await context.Response.WriteAsync($"event: argus-event\n", cancellationToken);
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { eventId, observedAt = DateTimeOffset.UtcNow })}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        else
        {
            await context.Response.WriteAsync($": heartbeat {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
});

app.MapGet("/", () => Results.Content("""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Argus Recon Platform</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #101214;
      --panel: #171b1f;
      --panel-2: #20262b;
      --line: #364049;
      --text: #edf2f4;
      --muted: #aab5bd;
      --blue: #4fb3ff;
      --green: #54d990;
      --amber: #f2b84b;
      --red: #ff6b6b;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font: 13px/1.4 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      height: 54px;
      padding: 10px 16px;
      border-bottom: 1px solid var(--line);
      background: #14181c;
    }
    h1 {
      margin: 0;
      font-size: 18px;
      font-weight: 650;
      letter-spacing: 0;
    }
    main {
      display: grid;
      grid-template-columns: 220px minmax(0, 1fr) 320px;
      min-height: calc(100vh - 54px);
    }
    nav, aside {
      background: var(--panel);
      border-right: 1px solid var(--line);
      padding: 10px;
    }
    aside {
      border-right: 0;
      border-left: 1px solid var(--line);
      overflow: auto;
      max-height: calc(100vh - 54px);
    }
    button, input, select, .chip {
      min-height: 28px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--panel-2);
      color: var(--text);
      padding: 4px 8px;
      font: inherit;
    }
    input, select { min-width: 140px; }
    form.inline {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      padding: 10px;
      border-bottom: 1px solid var(--line);
      background: #12161a;
    }
    form.inline input, form.inline select { min-width: 170px; }
    nav button {
      width: 100%;
      display: block;
      text-align: left;
      margin-bottom: 6px;
    }
    nav button.active {
      border-color: var(--blue);
      color: var(--blue);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      min-height: 52px;
      padding: 10px;
      border-bottom: 1px solid var(--line);
      background: #15191d;
    }
    .surface {
      overflow: auto;
      max-height: calc(100vh - 106px);
    }
    table {
      width: 100%;
      min-width: 1060px;
      border-collapse: collapse;
    }
    th, td {
      height: 32px;
      border-bottom: 1px solid #293139;
      padding: 5px 8px;
      text-align: left;
      white-space: nowrap;
      vertical-align: middle;
    }
    th {
      position: sticky;
      top: 0;
      z-index: 1;
      background: #20262b;
      color: var(--muted);
      font-weight: 600;
    }
    tbody tr { cursor: default; }
    tbody tr:hover { background: #1d2429; }
    tbody tr.selected { background: #20313a; }
    .metric-row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 8px;
      padding: 8px 0;
      border-bottom: 1px solid #293139;
    }
    .metric { font-size: 18px; font-weight: 700; }
    .side-title {
      margin: 14px 0 6px;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
      text-transform: uppercase;
    }
    .ok { color: var(--green); }
    .warn { color: var(--amber); }
    .hot { color: var(--red); }
    .link { color: var(--blue); }
    .muted { color: var(--muted); }
    .log {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      font-size: 12px;
      white-space: pre-wrap;
    }
    @media (max-width: 980px) {
      main { grid-template-columns: 1fr; }
      nav, aside { border: 0; border-bottom: 1px solid var(--line); max-height: none; }
      aside { border-top: 1px solid var(--line); }
      table { min-width: 900px; }
    }
  </style>
</head>
<body>
  <header>
    <h1>Argus Recon Platform</h1>
    <div class="chip" id="refreshState">connecting</div>
  </header>
  <main>
    <nav id="tabs">
      <button data-view="assets" class="active">Asset Explorer</button>
      <button data-view="tasks">Task Monitor</button>
      <button data-view="workers">Worker Fleet</button>
      <button data-view="events">Live Events</button>
      <button data-view="rateLimits">Rate Limits</button>
      <button data-view="scanPlans">Scan Plans</button>
      <button data-view="scopes">Scope Explorer</button>
      <button data-view="programs">Programs</button>
    </nav>
    <section>
      <div class="toolbar">
        <button id="refreshButton">Refresh</button>
        <input id="search" placeholder="Filter visible rows">
        <select id="assetType">
          <option value="">All asset types</option>
        </select>
        <span class="chip" id="rowCount">0 rows</span>
        <span class="chip" id="streamState">stream connecting</span>
      </div>
      <div class="surface" id="content"></div>
    </section>
    <aside>
      <div class="metric-row"><span>Assets</span><span class="metric ok" id="assetCount">0</span></div>
      <div class="metric-row"><span>Tasks running</span><span class="metric" id="runningTaskCount">0</span></div>
      <div class="metric-row"><span>Queue depth</span><span class="metric" id="queueDepth">0</span></div>
      <div class="metric-row"><span>Workers online</span><span class="metric ok" id="workerCount">0</span></div>
      <div class="metric-row"><span>Rate-limit waits</span><span class="metric warn" id="rateLimitWaits">0</span></div>
      <div class="side-title">Selection</div>
      <div id="selectionRail" class="log muted">No row selected</div>
      <div class="side-title">Recent Events</div>
      <div id="eventRail" class="log muted"></div>
    </aside>
  </main>
  <script>
    const state = { view: "assets", data: null, filter: "", assetType: "", currentRows: [], selected: null };
    const content = document.querySelector("#content");
    const search = document.querySelector("#search");
    const assetType = document.querySelector("#assetType");

    document.querySelector("#tabs").addEventListener("click", event => {
      const button = event.target.closest("button[data-view]");
      if (!button) return;
      state.view = button.dataset.view;
      document.querySelectorAll("#tabs button").forEach(tab => tab.classList.toggle("active", tab === button));
      state.selected = null;
      render();
    });

    document.querySelector("#refreshButton").addEventListener("click", load);
    search.addEventListener("input", () => { state.filter = search.value.toLowerCase(); render(); });
    assetType.addEventListener("change", () => { state.assetType = assetType.value; render(); });
    content.addEventListener("submit", submitCommand);
    content.addEventListener("click", selectRow);

    async function load() {
      const marker = document.querySelector("#refreshState");
      marker.textContent = "refreshing";
      try {
        const response = await fetch("/ui/state", { cache: "no-store" });
        state.data = await response.json();
        marker.textContent = new Date(state.data.generatedAt).toLocaleTimeString();
        populateAssetTypes();
        render();
      } catch (error) {
        marker.textContent = "offline";
        content.innerHTML = `<div class="toolbar hot">Unable to load service state: ${escapeHtml(error.message)}</div>`;
      }
    }

    function populateAssetTypes() {
      const selected = assetType.value;
      const assets = state.data?.assets?.items ?? [];
      const types = [...new Set(assets.map(asset => asset.type).filter(Boolean))].sort();
      assetType.innerHTML = `<option value="">All asset types</option>${types.map(type => `<option value="${escapeHtml(type)}">${escapeHtml(type)}</option>`).join("")}`;
      assetType.value = selected;
    }

    function render() {
      if (!state.data) return;
      updateMetrics();
      const rows = getRowsForView();
      state.currentRows = rows;
      document.querySelector("#rowCount").textContent = `${rows.length} rows`;
      content.innerHTML = controlsFor(state.view) + tableFor(state.view, rows);
      highlightSelectedRow();
      renderSelectionRail();
      renderEventRail();
    }

    function updateMetrics() {
      const assets = state.data.assets?.items ?? [];
      const tasks = state.data.tasks ?? [];
      const workers = state.data.workers ?? [];
      const events = state.data.events ?? [];
      document.querySelector("#assetCount").textContent = state.data.assets?.totalCount ?? assets.length;
      document.querySelector("#runningTaskCount").textContent = tasks.filter(task => task.state === "Running").length;
      document.querySelector("#queueDepth").textContent = tasks.filter(task => ["Requested", "Queued", "RetryPending"].includes(task.state)).length;
      document.querySelector("#workerCount").textContent = workers.filter(worker => worker.isOnline).length;
      document.querySelector("#rateLimitWaits").textContent = events.filter(event => event.eventType === "RateLimitDelayed").length;
    }

    function getRowsForView() {
      const data = state.data;
      let rows = state.view === "assets" ? (data.assets?.items ?? [])
        : state.view === "tasks" ? (data.tasks ?? [])
        : state.view === "workers" ? (data.workers ?? [])
        : state.view === "events" ? (data.events ?? [])
        : state.view === "rateLimits" ? (data.rateLimits ?? [])
        : state.view === "scanPlans" ? (data.scanPlans ?? [])
        : state.view === "scopes" ? (data.scopes ?? [])
        : (data.programs ?? []);

      if (state.view === "assets" && state.assetType) {
        rows = rows.filter(asset => asset.type === state.assetType);
      }

      if (state.filter) {
        rows = rows.filter(row => JSON.stringify(row).toLowerCase().includes(state.filter));
      }

      return rows;
    }

    function tableFor(view, rows) {
      if (view === "assets") {
        return renderTable(["Type","Subtype","Value","Status","Interesting","Risk","First Seen","Last Seen","Tags"], rows.map(asset => [
          asset.type, asset.subtype ?? "", asset.value, asset.status, asset.interestingScore, asset.riskScore,
          formatTime(asset.firstSeenAt), formatTime(asset.lastSeenAt), (asset.tags ?? []).join(", ")
        ]));
      }
      if (view === "tasks") {
        return renderTable(["Type","Capability","State","Attempt","Progress","Lease Owner","Started","Completed","Error"], rows.map(task => [
          task.taskType, task.workerCapability, task.state, `${task.attempt}/${task.maxAttempts}`, `${task.progressPercent}%`,
          task.leaseOwner ?? "", formatTime(task.startedAt), formatTime(task.completedAt), task.errorCode ?? ""
        ]));
      }
      if (view === "workers") {
        return renderTable(["Worker","Type","Online","Running","Capacity","Last Seen","Version"], rows.map(worker => [
          worker.workerId, worker.workerType, worker.isOnline ? "yes" : "no", worker.runningTasks, worker.maxConcurrency,
          formatTime(worker.lastSeenAt), worker.version ?? ""
        ]));
      }
      if (view === "events") {
        return renderTable(["Time","Type","Source","Correlation","Payload"], rows.map(event => [
          formatTime(event.occurredAt), event.eventType, event.sourceService, event.correlationId, JSON.stringify(event.payload ?? {})
        ]));
      }
      if (view === "rateLimits") {
        return renderTable(["Bucket","Capacity","Remaining","Resets"], rows.map(bucket => [
          bucket.bucketKey, bucket.capacity, bucket.remaining, formatTime(bucket.resetsAt)
        ]));
      }
      if (view === "scanPlans") {
        return renderTable(["Workflow","Target","State","Tasks","Seed Asset","Created"], rows.map(plan => [
          plan.workflowType, plan.target, plan.state, (plan.createdTaskIds ?? []).length,
          plan.seededDomainAssetId ?? "", formatTime(plan.createdAt)
        ]));
      }
      if (view === "scopes") {
        return renderTable(["Program","Type","Action","Pattern","Notes","Created"], rows.map(scope => [
          programName(scope.programId), scope.scopeType, scope.action, scope.pattern, scope.notes ?? "", formatTime(scope.createdAt)
        ]));
      }
      return renderTable(["Name","Source","Scopes","Created","Updated"], rows.map(program => [
        program.name, program.source, (program.scopes ?? []).length, formatTime(program.createdAt), formatTime(program.updatedAt)
      ]));
    }

    function controlsFor(view) {
      if (view === "programs") {
        return `<form class="inline" data-command="create-program">
          <input name="name" placeholder="Program name" required>
          <input name="source" placeholder="Source" value="custom">
          <input name="externalUrl" placeholder="External URL">
          <button type="submit">Create Program</button>
        </form>`;
      }
      if (view === "scopes") {
        return `<form class="inline" data-command="create-scope">
          ${programSelect("programId")}
          <select name="scopeType"><option value="domain">Domain</option><option value="wildcard-domain">Wildcard Domain</option><option value="url">URL</option><option value="cidr">CIDR</option></select>
          <select name="action"><option value="Include">Include</option><option value="Exclude">Exclude</option></select>
          <input name="pattern" placeholder="Scope pattern" required>
          <input name="notes" placeholder="Notes">
          <button type="submit">Add Scope</button>
        </form>`;
      }
      if (view === "scanPlans") {
        return `<form class="inline" data-command="start-domain-discovery">
          ${programSelect("programId")}
          ${scopeSelect("scopeId")}
          <input name="domain" placeholder="example.com" required>
          <button type="submit">Start Discovery</button>
        </form>`;
      }
      return "";
    }

    async function submitCommand(event) {
      const form = event.target.closest("form[data-command]");
      if (!form) return;
      event.preventDefault();

      const marker = document.querySelector("#refreshState");
      marker.textContent = "submitting";
      const formData = new FormData(form);
      const command = form.dataset.command;
      const payload = Object.fromEntries([...formData.entries()].map(([key, value]) => [key, normalizeFormValue(value)]));

      try {
        let response;
        if (command === "create-program") {
          response = await postJson("/ui/programs", payload);
        } else if (command === "create-scope") {
          response = await postJson(`/ui/programs/${encodeURIComponent(payload.programId)}/scopes`, payload);
        } else if (command === "start-domain-discovery") {
          response = await postJson("/ui/scan-plans/domain-discovery", payload);
        }

        if (!response?.ok) {
          const message = await response?.text();
          throw new Error(message || `Command failed with ${response?.status ?? "unknown status"}`);
        }

        form.reset();
        await load();
      } catch (error) {
        marker.textContent = "command failed";
        content.insertAdjacentHTML("afterbegin", `<div class="toolbar hot">${escapeHtml(error.message)}</div>`);
      }
    }

    function programSelect(name) {
      const programs = state.data?.programs ?? [];
      const options = programs.map(program => `<option value="${escapeHtml(program.programId)}">${escapeHtml(program.name)}</option>`).join("");
      return `<select name="${name}" required><option value="">Program</option>${options}</select>`;
    }

    function scopeSelect(name) {
      const scopes = state.data?.scopes ?? [];
      const options = scopes
        .filter(scope => scope.action === "Include")
        .map(scope => `<option value="${escapeHtml(scope.scopeId)}">${escapeHtml(programName(scope.programId))}: ${escapeHtml(scope.pattern)}</option>`)
        .join("");
      return `<select name="${name}"><option value="">Scope optional</option>${options}</select>`;
    }

    function programName(programId) {
      const program = (state.data?.programs ?? []).find(program => program.programId === programId);
      return program?.name ?? programId;
    }

    function normalizeFormValue(value) {
      const stringValue = String(value).trim();
      return stringValue.length ? stringValue : null;
    }

    function postJson(url, payload) {
      return fetch(url, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(payload)
      });
    }

    function renderTable(headers, rows) {
      const body = rows.length
        ? rows.map((row, index) => `<tr data-row-index="${index}">${row.map(value => `<td>${escapeHtml(value ?? "")}</td>`).join("")}</tr>`).join("")
        : `<tr><td colspan="${headers.length}" class="muted">No rows</td></tr>`;
      return `<table><thead><tr>${headers.map(header => `<th>${header}</th>`).join("")}</tr></thead><tbody>${body}</tbody></table>`;
    }

    function selectRow(event) {
      const row = event.target.closest("tr[data-row-index]");
      if (!row) return;

      const index = Number(row.dataset.rowIndex);
      state.selected = {
        view: state.view,
        index,
        row: state.currentRows[index],
        relationships: null,
        relationshipStatus: state.view === "assets" ? "loading" : null
      };

      highlightSelectedRow();
      renderSelectionRail();
      loadSelectedAssetRelationships();
    }

    function highlightSelectedRow() {
      content.querySelectorAll("tr[data-row-index]").forEach(row => {
        const isSelected = state.selected?.view === state.view && Number(row.dataset.rowIndex) === state.selected.index;
        row.classList.toggle("selected", isSelected);
      });
    }

    function renderSelectionRail() {
      const rail = document.querySelector("#selectionRail");

      if (!state.selected?.row) {
        rail.textContent = "No row selected";
        return;
      }

      const lines = flattenForDisplay(state.selected.row);

      if (state.selected.view === "assets") {
        lines.push("");
        lines.push("relationships:");

        if (state.selected.relationshipStatus === "loading") {
          lines.push("  loading...");
        } else if (state.selected.relationshipStatus === "error") {
          lines.push("  unavailable");
        } else if (state.selected.relationships?.length) {
          lines.push(...state.selected.relationships.map(formatRelationship));
        } else {
          lines.push("  none");
        }
      }

      rail.textContent = lines.join("\n");
    }

    async function loadSelectedAssetRelationships() {
      const selected = state.selected;
      const assetId = selected?.row?.assetId;

      if (selected?.view !== "assets" || !assetId) {
        return;
      }

      try {
        const response = await fetch(`/ui/assets/${encodeURIComponent(assetId)}/relationships`, { cache: "no-store" });

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const relationships = await response.json();

        if (state.selected?.row?.assetId !== assetId) {
          return;
        }

        state.selected.relationships = Array.isArray(relationships) ? relationships : [];
        state.selected.relationshipStatus = "ready";
        renderSelectionRail();
      } catch {
        if (state.selected?.row?.assetId === assetId) {
          state.selected.relationshipStatus = "error";
          renderSelectionRail();
        }
      }
    }

    function formatRelationship(relationship) {
      const direction = relationship.fromAssetId === state.selected?.row?.assetId ? "out" : "in";
      const peer = direction === "out" ? relationship.toAssetId : relationship.fromAssetId;
      return `  ${direction} ${relationship.edgeType} ${peer}`;
    }

    function flattenForDisplay(row) {
      return Object.entries(row)
        .filter(([, value]) => value !== null && value !== undefined && value !== "")
        .map(([key, value]) => `${key}: ${formatDetailValue(value)}`);
    }

    function formatDetailValue(value) {
      if (Array.isArray(value)) {
        return value.length ? value.map(item => typeof item === "object" ? JSON.stringify(item) : item).join(", ") : "[]";
      }

      if (typeof value === "object") {
        return JSON.stringify(value);
      }

      return value;
    }

    function renderEventRail() {
      const events = (state.data.events ?? []).slice(0, 12);
      document.querySelector("#eventRail").textContent = events.map(event => `${formatTime(event.occurredAt)} ${event.eventType}`).join("\n");
    }

    function connectEventStream() {
      const streamState = document.querySelector("#streamState");

      if (!window.EventSource) {
        streamState.textContent = "stream unsupported";
        setInterval(load, 5000);
        return;
      }

      const source = new EventSource("/ui/events/stream");
      source.addEventListener("open", () => { streamState.textContent = "stream live"; });
      source.addEventListener("argus-event", () => { streamState.textContent = "event received"; load(); });
      source.addEventListener("error", () => {
        streamState.textContent = "stream reconnecting";
      });
    }

    function formatTime(value) {
      return value ? new Date(value).toLocaleTimeString() : "";
    }

    function escapeHtml(value) {
      return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
    }

    load();
    connectEventStream();
    setInterval(load, 15000);
  </script>
</body>
</html>
""", "text/html"));

app.Run();

internal sealed class ArgusUiGateway(IHttpClientFactory httpClientFactory)
{
    public async Task<JsonNode?> GetJsonAsync(string baseAddress, string path, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            return await client.GetFromJsonAsync<JsonNode>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Contains("assets", StringComparison.OrdinalIgnoreCase)
                ? JsonNode.Parse("""{"items":[],"page":1,"pageSize":100,"totalCount":0}""")
                : JsonNode.Parse("[]");
        }
    }

    public async Task<IResult> PostJsonAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseAddress);
            using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            return Results.Content(content, contentType, statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem($"Unable to reach backend service: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public async Task<HttpResponseMessage> PostJsonRawAsync(string baseAddress, string path, JsonObject payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseAddress);
        return await client.PostAsJsonAsync(path, payload, cancellationToken);
    }

    public async Task<IResult> ResultFromResponse(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)response.StatusCode);
    }
}

internal sealed record ArgusServiceEndpoints(
    string ProgramScope,
    string Asset,
    string Task,
    string RateLimit,
    string ScanOrchestrator,
    string Realtime)
{
    public static ArgusServiceEndpoints From(IConfiguration configuration) =>
        new(
            configuration["ARGUS_PROGRAM_SCOPE_SERVICE"] ?? "https+http://program-scope-service",
            configuration["ARGUS_ASSET_SERVICE"] ?? "https+http://asset-service",
            configuration["ARGUS_TASK_SERVICE"] ?? "https+http://task-service",
            configuration["ARGUS_RATE_LIMIT_SERVICE"] ?? "https+http://rate-limit-service",
            configuration["ARGUS_SCAN_ORCHESTRATOR_SERVICE"] ?? "https+http://scan-orchestrator-service",
            configuration["ARGUS_REALTIME_SERVICE"] ?? "https+http://realtime-service");
}
