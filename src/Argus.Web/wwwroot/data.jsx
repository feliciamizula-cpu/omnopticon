/* eslint-disable */
// Mock recon data for ArgusEngine console.

const ASSET_TYPES = [
  { id: "Domain",        glyph: "▣", color: "cyan" },
  { id: "Subdomain",     glyph: "▤", color: "cyan" },
  { id: "Ip",            glyph: "◉", color: "violet" },
  { id: "Cidr",          glyph: "◎", color: "violet" },
  { id: "Url",           glyph: "↗", color: "fg-1" },
  { id: "HtmlPage",      glyph: "❐", color: "fg-1" },
  { id: "JavaScriptFile",glyph: "ƒ", color: "amber" },
  { id: "CssFile",       glyph: "≋", color: "fg-2" },
  { id: "JsonDocument",  glyph: "{}", color: "amber" },
  { id: "ApiEndpoint",   glyph: "⌬", color: "magenta" },
  { id: "Port",          glyph: "▷", color: "violet" },
  { id: "Technology",    glyph: "⌘", color: "fg-1" },
  { id: "FindingCandidate", glyph: "⚑", color: "red" },
  { id: "DnsRecord",     glyph: "ɴ", color: "fg-2" },
];

const STATUSES = [
  { id: "InScope",    color: "green" },
  { id: "New",        color: "amber" },
  { id: "Active",     color: "cyan" },
  { id: "OutOfScope", color: "dim" },
  { id: "Archived",   color: "dim" },
  { id: "Error",      color: "red" },
];

const WORKER_TYPES = [
  { id: "Subfinder",        cat: "Discovery",   color: "cyan",    inputs: ["Domain"], outputs: ["Subdomain"] },
  { id: "Amass",            cat: "Discovery",   color: "cyan",    inputs: ["Domain"], outputs: ["Subdomain", "Ip"] },
  { id: "DnsResolver",      cat: "Resolution",  color: "violet",  inputs: ["Subdomain"], outputs: ["Ip", "DnsRecord"] },
  { id: "HttpProbe",        cat: "Resolution",  color: "violet",  inputs: ["Subdomain", "Url"], outputs: ["HttpResponse", "HtmlPage"] },
  { id: "HtmlDomSpider",    cat: "Spider",      color: "amber",   inputs: ["HtmlPage"], outputs: ["Url", "JavaScriptFile"] },
  { id: "HeadlessSpider",   cat: "Spider",      color: "amber",   inputs: ["HtmlPage"], outputs: ["Url", "ApiEndpoint"] },
  { id: "JsExtractor",      cat: "Spider",      color: "amber",   inputs: ["JavaScriptFile"], outputs: ["ApiEndpoint", "Url"] },
  { id: "WordlistDiscovery",cat: "Brute",       color: "magenta", inputs: ["Url"], outputs: ["Url", "HtmlPage"] },
  { id: "Fingerprint",      cat: "Analysis",    color: "green",   inputs: ["HtmlPage"], outputs: ["Technology"] },
  { id: "AssetScoring",     cat: "Analysis",    color: "green",   inputs: ["*"], outputs: [] },
  { id: "SecretCandidate",  cat: "Analysis",    color: "red",     inputs: ["JavaScriptFile", "HtmlPage"], outputs: ["FindingCandidate"] },
  { id: "PatternMatch",     cat: "Analysis",    color: "red",     inputs: ["HtmlPage", "JavaScriptFile"], outputs: ["FindingCandidate"] },
];

const TAGS = ["api", "admin", "auth", "internal", "graphql", "v1", "v2", "staging", "prod", "legacy", "swagger", "s3", "github", "stripe", "aws"];

// Deterministic PRNG so layout doesn't change between renders.
function mulberry32(seed) {
  return function () {
    let t = (seed += 0x6d2b79f5);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const R = mulberry32(91827364);
const pick = (arr) => arr[Math.floor(R() * arr.length)];
const rint = (a, b) => Math.floor(R() * (b - a + 1)) + a;

// ---- targets ----
const TARGETS = [
  { id: "tgt_chronix", name: "chronix.io",       scopes: 8, assets: 14283, findings: 47, status: "active" },
  { id: "tgt_neonfin", name: "neonfinance.com",  scopes: 4, assets:  6712, findings: 12, status: "active" },
  { id: "tgt_aurora",  name: "aurora-labs.dev",  scopes: 3, assets:  1923, findings:  3, status: "paused" },
  { id: "tgt_helio",   name: "helio.aero",       scopes: 6, assets:  8044, findings: 21, status: "active" },
];

// ---- generate assets for chronix.io ----
const ROOTS = ["chronix.io"];
const SUBS = [
  "api", "api-v2", "admin", "app", "auth", "billing", "cdn", "dashboard",
  "dev", "docs", "graphql", "internal", "ldap", "legacy", "mail", "monitor",
  "preview", "prod", "qa", "secure", "staging", "static", "status", "support",
  "test", "vault", "vpn", "webhooks", "ws", "old-admin", "edge-01", "edge-02",
];
const PATHS = [
  "/", "/login", "/api/v1/users", "/api/v1/users/{id}", "/api/v2/orders",
  "/api/v2/orders/export", "/admin/panel", "/admin/users", "/admin/audit",
  "/.well-known/security.txt", "/robots.txt", "/sitemap.xml", "/healthz",
  "/graphql", "/oauth/authorize", "/oauth/token", "/internal/debug",
  "/_next/static/chunks/main.bundle.js", "/static/js/app.4f3a2b.js",
  "/static/js/vendor.b771.js", "/static/css/app.css", "/api/v1/swagger.json",
  "/api/internal/metrics", "/wp-admin/", "/.git/config", "/.env",
  "/api/v1/export?format=csv", "/dashboard?token=xxx", "/files/report-2024.pdf",
];

function makeUrl(host, p) {
  const scheme = R() < 0.85 ? "https" : "http";
  return { full: `${scheme}://${host}${p}`, scheme, host, path: p };
}

function genAssets() {
  let out = [];
  let id = 1000;
  // Domain
  out.push({
    id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
    type: "Domain",
    value: "chronix.io",
    parent: null,
    parentValue: null,
    status: "InScope",
    scope: "InScope",
    confidence: 100,
    risk: 5,
    interest: 30,
    tags: ["prod"],
    worker: "Manual",
    firstSeen: Date.now() - 86400000 * 41,
    lastSeen: Date.now() - 60000,
    chain: [],
  });
  // Subdomains
  const subAssets = [];
  for (const s of SUBS) {
    const host = `${s}.chronix.io`;
    const confidence = rint(60, 100);
    const status = R() < 0.7 ? "InScope" : R() < 0.5 ? "Active" : R() < 0.4 ? "OutOfScope" : "New";
    const tags = [];
    if (s.includes("admin")) tags.push("admin");
    if (s.includes("api")) tags.push("api");
    if (s.includes("graphql")) tags.push("graphql");
    if (s.includes("internal") || s.includes("vault") || s.includes("ldap")) tags.push("internal");
    if (s.includes("staging") || s.includes("qa") || s.includes("test") || s.includes("dev")) tags.push("staging");
    if (s.includes("old") || s.includes("legacy")) tags.push("legacy");
    const risk = (tags.includes("admin") ? 60 : 10) + (tags.includes("internal") ? 30 : 0) + (tags.includes("legacy") ? 20 : 0) + rint(0, 15);
    const interest = (tags.includes("admin") || tags.includes("api") ? 70 : 25) + (tags.includes("graphql") ? 20 : 0) + rint(0, 15);
    const a = {
      id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
      type: "Subdomain",
      value: host,
      parent: "A-00000",
      parentValue: "chronix.io",
      status,
      scope: status === "OutOfScope" ? "OutOfScope" : "InScope",
      confidence,
      risk: Math.min(100, risk),
      interest: Math.min(100, interest),
      tags,
      worker: R() < 0.6 ? "Subfinder" : "Amass",
      firstSeen: Date.now() - rint(60, 3500) * 60000,
      lastSeen: Date.now() - rint(10, 240) * 60000,
      chain: ["chronix.io"],
    };
    out.push(a);
    subAssets.push(a);
  }
  // IPs
  for (let i = 0; i < 8; i++) {
    const ip = `${rint(34, 204)}.${rint(0, 255)}.${rint(0, 255)}.${rint(0, 255)}`;
    const parent = pick(subAssets);
    out.push({
      id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
      type: "Ip",
      value: ip,
      parent: parent.id,
      parentValue: parent.value,
      status: "Active",
      scope: "InScope",
      confidence: rint(85, 100),
      risk: rint(0, 40),
      interest: rint(20, 60),
      tags: ["aws"],
      worker: "DnsResolver",
      firstSeen: Date.now() - rint(30, 2000) * 60000,
      lastSeen: Date.now() - rint(5, 120) * 60000,
      chain: ["chronix.io", parent.value],
    });
  }
  // URLs / Pages
  const urlAssets = [];
  for (let i = 0; i < 24; i++) {
    const sub = pick(subAssets.slice(0, 14));
    const p = pick(PATHS);
    const u = makeUrl(sub.value, p);
    const t = p.endsWith(".js") ? "JavaScriptFile" : p.endsWith(".css") ? "CssFile" : p.endsWith(".json") ? "JsonDocument" : p.includes("/api/") ? "ApiEndpoint" : "HtmlPage";
    const tags = [...sub.tags];
    if (p.includes("admin")) tags.push("admin");
    if (p.includes("/api/")) tags.push("api");
    if (p.includes("v1")) tags.push("v1");
    if (p.includes("v2")) tags.push("v2");
    if (p.includes("swagger")) tags.push("swagger");
    if (p.includes(".git") || p.includes(".env")) tags.push("legacy");
    const risk = sub.risk + (p.includes(".git") || p.includes(".env") ? 50 : 0) + (p.includes("admin") ? 20 : 0) + (p.includes("swagger") ? 15 : 0);
    const interest = sub.interest + (t === "ApiEndpoint" ? 25 : 0) + (p.includes("swagger") || p.includes("graphql") ? 30 : 0);
    const status = t === "ApiEndpoint" && R() < 0.4 ? "New" : R() < 0.7 ? "Active" : "InScope";
    const a = {
      id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
      type: t,
      value: u.full,
      urlParts: u,
      parent: sub.id,
      parentValue: sub.value,
      status,
      scope: "InScope",
      confidence: rint(70, 100),
      risk: Math.min(100, risk),
      interest: Math.min(100, interest),
      tags: [...new Set(tags)],
      worker: t === "HtmlPage" ? "HttpProbe" : t === "JavaScriptFile" ? "HtmlDomSpider" : t === "ApiEndpoint" ? "JsExtractor" : "HttpProbe",
      firstSeen: Date.now() - rint(10, 1500) * 60000,
      lastSeen: Date.now() - rint(1, 60) * 60000,
      chain: ["chronix.io", sub.value],
      httpStatus: pick([200, 200, 200, 200, 301, 302, 401, 403, 404, 500]),
      contentLength: rint(120, 248000),
    };
    out.push(a);
    urlAssets.push(a);
  }
  // FindingCandidates
  const findings = [
    { v: "AWS_ACCESS_KEY=AKIA****", risk: 95, int: 95, tag: "aws", source: "/static/js/app.4f3a2b.js" },
    { v: "Stripe sk_live_*** key in client bundle", risk: 92, int: 95, tag: "stripe", source: "/static/js/vendor.b771.js" },
    { v: "Swagger UI exposed", risk: 60, int: 80, tag: "swagger", source: "/api/v1/swagger.json" },
    { v: ".git/config publicly readable", risk: 88, int: 90, tag: "legacy", source: "/.git/config" },
    { v: "Open GraphQL introspection", risk: 55, int: 85, tag: "graphql", source: "/graphql" },
    { v: "Verbose error reveals stack trace", risk: 40, int: 60, tag: "internal", source: "/api/v2/orders/export" },
    { v: "Internal admin panel publicly reachable", risk: 75, int: 85, tag: "admin", source: "/admin/panel" },
    { v: "Legacy WordPress fingerprint", risk: 50, int: 55, tag: "legacy", source: "/wp-admin/" },
  ];
  for (const f of findings) {
    const parent = urlAssets.find(u => u.urlParts && u.urlParts.path === f.source) || pick(urlAssets);
    out.push({
      id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
      type: "FindingCandidate",
      value: f.v,
      parent: parent.id,
      parentValue: parent.value,
      status: "New",
      scope: "InScope",
      confidence: rint(75, 99),
      risk: f.risk,
      interest: f.int,
      tags: [f.tag, "hot"],
      worker: f.tag === "swagger" || f.tag === "graphql" ? "PatternMatch" : "SecretCandidate",
      firstSeen: Date.now() - rint(2, 30) * 60000,
      lastSeen: Date.now() - rint(1, 10) * 60000,
      chain: ["chronix.io", parent.parentValue, parent.value],
    });
  }
  // Technologies
  const techs = ["nginx/1.24", "Next.js 14.2", "Cloudflare", "React 18", "PostgreSQL", "Stripe.js", "Auth0", "AWS S3"];
  for (const t of techs) {
    const sub = pick(subAssets.slice(0, 10));
    out.push({
      id: "A-" + (id++).toString(36).toUpperCase().padStart(5, "0"),
      type: "Technology",
      value: t,
      parent: sub.id,
      parentValue: sub.value,
      status: "Active",
      scope: "InScope",
      confidence: rint(80, 100),
      risk: rint(0, 30),
      interest: rint(30, 60),
      tags: [],
      worker: "Fingerprint",
      firstSeen: Date.now() - rint(60, 2000) * 60000,
      lastSeen: Date.now() - rint(5, 60) * 60000,
      chain: ["chronix.io", sub.value],
    });
  }
  return out.sort((a, b) => b.interest - a.interest);
}

const ASSETS = genAssets();

// ---- workers (instances) ----
const WORKER_INSTANCES = [];
let widCounter = 1;
for (const wt of WORKER_TYPES) {
  const n = rint(1, 4);
  for (let i = 0; i < n; i++) {
    const status = R() < 0.7 ? "Healthy" : R() < 0.5 ? "Busy" : R() < 0.4 ? "Degraded" : "Healthy";
    WORKER_INSTANCES.push({
      id: `w-${wt.id.toLowerCase()}-${(widCounter++).toString().padStart(3, "0")}`,
      type: wt.id,
      cat: wt.cat,
      color: wt.color,
      hostname: `argus-w${rint(1, 8)}-${String.fromCharCode(97 + rint(0, 12))}`,
      status,
      running: rint(0, 4),
      concurrency: rint(2, 8),
      version: "1.4.2",
      uptime: rint(120, 86400),
      processed: rint(140, 28000),
      errRate: R() * 4,
      throughput: rint(2, 38),
      heartbeat: rint(1, 12),
      inputs: wt.inputs,
      outputs: wt.outputs,
      // sparkline data
      hist: Array.from({ length: 30 }, () => rint(1, 9)),
    });
  }
}

// ---- task runs (recent) ----
const TASK_STATES = ["Queued", "Leased", "Running", "Checkpointed", "Succeeded", "Failed", "RetryPending", "HeartbeatLost"];
function genTasks() {
  const out = [];
  for (let i = 0; i < 220; i++) {
    const w = pick(WORKER_INSTANCES);
    const a = pick(ASSETS);
    const state = pick(TASK_STATES);
    out.push({
      id: `T-${(0xa10000 + i).toString(16).toUpperCase()}`,
      type: w.type,
      worker: w.id,
      assetId: a.id,
      assetValue: a.value,
      assetType: a.type,
      state,
      progress: state === "Running" ? rint(5, 95) : state === "Succeeded" ? 100 : state === "Checkpointed" ? rint(40, 80) : 0,
      attempt: rint(1, 3),
      maxAttempts: 3,
      duration: rint(80, 14000),
      startedAt: Date.now() - rint(60, 3600) * 1000,
      checkpointJson: state === "Checkpointed" ? `{"cursor":${rint(120, 8800)},"page":${rint(2, 22)}}` : null,
      errorCode: state === "Failed" ? pick(["ETIMEDOUT", "ECONNRESET", "RATE_LIMIT", "DNS_NXDOMAIN", "HTTP_5XX"]) : null,
    });
  }
  return out;
}
const TASKS = genTasks();

// ---- events ----
const EVENT_TYPES = [
  { id: "AssetDiscovered",       color: "cyan",   tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "AssetConfirmed",        color: "green",  tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "AssetHighValueMarked",  color: "amber",  tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "WorkerTaskStarted",     color: "violet", tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "WorkerTaskCheckpointed",color: "violet", tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "WorkerTaskCompleted",   color: "green",  tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "WorkerTaskFailed",      color: "red",    tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "FindingCreated",        color: "red",    tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
  { id: "AssetOutOfScope",       color: "amber",  tmpl: (w, a) => ({ worker: w.worker || "—", host: a.value, kind: a.type }) },
];

function genEvents(n) {
  const out = [];
  const t0 = Date.now() - 60_000;
  for (let i = 0; i < n; i++) {
    const et = pick(EVENT_TYPES);
    const a = pick(ASSETS);
    out.push({
      id: `E-${(0xff0000 - i).toString(16).toUpperCase()}`,
      type: et.id,
      color: et.color,
      worker: pick(WORKER_INSTANCES).id,
      assetId: a.id,
      assetValue: a.value,
      assetType: a.type,
      t: t0 - i * rint(120, 1800),
    });
  }
  return out;
}
const EVENTS = genEvents(160);

// expose
Object.assign(window, {
  ASSET_TYPES, STATUSES, WORKER_TYPES, WORKER_INSTANCES, TARGETS, ASSETS, TASKS, EVENTS, EVENT_TYPES, TAGS
});
