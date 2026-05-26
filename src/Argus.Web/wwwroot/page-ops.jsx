/* eslint-disable */
// OPERATIONS PAGE — manual recon workspace with asset browser,
// context menus, request editor, response viewer, history & diff.

// ── Mock HTTP data ──────────────────────────────────────────────────
const MOCK_REQUESTS = {
  "https://api.chronix.io/api/v1/users": {
    method: "GET",
    url: "https://api.chronix.io/api/v1/users",
    headers: [
      ["Host", "api.chronix.io"],
      ["Authorization", "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyMSJ9..."],
      ["Accept", "application/json"],
      ["User-Agent", "Mozilla/5.0 (compatible; ArgusEngine/0.7)"],
    ],
    body: "",
  },
  "https://api.chronix.io/api/v2/orders": {
    method: "POST",
    url: "https://api.chronix.io/api/v2/orders",
    headers: [
      ["Host", "api.chronix.io"],
      ["Content-Type", "application/json"],
      ["Authorization", "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyMSJ9..."],
    ],
    body: JSON.stringify({ customer_id: 42, items: [{ sku: "PRD-001", qty: 1 }] }, null, 2),
  },
  "https://api.chronix.io/graphql": {
    method: "POST",
    url: "https://api.chronix.io/graphql",
    headers: [
      ["Host", "api.chronix.io"],
      ["Content-Type", "application/json"],
    ],
    body: JSON.stringify({ query: "{ __schema { types { name } } }" }, null, 2),
  },
  "https://admin.chronix.io/admin/panel": {
    method: "GET",
    url: "https://admin.chronix.io/admin/panel",
    headers: [
      ["Host", "admin.chronix.io"],
      ["Cookie", "session=abc123; csrf=tok456"],
    ],
    body: "",
  },
};

const MOCK_RESPONSES = {
  "https://api.chronix.io/api/v1/users": {
    status: 200, statusText: "OK",
    headers: [
      ["Content-Type", "application/json"],
      ["X-Request-Id", "req_9f3a2b"],
      ["X-RateLimit-Remaining", "97"],
    ],
    body: JSON.stringify([
      { id: 1, email: "admin@chronix.io", role: "admin" },
      { id: 2, email: "bob@chronix.io", role: "user" },
    ], null, 2),
    time: 124,
    size: 312,
  },
  "https://api.chronix.io/api/v2/orders": {
    status: 201, statusText: "Created",
    headers: [["Content-Type", "application/json"], ["Location", "/api/v2/orders/8821"]],
    body: JSON.stringify({ id: 8821, status: "pending" }, null, 2),
    time: 214,
    size: 58,
  },
  "https://api.chronix.io/graphql": {
    status: 200, statusText: "OK",
    headers: [["Content-Type", "application/json"]],
    body: JSON.stringify({ data: { __schema: { types: [{ name: "Query" }, { name: "User" }, { name: "Order" }] } } }, null, 2),
    time: 84,
    size: 192,
  },
  "https://admin.chronix.io/admin/panel": {
    status: 302, statusText: "Found",
    headers: [["Location", "/login"], ["Set-Cookie", "csrf=new456; SameSite=Strict"]],
    body: "",
    time: 12,
    size: 0,
  },
};

// Context menu config by asset type
const CONTEXT_MENUS = {
  Domain: [
    { id: "enum-sub",  label: "Enumerate Subdomains", icon: "▤", tone: "cyan" },
    { id: "probe",     label: "Probe HTTP",            icon: "◉", tone: "" },
    { id: "scope",     label: "Add to Scope",          icon: "✓", tone: "green" },
    { id: "sep" },
    { id: "view",      label: "View Assets",           icon: "↗", tone: "" },
  ],
  Subdomain: [
    { id: "probe",     label: "Probe HTTP",            icon: "◉", tone: "" },
    { id: "enum-sub",  label: "Enumerate Subdomains",  icon: "▤", tone: "cyan" },
    { id: "fingerprint", label: "Fingerprint Tech",    icon: "⌘", tone: "" },
    { id: "sep" },
    { id: "spider",    label: "Spider Site",           icon: "↗", tone: "amber" },
    { id: "scope",     label: "Mark Out-of-Scope",     icon: "✗", tone: "red" },
  ],
  Url: [
    { id: "open-req",  label: "Open Request",          icon: "↗", tone: "cyan" },
    { id: "resend",    label: "Send to Editor",        icon: "✎", tone: "" },
    { id: "spider",    label: "Spider",                icon: "⌂", tone: "amber" },
    { id: "sep" },
    { id: "copy-url",  label: "Copy URL",              icon: "⎘", tone: "" },
    { id: "scope",     label: "Mark Out-of-Scope",     icon: "✗", tone: "red" },
  ],
  HtmlPage: [
    { id: "open-req",  label: "Open Request",          icon: "↗", tone: "cyan" },
    { id: "resend",    label: "Send to Editor",        icon: "✎", tone: "" },
    { id: "spider",    label: "Spider (DOM)",          icon: "⌂", tone: "amber" },
    { id: "spider-hl", label: "Spider (Headless)",     icon: "⌂", tone: "amber" },
    { id: "sep" },
    { id: "scope",     label: "Mark Out-of-Scope",     icon: "✗", tone: "red" },
  ],
  JavaScriptFile: [
    { id: "open-req",  label: "Open Request",          icon: "↗", tone: "cyan" },
    { id: "resend",    label: "Send to Editor",        icon: "✎", tone: "" },
    { id: "extract",   label: "Extract Endpoints",     icon: "⌬", tone: "magenta" },
    { id: "sep" },
    { id: "download",  label: "Download File",         icon: "⬇", tone: "" },
  ],
  ApiEndpoint: [
    { id: "open-req",  label: "Open Request",          icon: "↗", tone: "cyan" },
    { id: "resend",    label: "Send to Editor",        icon: "✎", tone: "amber" },
    { id: "fuzz",      label: "Fuzz Parameters",       icon: "⚡", tone: "magenta" },
    { id: "sep" },
    { id: "copy-url",  label: "Copy URL",              icon: "⎘", tone: "" },
    { id: "copy-curl", label: "Copy as cURL",          icon: "⎘", tone: "" },
  ],
  FindingCandidate: [
    { id: "open-req",  label: "Open Request",          icon: "↗", tone: "cyan" },
    { id: "confirm",   label: "Mark Confirmed",        icon: "✓", tone: "red" },
    { id: "dismiss",   label: "Dismiss",               icon: "✗", tone: "" },
    { id: "sep" },
    { id: "export",    label: "Export Evidence",       icon: "⬇", tone: "" },
  ],
  Technology: [
    { id: "search",    label: "Search CVEs",           icon: "⚑", tone: "red" },
    { id: "view",      label: "View All Instances",    icon: "↗", tone: "" },
  ],
  Ip: [
    { id: "port-scan", label: "Port Scan",             icon: "▷", tone: "amber" },
    { id: "rdns",      label: "Reverse DNS",           icon: "ɴ", tone: "" },
    { id: "sep" },
    { id: "scope",     label: "Mark Out-of-Scope",     icon: "✗", tone: "red" },
  ],
};
const DEFAULT_CONTEXT = [
  { id: "view",    label: "View Asset",    icon: "↗", tone: "" },
  { id: "copy",    label: "Copy Value",   icon: "⎘", tone: "" },
  { id: "scope",   label: "Mark OOS",     icon: "✗", tone: "red" },
];

// ── HTTP types (have real request data) ─────────────────────────────
const HTTP_TYPES = new Set(["Url", "HtmlPage", "JavaScriptFile", "ApiEndpoint", "FindingCandidate"]);

// ── Diff util ────────────────────────────────────────────────────────
function diffLines(a, b) {
  const la = a.split("\n");
  const lb = b.split("\n");
  const max = Math.max(la.length, lb.length);
  return Array.from({ length: max }, (_, i) => {
    const al = la[i] ?? "";
    const bl = lb[i] ?? "";
    if (al === bl) return { kind: "same", a: al, b: bl };
    if (!la[i]) return { kind: "added", a: "", b: bl };
    if (!lb[i]) return { kind: "removed", a: al, b: "" };
    return { kind: "changed", a: al, b: bl };
  });
}

// ═══════════════════════════════════════════════════════════════════
// MAIN PAGE
// ═══════════════════════════════════════════════════════════════════

function OpsPage() {
  const [selectedTarget, setSelectedTarget] = useState(TARGETS[0]);
  const [selectedAsset, setSelectedAsset] = useState(null);
  const [contextMenu, setContextMenu] = useState(null); // { x, y, asset }
  const [activeTab, setActiveTab] = useState("editor"); // editor | history | diff
  const [workspace, setWorkspace] = useState(null); // current request being edited
  const [history, setHistory] = useState([]); // [{req, resp, ts}]
  const [compareA, setCompareA] = useState(null);
  const [compareB, setCompareB] = useState(null);

  // Close context menu on click outside
  useEffect(() => {
    const handler = () => setContextMenu(null);
    window.addEventListener("click", handler);
    return () => window.removeEventListener("click", handler);
  }, []);

  const openInEditor = (asset) => {
    const url = asset.urlParts?.full || asset.value;
    const req = MOCK_REQUESTS[url] || {
      method: "GET",
      url: url,
      headers: [["Host", new URL(url.startsWith("http") ? url : "https://" + url).hostname]],
      body: "",
    };
    setWorkspace({ req: JSON.parse(JSON.stringify(req)), resp: null, sending: false });
    setActiveTab("editor");
    setContextMenu(null);
  };

  const sendRequest = async () => {
    if (!workspace) return;
    setWorkspace(w => ({ ...w, sending: true }));
    try {
      const resp = window.ArgusApi?.sendOpsRequest
        ? await window.ArgusApi.sendOpsRequest(workspace.req)
        : (MOCK_RESPONSES[workspace.req.url] || {
            status: 200, statusText: "OK",
            headers: [["Content-Type", "text/plain"]],
            body: "OK", time: 88, size: 2,
          });
      const entry = { req: { ...workspace.req }, resp, ts: Date.now() };
      setHistory(h => [entry, ...h]);
      setWorkspace(w => ({ ...w, resp, sending: false }));
    } catch (err) {
      const resp = {
        status: err.status || 0,
        statusText: "Request failed",
        headers: [["Content-Type", "text/plain"]],
        body: err.message || "Unable to send request",
        time: 0,
        size: 0,
      };
      const entry = { req: { ...workspace.req }, resp, ts: Date.now() };
      setHistory(h => [entry, ...h]);
      setWorkspace(w => ({ ...w, resp, sending: false }));
    }
  };

  const handleContextAction = (action, asset) => {
    setContextMenu(null);
    if (action === "open-req" || action === "resend") {
      openInEditor(asset);
    } else if (action === "spider" || action === "enum-sub" || action === "probe" ||
               action === "spider-hl" || action === "extract" || action === "fingerprint" ||
               action === "fuzz" || action === "port-scan") {
      const label = CONTEXT_MENUS[asset.type]?.find(m => m.id === action)?.label || action;
      if (window.ArgusApi?.enqueueAssetAction) {
        window.ArgusApi.enqueueAssetAction(asset, action)
          .then(() => alert(`Queued: ${label} for ${asset.value}`))
          .catch((err) => alert(`${label}: ${err.message || "Unable to enqueue"}`));
      } else {
        alert(`[Simulated] Queued: ${label} for ${asset.value}`);
      }
    } else if (action === "copy-url" || action === "copy") {
      navigator.clipboard?.writeText(asset.value).catch(() => {});
    } else if (action === "copy-curl") {
      const req = MOCK_REQUESTS[asset.urlParts?.full || asset.value];
      if (req) {
        const headers = req.headers.map(([k,v]) => `-H "${k}: ${v}"`).join(" ");
        navigator.clipboard?.writeText(`curl -X ${req.method} ${req.url} ${headers}`).catch(() => {});
      }
    }
  };

  // Assets for selected target — build a tree grouped by type
  const targetAssets = useMemo(() => {
    // Filter to assets belonging to chronix.io scope (in a real app, filter by target)
    return ASSETS.filter(a => a.scope === "InScope").slice(0, 80);
  }, [selectedTarget]);

  const grouped = useMemo(() => {
    const g = {};
    for (const a of targetAssets) {
      (g[a.type] = g[a.type] || []).push(a);
    }
    return g;
  }, [targetAssets]);

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      {/* Tab bar */}
      <div className="page-tabs">
        <div className="page-tab active">
          <span>Operations</span>
          <span className="tab-count">Manual</span>
        </div>
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <span className="mono-label" style={{ marginRight: 6 }}>target</span>
          <select
            value={selectedTarget.id}
            onChange={e => setSelectedTarget(TARGETS.find(t => t.id === e.target.value))}
            style={{ background: "var(--bg-2)", border: "1px solid var(--line-2)", color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontSize: 11, padding: "1px 6px", height: 20 }}
          >
            {TARGETS.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </div>
      </div>

      {/* Body */}
      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>

        {/* LEFT: Asset browser */}
        <ResizablePanel id="ops-assets" side="left" defaultWidth={280} minWidth={140} maxWidth={480} label="Assets">
          <AssetBrowser
            grouped={grouped}
            selected={selectedAsset}
            onSelect={setSelectedAsset}
            onContextMenu={(e, asset) => {
              e.preventDefault();
              setContextMenu({ x: e.clientX, y: e.clientY, asset });
            }}
          />
        </ResizablePanel>

        {/* CENTER: Workspace */}
        <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0, minHeight: 0 }}>
          {/* Workspace tab bar */}
          <div style={{ display: "flex", background: "var(--bg-1)", borderBottom: "1px solid var(--line-1)", flexShrink: 0 }}>
            {[
              ["editor", "Request Editor"],
              ["history", `History (${history.length})`],
              ["diff", "Diff / Compare"],
            ].map(([id, label]) => (
              <div
                key={id}
                className={"page-tab " + (activeTab === id ? "active" : "")}
                onClick={() => setActiveTab(id)}
              >
                {label}
              </div>
            ))}
          </div>

          <div style={{ flex: 1, overflow: "hidden", display: "flex", flexDirection: "column" }}>
            {activeTab === "editor" && (
              <RequestEditor
                workspace={workspace}
                setWorkspace={setWorkspace}
                onSend={sendRequest}
              />
            )}
            {activeTab === "history" && (
              <HistoryPanel
                history={history}
                compareA={compareA}
                compareB={compareB}
                setCompareA={setCompareA}
                setCompareB={setCompareB}
                onRestore={(entry) => {
                  setWorkspace({ req: { ...entry.req }, resp: entry.resp, sending: false });
                  setActiveTab("editor");
                }}
                onOpenDiff={() => setActiveTab("diff")}
              />
            )}
            {activeTab === "diff" && (
              <DiffPanel compareA={compareA} compareB={compareB} />
            )}
          </div>
        </div>

        {/* RIGHT: Current response */}
        {workspace && (
          <ResizablePanel id="ops-response" side="right" defaultWidth={380} minWidth={200} maxWidth={640} label="Response">
            <ResponseViewer
              resp={workspace.resp}
              sending={workspace.sending}
              history={history}
              onCompare={(entry) => {
                if (!compareA) setCompareA(entry);
                else if (!compareB) { setCompareB(entry); setActiveTab("diff"); }
                else { setCompareA(entry); setCompareB(null); }
              }}
              onOpenHistory={() => setActiveTab("history")}
            />
          </ResizablePanel>
        )}
      </div>

      {/* Context menu */}
      {contextMenu && (
        <ContextMenuOverlay
          x={contextMenu.x}
          y={contextMenu.y}
          asset={contextMenu.asset}
          onAction={handleContextAction}
          onClose={() => setContextMenu(null)}
        />
      )}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════
// ASSET BROWSER
// ═══════════════════════════════════════════════════════════════════

function AssetBrowser({ grouped, selected, onSelect, onContextMenu }) {
  const [expanded, setExpanded] = useState(() => {
    const s = {};
    for (const k of ["ApiEndpoint", "HtmlPage", "JavaScriptFile", "Subdomain"]) s[k] = true;
    return s;
  });

  const typeOrder = ["Domain", "Subdomain", "Ip", "HtmlPage", "Url", "JavaScriptFile", "ApiEndpoint", "FindingCandidate", "Technology"];

  return (
    <div style={{ overflow: "auto", flex: 1, fontFamily: "var(--font-mono)", fontSize: 11 }}>
      {typeOrder.filter(t => grouped[t]?.length).map(type => {
        const assets = grouped[type] || [];
        const isOpen = expanded[type];
        const t = ASSET_TYPES.find(a => a.id === type) || { glyph: "·", color: "fg-2" };
        return (
          <div key={type}>
            {/* Type header */}
            <div
              onClick={() => setExpanded(e => ({ ...e, [type]: !e[type] }))}
              style={{
                display: "flex", alignItems: "center", gap: 6, padding: "4px 10px",
                background: "var(--bg-2)", borderBottom: "1px solid var(--line-1)",
                cursor: "pointer", userSelect: "none",
              }}
            >
              <span style={{ fontSize: 8, color: "var(--fg-3)" }}>{isOpen ? "▼" : "▶"}</span>
              <span style={{ color: `var(--${t.color === "fg-1" || t.color === "fg-2" ? "fg-2" : t.color})` }}>{t.glyph}</span>
              <span style={{ color: "var(--fg-1)", fontWeight: 600, letterSpacing: "0.06em", textTransform: "uppercase", fontSize: 10 }}>{type}</span>
              <span style={{ marginLeft: "auto", color: "var(--fg-3)", fontSize: 10 }}>{assets.length}</span>
            </div>
            {/* Asset rows */}
            {isOpen && assets.map(a => (
              <div
                key={a.id}
                onClick={() => onSelect(a)}
                onContextMenu={(e) => onContextMenu(e, a)}
                style={{
                  padding: "3px 10px 3px 22px",
                  borderBottom: "1px solid var(--line-0)",
                  cursor: "pointer",
                  background: selected?.id === a.id ? "var(--bg-3)" : "transparent",
                  boxShadow: selected?.id === a.id ? "inset 2px 0 0 var(--accent)" : "none",
                  display: "flex", alignItems: "center", gap: 6,
                }}
              >
                {/* Status dot */}
                <span className={"dot " + (a.status === "InScope" ? "green" : a.status === "New" ? "amber" : a.status === "Active" ? "cyan" : "")} />
                {/* Value */}
                <span style={{
                  flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
                  color: a.risk > 70 ? "var(--red)" : a.interest > 70 ? "var(--magenta)" : "var(--fg-1)",
                  fontSize: 10.5,
                }}>
                  {a.urlParts ? a.urlParts.path : a.value}
                </span>
                {/* Tags */}
                {a.tags.slice(0, 1).map(tag => (
                  <span key={tag} className={`tag-chip ${tag === "hot" ? "hot" : tag === "api" ? "api" : tag === "admin" ? "adm" : ""}`}>{tag}</span>
                ))}
                {/* HTTP icon if applicable */}
                {HTTP_TYPES.has(a.type) && (
                  <span style={{ color: "var(--fg-3)", fontSize: 9 }}>↗</span>
                )}
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════
// CONTEXT MENU
// ═══════════════════════════════════════════════════════════════════

function ContextMenuOverlay({ x, y, asset, onAction, onClose }) {
  const items = CONTEXT_MENUS[asset.type] || DEFAULT_CONTEXT;

  // Adjust position to stay in viewport
  const left = Math.min(x, window.innerWidth - 220);
  const top = Math.min(y, window.innerHeight - items.length * 26 - 20);

  return (
    <div
      style={{ position: "fixed", inset: 0, zIndex: 200 }}
      onClick={onClose}
      onContextMenu={(e) => { e.preventDefault(); onClose(); }}
    >
      <div
        style={{
          position: "absolute", left, top,
          background: "var(--bg-2)",
          border: "1px solid var(--line-3)",
          boxShadow: "0 8px 32px rgba(0,0,0,0.6)",
          minWidth: 210,
          zIndex: 201,
        }}
        onClick={e => e.stopPropagation()}
      >
        {/* Header */}
        <div style={{
          padding: "5px 10px", borderBottom: "1px solid var(--line-1)",
          fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--fg-3)",
          display: "flex", alignItems: "center", gap: 6,
        }}>
          <TypeGlyph type={asset.type} />
          <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", maxWidth: 160 }}>
            {asset.urlParts ? asset.urlParts.path : asset.value}
          </span>
        </div>
        {/* Menu items */}
        {items.map((item, i) => {
          if (item.id === "sep") return (
            <div key={i} style={{ height: 1, background: "var(--line-1)", margin: "2px 0" }} />
          );
          return (
            <div
              key={item.id}
              onClick={() => onAction(item.id, asset)}
              style={{
                padding: "5px 12px",
                fontFamily: "var(--font-mono)",
                fontSize: 11,
                color: item.tone === "red" ? "var(--red)" : item.tone === "cyan" ? "var(--cyan)" : item.tone === "amber" ? "var(--amber)" : item.tone === "green" ? "var(--green)" : item.tone === "magenta" ? "var(--magenta)" : "var(--fg-1)",
                cursor: "pointer",
                display: "flex",
                alignItems: "center",
                gap: 8,
              }}
              onMouseEnter={e => e.currentTarget.style.background = "var(--bg-3)"}
              onMouseLeave={e => e.currentTarget.style.background = "transparent"}
            >
              <span style={{ width: 14, textAlign: "center", flexShrink: 0 }}>{item.icon}</span>
              <span>{item.label}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════
// REQUEST EDITOR
// ═══════════════════════════════════════════════════════════════════

function RequestEditor({ workspace, setWorkspace, onSend }) {
  const [reqTab, setReqTab] = useState("headers"); // headers | body | params

  if (!workspace) {
    return (
      <div style={{
        flex: 1, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center",
        gap: 12, color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 12,
      }}>
        <div style={{ fontSize: 32 }}>↗</div>
        <div>Right-click an asset → <span style={{ color: "var(--accent)" }}>Send to Editor</span></div>
        <div style={{ fontSize: 10, color: "var(--fg-4)" }}>or select any HTTP asset from the browser</div>
      </div>
    );
  }

  const { req, sending } = workspace;
  const setReq = (patch) => setWorkspace(w => ({ ...w, req: { ...w.req, ...patch } }));

  const updateHeader = (i, key, val) => {
    const headers = [...req.headers];
    headers[i] = [key, val];
    setReq({ headers });
  };
  const addHeader = () => setReq({ headers: [...req.headers, ["", ""]] });
  const removeHeader = (i) => setReq({ headers: req.headers.filter((_, j) => j !== i) });

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minHeight: 0 }}>
      {/* URL bar */}
      <div style={{
        display: "flex", alignItems: "center", gap: 6,
        padding: "6px 10px", background: "var(--bg-1)", borderBottom: "1px solid var(--line-1)", flexShrink: 0,
      }}>
        <select
          value={req.method}
          onChange={e => setReq({ method: e.target.value })}
          style={{
            background: "var(--bg-2)", border: "1px solid var(--line-2)", color: "var(--amber)",
            fontFamily: "var(--font-mono)", fontWeight: 700, fontSize: 11, padding: "3px 6px", height: 24, flexShrink: 0,
          }}
        >
          {["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"].map(m => (
            <option key={m} value={m}>{m}</option>
          ))}
        </select>
        <input
          value={req.url}
          onChange={e => setReq({ url: e.target.value })}
          style={{
            flex: 1, background: "var(--bg-2)", border: "1px solid var(--line-2)", color: "var(--fg-0)",
            fontFamily: "var(--font-mono)", fontSize: 11.5, padding: "3px 8px", height: 24, outline: "none",
          }}
          placeholder="https://..."
        />
        <button
          className={"btn primary" + (sending ? "" : "")}
          style={{ height: 24, minWidth: 64, opacity: sending ? 0.6 : 1 }}
          onClick={onSend}
          disabled={sending}
        >
          {sending ? "Sending…" : "Send"}
        </button>
      </div>

      {/* Request tabs */}
      <div style={{ display: "flex", background: "var(--bg-1)", borderBottom: "1px solid var(--line-1)", flexShrink: 0 }}>
        {[["headers", `Headers (${req.headers.length})`], ["body", "Body"], ["params", "Params"]].map(([id, label]) => (
          <div
            key={id}
            className={"page-tab " + (reqTab === id ? "active" : "")}
            onClick={() => setReqTab(id)}
            style={{ fontSize: 10.5, padding: "0 12px" }}
          >
            {label}
          </div>
        ))}
      </div>

      {/* Request content */}
      <div style={{ flex: 1, overflow: "auto", background: "var(--bg-0)" }}>
        {reqTab === "headers" && (
          <div style={{ padding: 10 }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontFamily: "var(--font-mono)", fontSize: 11 }}>
              <thead>
                <tr>
                  {["Header Name", "Value", ""].map(h => (
                    <th key={h} style={{ textAlign: "left", padding: "3px 8px", fontSize: 9.5, color: "var(--fg-3)", borderBottom: "1px solid var(--line-1)", fontWeight: 500, letterSpacing: "0.12em", textTransform: "uppercase" }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {req.headers.map(([k, v], i) => (
                  <tr key={i} style={{ borderBottom: "1px solid var(--line-0)" }}>
                    <td style={{ padding: "2px 4px", width: "35%" }}>
                      <input
                        value={k} onChange={e => updateHeader(i, e.target.value, v)}
                        style={headerInputStyle}
                      />
                    </td>
                    <td style={{ padding: "2px 4px" }}>
                      <input
                        value={v} onChange={e => updateHeader(i, k, e.target.value)}
                        style={{ ...headerInputStyle, color: "var(--cyan)" }}
                      />
                    </td>
                    <td style={{ padding: "2px 4px", width: 24 }}>
                      <button onClick={() => removeHeader(i)} style={{ color: "var(--fg-3)", fontSize: 11 }}>×</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <button className="btn ghost tiny" style={{ marginTop: 8 }} onClick={addHeader}>+ Add Header</button>
          </div>
        )}
        {reqTab === "body" && (
          <textarea
            value={req.body}
            onChange={e => setReq({ body: e.target.value })}
            style={{
              width: "100%", height: "100%", minHeight: 200, background: "transparent", border: "none",
              color: "var(--fg-1)", fontFamily: "var(--font-mono)", fontSize: 11.5, padding: 12,
              outline: "none", resize: "none", lineHeight: 1.5,
            }}
            placeholder="Request body..."
          />
        )}
        {reqTab === "params" && (
          <div style={{ padding: 10, fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--fg-3)" }}>
            {/* Parse URL params */}
            {(() => {
              try {
                const u = new URL(req.url.startsWith("http") ? req.url : "https://x.com" + req.url);
                const params = [...u.searchParams.entries()];
                if (!params.length) return <div style={{ padding: 12 }}>No query parameters</div>;
                return (
                  <table style={{ width: "100%", borderCollapse: "collapse" }}>
                    <thead>
                      <tr>{["Key", "Value"].map(h => <th key={h} style={{ textAlign: "left", padding: "3px 8px", fontSize: 9.5, color: "var(--fg-3)", borderBottom: "1px solid var(--line-1)", fontWeight: 500, letterSpacing: "0.12em", textTransform: "uppercase" }}>{h}</th>)}</tr>
                    </thead>
                    <tbody>
                      {params.map(([k, v], i) => (
                        <tr key={i} style={{ borderBottom: "1px solid var(--line-0)" }}>
                          <td style={{ padding: "4px 8px", color: "var(--amber)" }}>{k}</td>
                          <td style={{ padding: "4px 8px", color: "var(--cyan)" }}>{v}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                );
              } catch { return <div style={{ padding: 12 }}>Invalid URL</div>; }
            })()}
          </div>
        )}
      </div>
    </div>
  );
}

const headerInputStyle = {
  width: "100%", background: "transparent", border: "none", outline: "none",
  fontFamily: "var(--font-mono)", fontSize: 11.5, color: "var(--fg-1)", padding: "3px 4px",
};

// ═══════════════════════════════════════════════════════════════════
// RESPONSE VIEWER
// ═══════════════════════════════════════════════════════════════════

function ResponseViewer({ resp, sending, history, onCompare, onOpenHistory }) {
  const [respTab, setRespTab] = useState("body");

  if (sending) return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 10, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>
      <div style={{ color: "var(--accent)", fontSize: 16 }}>⟳</div>
      <div style={{ fontSize: 11 }}>Sending request…</div>
    </div>
  );

  if (!resp) return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
      No response yet
    </div>
  );

  const statusColor = resp.status >= 500 ? "red" : resp.status >= 400 ? "amber" : resp.status >= 300 ? "cyan" : resp.status >= 200 ? "green" : "fg-2";

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minHeight: 0 }}>
      {/* Status bar */}
      <div style={{
        display: "flex", alignItems: "center", gap: 10, padding: "5px 10px",
        background: "var(--bg-2)", borderBottom: "1px solid var(--line-1)", flexShrink: 0,
        fontFamily: "var(--font-mono)", fontSize: 11,
      }}>
        <span style={{ fontWeight: 700, color: `var(--${statusColor})`, fontSize: 13 }}>
          {resp.status} {resp.statusText}
        </span>
        <span style={{ color: "var(--fg-3)" }}>·</span>
        <span style={{ color: "var(--fg-2)" }}>{resp.time}ms</span>
        <span style={{ color: "var(--fg-3)" }}>·</span>
        <span style={{ color: "var(--fg-2)" }}>{resp.size}B</span>
        <span style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
          {history.length > 0 && (
            <button className="btn ghost tiny" onClick={() => onCompare({ resp })}>
              + Compare
            </button>
          )}
          <button className="btn ghost tiny" onClick={onOpenHistory}>History</button>
        </span>
      </div>

      {/* Tabs */}
      <div style={{ display: "flex", background: "var(--bg-1)", borderBottom: "1px solid var(--line-1)", flexShrink: 0 }}>
        {[["body", "Body"], ["headers", `Headers (${resp.headers.length})`], ["raw", "Raw"]].map(([id, label]) => (
          <div
            key={id}
            className={"page-tab " + (respTab === id ? "active" : "")}
            onClick={() => setRespTab(id)}
            style={{ fontSize: 10.5, padding: "0 12px" }}
          >
            {label}
          </div>
        ))}
      </div>

      {/* Content */}
      <div style={{ flex: 1, overflow: "auto", background: "var(--bg-0)" }}>
        {respTab === "body" && (
          <pre style={{
            margin: 0, padding: 12, fontFamily: "var(--font-mono)", fontSize: 11.5,
            color: "var(--fg-1)", whiteSpace: "pre-wrap", wordBreak: "break-all", lineHeight: 1.6,
          }}>
            {resp.body || <span style={{ color: "var(--fg-3)" }}>(empty body)</span>}
          </pre>
        )}
        {respTab === "headers" && (
          <table style={{ width: "100%", borderCollapse: "collapse", fontFamily: "var(--font-mono)", fontSize: 11.5 }}>
            <tbody>
              {resp.headers.map(([k, v], i) => (
                <tr key={i} style={{ borderBottom: "1px solid var(--line-0)" }}>
                  <td style={{ padding: "4px 12px", color: "var(--amber)", width: "40%", verticalAlign: "top" }}>{k}</td>
                  <td style={{ padding: "4px 12px", color: "var(--fg-1)", wordBreak: "break-all" }}>{v}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {respTab === "raw" && (
          <pre style={{
            margin: 0, padding: 12, fontFamily: "var(--font-mono)", fontSize: 10.5,
            color: "var(--fg-2)", whiteSpace: "pre-wrap", wordBreak: "break-all",
          }}>
            {`HTTP/1.1 ${resp.status} ${resp.statusText}\r\n`}
            {resp.headers.map(([k, v]) => `${k}: ${v}`).join("\r\n")}
            {`\r\n\r\n${resp.body}`}
          </pre>
        )}
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════
// HISTORY PANEL
// ═══════════════════════════════════════════════════════════════════

function HistoryPanel({ history, compareA, compareB, setCompareA, setCompareB, onRestore, onOpenDiff }) {
  if (!history.length) return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11, flexDirection: "column", gap: 8 }}>
      <div>No requests sent yet</div>
      <div style={{ fontSize: 10, color: "var(--fg-4)" }}>Requests appear here after sending</div>
    </div>
  );

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minHeight: 0 }}>
      {/* Compare status */}
      {(compareA || compareB) && (
        <div style={{ padding: "5px 10px", background: "var(--amber-bg)", borderBottom: "1px solid var(--amber-dim)", fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--amber)", display: "flex", alignItems: "center", gap: 8 }}>
          <span>Comparing:</span>
          <span>{compareA ? `A: ${compareA.resp?.status}` : "A: not set"}</span>
          <span>vs</span>
          <span>{compareB ? `B: ${compareB.resp?.status}` : "B: not set"}</span>
          {compareA && compareB && <button className="btn ghost tiny" style={{ marginLeft: "auto" }} onClick={onOpenDiff}>View Diff →</button>}
          <button className="btn ghost tiny" onClick={() => { setCompareA(null); setCompareB(null); }}>Clear</button>
        </div>
      )}

      <div style={{ flex: 1, overflow: "auto" }}>
        {history.map((entry, i) => {
          const isA = compareA === entry;
          const isB = compareB === entry;
          const statusColor = entry.resp.status >= 500 ? "red" : entry.resp.status >= 400 ? "amber" : entry.resp.status >= 300 ? "cyan" : "green";
          return (
            <div key={i} style={{
              borderBottom: "1px solid var(--line-1)", padding: "8px 10px",
              background: isA || isB ? "var(--bg-3)" : "var(--bg-0)",
              boxShadow: isA ? "inset 2px 0 0 var(--cyan)" : isB ? "inset 2px 0 0 var(--magenta)" : "none",
            }}>
              <div style={{ display: "flex", alignItems: "center", gap: 8, fontFamily: "var(--font-mono)", fontSize: 11 }}>
                <span style={{ color: "var(--amber)", fontWeight: 700 }}>{entry.req.method}</span>
                <span style={{ color: `var(--${statusColor})`, fontWeight: 700 }}>{entry.resp.status}</span>
                <span style={{ color: "var(--fg-2)", fontSize: 10.5 }}>{entry.resp.time}ms</span>
                <span style={{ marginLeft: "auto", color: "var(--fg-3)", fontSize: 10 }}>{fmtTime(entry.ts)} ago</span>
              </div>
              <div style={{ fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--fg-2)", marginTop: 2, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {entry.req.url}
              </div>
              <div style={{ display: "flex", gap: 4, marginTop: 6 }}>
                <button className="btn ghost tiny" onClick={() => onRestore(entry)}>Restore</button>
                <button
                  className={"btn ghost tiny"}
                  style={{ color: isA ? "var(--cyan)" : "var(--fg-1)", borderColor: isA ? "var(--cyan-dim)" : "var(--line-2)" }}
                  onClick={() => { if (isA) setCompareA(null); else setCompareA(entry); }}
                >
                  {isA ? "✓ A" : "Set A"}
                </button>
                <button
                  className={"btn ghost tiny"}
                  style={{ color: isB ? "var(--magenta)" : "var(--fg-1)", borderColor: isB ? "var(--magenta-dim)" : "var(--line-2)" }}
                  onClick={() => { if (isB) setCompareB(null); else setCompareB(entry); }}
                >
                  {isB ? "✓ B" : "Set B"}
                </button>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════
// DIFF PANEL
// ═══════════════════════════════════════════════════════════════════

function DiffPanel({ compareA, compareB }) {
  if (!compareA || !compareB) return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 8, color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
      <div style={{ fontSize: 24 }}>⇄</div>
      <div>Set two responses to compare in the History panel</div>
      <div style={{ fontSize: 10, color: "var(--fg-4)" }}>Click "Set A" then "Set B" on any two history entries</div>
    </div>
  );

  const bodyA = compareA.resp?.body || "";
  const bodyB = compareB.resp?.body || "";
  const diffs = diffLines(bodyA, bodyB);

  const added   = diffs.filter(d => d.kind === "added").length;
  const removed = diffs.filter(d => d.kind === "removed").length;
  const changed = diffs.filter(d => d.kind === "changed").length;

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minHeight: 0 }}>
      {/* Header */}
      <div style={{ display: "flex", background: "var(--bg-1)", borderBottom: "1px solid var(--line-1)", flexShrink: 0, fontFamily: "var(--font-mono)", fontSize: 11 }}>
        <div style={{ flex: 1, padding: "5px 12px", borderRight: "1px solid var(--line-1)", display: "flex", gap: 10, alignItems: "center" }}>
          <span style={{ color: "var(--cyan)", fontWeight: 700 }}>A</span>
          <span style={{ color: `var(--${compareA.resp.status >= 400 ? "red" : "green"})` }}>{compareA.resp.status}</span>
          <span style={{ color: "var(--fg-2)" }}>{compareA.resp.time}ms</span>
          <span style={{ color: "var(--fg-3)", marginLeft: "auto", fontSize: 10 }}>{fmtTime(compareA.ts)} ago</span>
        </div>
        <div style={{ padding: "5px 12px", fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--fg-3)", display: "flex", gap: 10, alignItems: "center" }}>
          {changed > 0 && <span style={{ color: "var(--amber)" }}>~{changed} changed</span>}
          {added > 0   && <span style={{ color: "var(--green)" }}>+{added} added</span>}
          {removed > 0 && <span style={{ color: "var(--red)" }}>-{removed} removed</span>}
        </div>
        <div style={{ flex: 1, padding: "5px 12px", borderLeft: "1px solid var(--line-1)", display: "flex", gap: 10, alignItems: "center" }}>
          <span style={{ color: "var(--magenta)", fontWeight: 700 }}>B</span>
          <span style={{ color: `var(--${compareB.resp.status >= 400 ? "red" : "green"})` }}>{compareB.resp.status}</span>
          <span style={{ color: "var(--fg-2)" }}>{compareB.resp.time}ms</span>
          <span style={{ color: "var(--fg-3)", marginLeft: "auto", fontSize: 10 }}>{fmtTime(compareB.ts)} ago</span>
        </div>
      </div>

      {/* Diff body */}
      <div style={{ flex: 1, overflow: "auto", display: "grid", gridTemplateColumns: "1fr 1fr", background: "var(--line-1)", gap: 1 }}>
        {/* Column A */}
        <div style={{ background: "var(--bg-0)", overflow: "auto" }}>
          <pre style={{ margin: 0, padding: 0, fontFamily: "var(--font-mono)", fontSize: 11, lineHeight: 1.5 }}>
            {diffs.map((d, i) => (
              <div key={i} style={{
                padding: "0 12px",
                background: d.kind === "removed" ? "rgba(248,114,114,0.12)" : d.kind === "changed" ? "rgba(245,165,36,0.1)" : "transparent",
                color: d.kind === "removed" ? "var(--red)" : d.kind === "changed" ? "var(--amber)" : "var(--fg-1)",
                borderLeft: d.kind === "removed" ? "2px solid var(--red)" : d.kind === "changed" ? "2px solid var(--amber)" : "2px solid transparent",
              }}>
                <span style={{ color: "var(--fg-4)", userSelect: "none", marginRight: 12, fontSize: 9.5 }}>{i + 1}</span>
                {d.a}
              </div>
            ))}
          </pre>
        </div>
        {/* Column B */}
        <div style={{ background: "var(--bg-0)", overflow: "auto" }}>
          <pre style={{ margin: 0, padding: 0, fontFamily: "var(--font-mono)", fontSize: 11, lineHeight: 1.5 }}>
            {diffs.map((d, i) => (
              <div key={i} style={{
                padding: "0 12px",
                background: d.kind === "added" ? "rgba(54,211,153,0.10)" : d.kind === "changed" ? "rgba(245,165,36,0.1)" : "transparent",
                color: d.kind === "added" ? "var(--green)" : d.kind === "changed" ? "var(--amber)" : "var(--fg-1)",
                borderLeft: d.kind === "added" ? "2px solid var(--green)" : d.kind === "changed" ? "2px solid var(--amber)" : "2px solid transparent",
              }}>
                <span style={{ color: "var(--fg-4)", userSelect: "none", marginRight: 12, fontSize: 9.5 }}>{i + 1}</span>
                {d.b}
              </div>
            ))}
          </pre>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { OpsPage });
