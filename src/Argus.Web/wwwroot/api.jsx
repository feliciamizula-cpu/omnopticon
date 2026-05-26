/* eslint-disable */
// Argus UI BFF adapter.
// Keeps the provided prototype usable with generated sample data, then overlays live
// data from the existing /ui/* backend-for-frontend endpoints when services respond.

const __ARGUS_GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function __arr(payload) {
  if (!payload) return [];
  if (Array.isArray(payload)) return payload;
  if (Array.isArray(payload.items)) return payload.items;
  if (Array.isArray(payload.Items)) return payload.Items;
  return [];
}

function __num(v, fallback = 0) {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function __time(v, fallback = Date.now()) {
  if (!v) return fallback;
  const n = Date.parse(v);
  return Number.isFinite(n) ? n : fallback;
}

function __replaceArray(target, items) {
  if (!Array.isArray(target) || !Array.isArray(items)) return;
  target.splice(0, target.length, ...items);
}

function __eventColor(type) {
  const found = EVENT_TYPES.find(e => e.id === type);
  if (found) return found.color;
  if (/fail|error|dead/i.test(type)) return "red";
  if (/complete|confirm|success/i.test(type)) return "green";
  if (/start|checkpoint|lease/i.test(type)) return "violet";
  if (/finding|secret|vuln/i.test(type)) return "magenta";
  if (/scope|limit|warn/i.test(type)) return "amber";
  return "cyan";
}

function __workerColor(type) {
  return WORKER_TYPES.find(w => w.id === type)?.color || "cyan";
}

function __assetType(raw) {
  const t = String(raw || "Other");
  const map = {
    HttpResponse: "HtmlPage",
    Script: "JavaScriptFile",
    Style: "CssFile",
    Api: "ApiEndpoint",
    Finding: "FindingCandidate",
    Vulnerability: "FindingCandidate",
    DomainRoot: "Domain",
    DomainSubdomain: "Subdomain",
    IpV4: "Ip",
    IpV6: "Ip",
    Cidr: "Cidr",
    HtmlDocument: "HtmlPage",
    JsonDocument: "JsonDocument",
    JavaScriptFile: "JavaScriptFile",
    CssFile: "CssFile",
    ApiEndpoint: "ApiEndpoint",
    PortService: "Port",
    TechnologyFingerprint: "Technology",
  };
  return map[t] || t;
}

function __normalizeAsset(x) {
  const meta = x.metadata2 || x.metadata || {};
  const tags = Array.isArray(x.tags2) ? x.tags2 : Array.isArray(x.tags) ? x.tags : [];
  const type = __assetType(x.typeKey || x.subcategory || x.type || x.category);
  const status = String(x.scopeStatus || x.status || "New");
  const id = String(x.assetId || x.id || x.naturalKey || x.value);
  const parent = x.parentAssetId || x.parent || null;
  const value = String(x.value || x.naturalKey || id);
  let urlParts = null;
  try {
    if (/^https?:\/\//i.test(value)) {
      const u = new URL(value);
      urlParts = { full: u.href, host: u.host, path: u.pathname + u.search };
    }
  } catch (e) {}
  return {
    id,
    assetId: x.assetId || x.id,
    programId: x.programId,
    scopeId: x.scopeId,
    type,
    value,
    parent,
    parentValue: x.parentValue || meta.parent || null,
    status,
    scope: status === "OutOfScope" ? "OutOfScope" : (x.scopeStatus || "InScope"),
    confidence: Math.round(__num(x.confidence, 1) <= 1 ? __num(x.confidence, 1) * 100 : __num(x.confidence, 100)),
    risk: __num(x.riskScore ?? x.risk, 0),
    interest: __num(x.interestingScore ?? x.interest, 0),
    tags,
    worker: x.sourceWorkerType || x.discoveredByTaskId || x.worker || "—",
    firstSeen: __time(x.firstSeenAt || x.firstSeen, Date.now()),
    lastSeen: __time(x.lastSeenAt || x.lastObservedAt || x.lastSeen, Date.now()),
    chain: [],
    urlParts,
    metadata: meta,
    parentCount: __num(x.parentCount, 0),
    childCount: __num(x.childCount, 0),
    findingCount: __num(x.findingCount, 0),
    artifactCount: __num(x.artifactCount, 0),
  };
}

function __normalizeTask(x) {
  const asset = ASSETS.find(a => a.id === String(x.inputAssetId) || a.assetId === x.inputAssetId);
  const state = String(x.state || x.status || "Queued");
  const started = __time(x.startedAt || x.createdAt, Date.now());
  const completed = x.completedAt ? __time(x.completedAt) : null;
  return {
    id: String(x.taskId || x.id),
    taskId: x.taskId || x.id,
    type: x.workerCapability || x.taskType || x.type || "Worker",
    worker: x.leaseOwner || x.workerId || "—",
    assetId: String(x.inputAssetId || asset?.id || ""),
    assetValue: asset?.value || x.inputPayloadJson || "—",
    assetType: asset?.type || x.requiredAssetType || "Asset",
    state,
    progress: __num(x.progressPercent ?? x.progress, state === "Succeeded" ? 100 : 0),
    attempt: __num(x.attempt, 0),
    maxAttempts: __num(x.maxAttempts, 3),
    duration: completed ? Math.max(0, completed - started) : Math.max(0, Date.now() - started),
    startedAt: started,
    checkpointJson: x.checkpointJson || null,
    errorCode: x.errorCode || x.errorMessage || null,
    priority: x.priority || "Normal",
  };
}

function __normalizeEvent(x) {
  const payload = x.payload || {};
  const assetId = payload.assetId || payload.AssetId || payload.asset?.id || x.assetId;
  const asset = ASSETS.find(a => a.id === String(assetId) || a.assetId === assetId);
  const type = String(x.eventType || x.type || "IntegrationEvent");
  return {
    id: String(x.eventId || x.id || `E-${Date.now().toString(16).toUpperCase()}`),
    type,
    color: __eventColor(type),
    worker: payload.workerId || payload.worker || x.sourceService || "—",
    assetId: String(assetId || asset?.id || ""),
    assetValue: payload.assetValue || payload.value || asset?.value || "—",
    assetType: __assetType(payload.assetType || asset?.type || "Asset"),
    t: __time(x.occurredAt || x.observedAt || x.t, Date.now()),
    payload,
  };
}

function __normalizeWorker(x) {
  const type = String(x.workerType || x.type || "Worker");
  const running = __num(x.runningTasks ?? x.running, 0);
  const concurrency = __num(x.maxConcurrency ?? x.concurrency, 1);
  const isOnline = x.isOnline !== false && x.status !== "Stopped";
  return {
    id: String(x.workerId || x.id || `${type}-${Math.random().toString(36).slice(2, 6)}`),
    type,
    color: __workerColor(type),
    status: !isOnline ? "Stopped" : running > 0 ? "Busy" : "Healthy",
    running,
    concurrency,
    throughput: __num(x.throughput, running * 2),
    errRate: __num(x.errRate, 0),
    lastSeen: __time(x.lastSeenAt || x.lastHeartbeatAt || x.lastSeen, Date.now()),
    version: x.version || "—",
    hist: Array.from({ length: 12 }, (_, i) => Math.max(0, running + ((i % 3) - 1))),
  };
}

function __normalizeProgram(x) {
  const scopes = __arr(x.scopes);
  const programId = x.programId || x.id;
  return {
    id: String(programId || x.name),
    name: String(x.name || x.source || "Program"),
    scopes: scopes.length,
    assets: ASSETS.filter(a => !programId || a.programId === programId).length,
    findings: ASSETS.filter(a => a.type === "FindingCandidate" && (!programId || a.programId === programId)).length,
    status: "active",
  };
}

function __normalizeAgent(x) {
  const id = String(x.agentId || x.id);
  const tool = x.tool || x.cli || "claude";
  return {
    id,
    agentId: x.agentId || x.id,
    name: x.name || id,
    role: x.role || "development",
    cli: tool,
    model: x.model || AGENT_MODELS[tool]?.[0] || "default",
    enabled: !/disabled|paused/i.test(x.status || ""),
    workStatus: x.workStatus || (/disabled|paused/i.test(x.status || "") ? "idle" : "idle"),
    currentTask: x.currentTaskId || null,
    currentDescription: null,
    pid: null,
    cwd: "/srv/argus",
    startedAt: __time(x.createdAt, Date.now()),
    lastHeartbeat: __time(x.lastHeartbeatAt, Date.now()),
    lastError: x.lastError || null,
    promptPreview: (x.responsibilities || []).join("; ") || "Agent managed by Argus AgentService.",
    attempts: 0,
    tokensToday: 0,
    costToday: 0,
    runs24h: 0,
    successRate: 0,
    recurring: [],
    history: [],
  };
}

function __normalizeAgentTask(x) {
  return {
    id: String(x.taskId || x.id),
    description: x.description || "Agent task",
    priority: String(x.priority || "medium").toLowerCase(),
    status: String(x.status || "pending").toLowerCase(),
    assignedTo: x.assignedTo || null,
    kind: x.kind || "feature",
    createdAt: __time(x.createdAt, Date.now()),
    claimedAt: x.claimedAt ? __time(x.claimedAt) : null,
    completedAt: x.completedAt ? __time(x.completedAt) : null,
    attempts: __num(x.attempts, 0),
    prompt: x.recoveryContext || `Implement: ${x.description || "Agent task"}`,
    blockedReason: /blocked/i.test(x.status || "") ? (x.recoveryContext || "Blocked") : null,
  };
}

async function __getJson(path, opts) {
  const res = await fetch(path, {
    headers: { "Accept": "application/json", ...(opts?.headers || {}) },
    ...opts,
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch (e) { data = { message: text }; }
  if (!res.ok) {
    const err = new Error(data?.detail || data?.title || data?.message || `${res.status} ${res.statusText}`);
    err.status = res.status;
    err.data = data;
    throw err;
  }
  return data;
}

async function loadInitialState() {
  const state = await __getJson("/ui/state");
  const assets = __arr(state.assets).map(__normalizeAsset);
  if (assets.length) __replaceArray(ASSETS, assets);

  const programs = __arr(state.programs).map(__normalizeProgram);
  if (programs.length || state.programs) __replaceArray(TARGETS, programs.length ? programs : TARGETS);

  const tasks = __arr(state.tasks).map(__normalizeTask);
  if (tasks.length) __replaceArray(TASKS, tasks);

  const events = __arr(state.events).map(__normalizeEvent).sort((a, b) => b.t - a.t);
  if (events.length) __replaceArray(EVENTS, events);

  const workers = __arr(state.workers).map(__normalizeWorker);
  if (workers.length) __replaceArray(WORKER_INSTANCES, workers);

  try {
    const agents = __arr(await __getJson("/ui/agents")).map(__normalizeAgent);
    if (agents.length) __replaceArray(AGENTS, agents);
  } catch (e) {
    console.warn("Unable to load agents", e);
  }

  try {
    const agentTasks = __arr(await __getJson("/ui/agent-tasks")).map(__normalizeAgentTask);
    if (agentTasks.length) __replaceArray(AGENT_TASKS, agentTasks);
  } catch (e) {
    console.warn("Unable to load agent tasks", e);
  }

  return {
    generatedAt: state.generatedAt || new Date().toISOString(),
    assets: ASSETS.length,
    tasks: TASKS.length,
    events: EVENTS.length,
    workers: WORKER_INSTANCES.length,
    agents: AGENTS.length,
    agentTasks: AGENT_TASKS.length,
  };
}

async function sendOpsRequest(req) {
  return await __getJson("/ui/ops/send", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
}

async function enqueueAssetAction(asset, action) {
  const actionMap = {
    "enum-sub": ["DomainDiscovery", "Subfinder"],
    "probe": ["HttpProbe", "HttpProbe"],
    "spider": ["HtmlSpider", "HtmlDomSpider"],
    "spider-hl": ["HeadlessSpider", "HeadlessSpider"],
    "extract": ["JsExtract", "JsExtractor"],
    "fingerprint": ["Fingerprint", "Fingerprint"],
    "fuzz": ["WordlistDiscovery", "WordlistDiscovery"],
    "port-scan": ["PortScan", "PortScanner"],
  };
  const [taskType, workerCapability] = actionMap[action] || [action, action];
  if (!asset?.assetId || !__ARGUS_GUID_RE.test(String(asset.assetId))) {
    throw new Error("This action requires a live backend asset id; sample rows cannot be enqueued.");
  }
  return await __getJson("/ui/assets/bulk/enqueue", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      assetIds: [asset.assetId],
      taskType,
      workerCapability,
      programId: asset.programId,
      scopeId: asset.scopeId || null,
      maxAttempts: 3,
      priority: "Normal",
    }),
  });
}

Object.assign(window, { ArgusApi: { loadInitialState, sendOpsRequest, enqueueAssetAction } });
