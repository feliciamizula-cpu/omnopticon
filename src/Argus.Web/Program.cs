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

    var assetsTask = gateway.GetJsonAsync("https+http://asset-service", "/assets?pageSize=200", cancellationToken);
    var tasksTask = gateway.GetJsonAsync("https+http://task-service", "/tasks", cancellationToken);
    var programsTask = gateway.GetJsonAsync("https+http://program-scope-service", "/programs", cancellationToken);
    var eventsTask = gateway.GetJsonAsync("https+http://realtime-service", "/events?take=80", cancellationToken);
    var workersTask = gateway.GetJsonAsync("https+http://realtime-service", "/workers", cancellationToken);
    var rateLimitsTask = gateway.GetJsonAsync("https+http://rate-limit-service", "/rate-limits", cancellationToken);
    var scanPlansTask = gateway.GetJsonAsync("https+http://scan-orchestrator-service", "/scan-plans", cancellationToken);

    await Task.WhenAll(assetsTask, tasksTask, programsTask, eventsTask, workersTask, rateLimitsTask, scanPlansTask);

    return Results.Json(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        assets = assetsTask.Result,
        tasks = tasksTask.Result,
        programs = programsTask.Result,
        events = eventsTask.Result,
        workers = workersTask.Result,
        rateLimits = rateLimitsTask.Result,
        scanPlans = scanPlansTask.Result
    });
});

app.MapGet("/ui/events/stream", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var gateway = new ArgusUiGateway(httpClientFactory);
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    string? lastEventId = null;

    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await gateway.GetJsonAsync("https+http://realtime-service", "/events?take=1", cancellationToken);
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
      <div class="side-title">Recent Events</div>
      <div id="eventRail" class="log muted"></div>
    </aside>
  </main>
  <script>
    const state = { view: "assets", data: null, filter: "", assetType: "" };
    const content = document.querySelector("#content");
    const search = document.querySelector("#search");
    const assetType = document.querySelector("#assetType");

    document.querySelector("#tabs").addEventListener("click", event => {
      const button = event.target.closest("button[data-view]");
      if (!button) return;
      state.view = button.dataset.view;
      document.querySelectorAll("#tabs button").forEach(tab => tab.classList.toggle("active", tab === button));
      render();
    });

    document.querySelector("#refreshButton").addEventListener("click", load);
    search.addEventListener("input", () => { state.filter = search.value.toLowerCase(); render(); });
    assetType.addEventListener("change", () => { state.assetType = assetType.value; render(); });

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
      document.querySelector("#rowCount").textContent = `${rows.length} rows`;
      content.innerHTML = tableFor(state.view, rows);
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
      return renderTable(["Name","Source","Scopes","Created","Updated"], rows.map(program => [
        program.name, program.source, (program.scopes ?? []).length, formatTime(program.createdAt), formatTime(program.updatedAt)
      ]));
    }

    function renderTable(headers, rows) {
      const body = rows.length
        ? rows.map(row => `<tr>${row.map(value => `<td>${escapeHtml(value ?? "")}</td>`).join("")}</tr>`).join("")
        : `<tr><td colspan="${headers.length}" class="muted">No rows</td></tr>`;
      return `<table><thead><tr>${headers.map(header => `<th>${header}</th>`).join("")}</tr></thead><tbody>${body}</tbody></table>`;
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return path.Contains("assets", StringComparison.OrdinalIgnoreCase)
                ? JsonNode.Parse("""{"items":[],"page":1,"pageSize":100,"totalCount":0}""")
                : JsonNode.Parse("[]");
        }
    }
}
