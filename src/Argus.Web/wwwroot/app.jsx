/* eslint-disable */
// ROOT APP

const NAV = [
  { id: "command",   label: "Command Center",  icon: "command", section: "OVERVIEW", live: true },
  { id: "targets",   label: "Targets",         icon: "targets", section: "OVERVIEW", badge: "4" },
  { id: "assets",    label: "Assets",          icon: "assets", section: "EXPLORE", badge: "14.2k" },
  { id: "findings",  label: "Findings",        icon: "findings", section: "EXPLORE", badge: "47", alert: true },
  { id: "atypes",    label: "Asset Types",     icon: "workerTypes", section: "EXPLORE" },
  { id: "workers",   label: "Workers",         icon: "workers", section: "EXECUTE", live: true },
  { id: "wtypes",    label: "Worker Types",    icon: "workerTypes", section: "EXECUTE", badge: "12" },
  { id: "subs",      label: "Subscriptions",   icon: "subs", section: "EXECUTE", badge: "34" },
  { id: "tasks",     label: "Task Runs",       icon: "tasks", section: "EXECUTE", badge: "220" },
  { id: "ops",       label: "Operations",      icon: "command", section: "EXECUTE", badge: "HTTP" },
  { id: "contexts",  label: "Checkpoints",     icon: "contexts", section: "EXECUTE", badge: "11" },
  { id: "events",    label: "Events",          icon: "events", section: "OBSERVE", live: true },
  { id: "agents",    label: "Agents",          icon: "agents-ai", section: "DEVELOPMENT", live: true, badge: "8" },
  { id: "agtasks",   label: "Agent Tasks",     icon: "tasks", section: "DEVELOPMENT", badge: "26" },
  { id: "schedules", label: "Schedules",       icon: "contexts", section: "DEVELOPMENT" },
  { id: "settings",  label: "Settings",        icon: "settings", section: "SYSTEM" },
];


const PAGE_ALIASES = {
  "": "command",
  "/": "command",
  "/command-center": "command",
  "/command": "command",
  "/targets": "targets",
  "/assets": "assets",
  "/findings": "findings",
  "/asset-types": "atypes",
  "/workers": "workers",
  "/worker-types": "wtypes",
  "/subscriptions": "subs",
  "/tasks": "tasks",
  "/operations": "ops",
  "/ops": "ops",
  "/checkpoints": "contexts",
  "/events": "events",
  "/agents": "agents",
  "/agent-tasks": "agtasks",
  "/schedules": "schedules",
  "/settings": "settings",
};

function pageFromLocation() {
  const hash = window.location.hash?.replace(/^#\/?/, "");
  if (hash && NAV.some(n => n.id === hash)) return hash;
  const path = window.location.pathname.replace(/\/+$/, "") || "/";
  return PAGE_ALIASES[path] || (NAV.some(n => n.id === path.slice(1)) ? path.slice(1) : "command");
}

function pathForPage(page) {
  return {
    command: "/command-center",
    atypes: "/asset-types",
    wtypes: "/worker-types",
    subs: "/subscriptions",
    ops: "/operations",
    contexts: "/checkpoints",
    agtasks: "/agent-tasks",
  }[page] || `/${page}`;
}

function navBadge(id) {
  switch (id) {
    case "targets": return TARGETS.length || "";
    case "assets": return ASSETS.length ? fmtNum(ASSETS.length) : "";
    case "findings": return ASSETS.filter(a => a.type === "FindingCandidate").length || "";
    case "wtypes": return WORKER_TYPES.length;
    case "tasks": return TASKS.length ? fmtNum(TASKS.length) : "";
    case "events": return EVENTS.length ? fmtNum(EVENTS.length) : "";
    case "agents": return AGENTS.length || "";
    case "agtasks": return AGENT_TASKS.length || "";
    default: return null;
  }
}


const DEFAULTS = /*EDITMODE-BEGIN*/{
  "density": "compact",
  "accent": "amber",
  "ticker": true,
  "tickSpeed": 1
}/*EDITMODE-END*/;

function App() {
  const [page, setPage] = useState(pageFromLocation());
  const [target, setTarget] = useState(TARGETS[0]);
  const [t, setTweak] = useTweaks(DEFAULTS);
  const [liveTick, setLiveTick] = useState(0);
  const [liveEvents, setLiveEvents] = useState([]);
  const [clock, setClock] = useState(Date.now());
  const [dataVersion, setDataVersion] = useState(0);
  const [dataStatus, setDataStatus] = useState("loading");

  // Live ticker — bump every N seconds, generate synthetic events.
  useEffect(() => {
    if (!t.ticker) return;
    const interval = Math.max(200, 1500 / t.tickSpeed);
    const id = setInterval(() => {
      setLiveTick(x => x + 1);
      const et = EVENT_TYPES[Math.floor(Math.random() * EVENT_TYPES.length)];
      const a = ASSETS[Math.floor(Math.random() * ASSETS.length)];
      const w = WORKER_INSTANCES[Math.floor(Math.random() * WORKER_INSTANCES.length)];
      setLiveEvents(le => [{
        id: "E-" + Date.now().toString(16).toUpperCase(),
        type: et.id,
        color: et.color,
        worker: w.id,
        assetId: a.id,
        assetValue: a.value,
        assetType: a.type,
        t: Date.now(),
      }, ...le].slice(0, 50));
    }, interval);
    return () => clearInterval(id);
  }, [t.ticker, t.tickSpeed]);

  // clock
  useEffect(() => {
    const id = setInterval(() => setClock(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  // Load live backend state through the existing .NET BFF endpoints.
  useEffect(() => {
    let cancelled = false;
    if (!window.ArgusApi?.loadInitialState) {
      setDataStatus("mock");
      return;
    }
    window.ArgusApi.loadInitialState()
      .then((summary) => {
        if (cancelled) return;
        setDataStatus(summary ? "live" : "mock");
        setTarget(TARGETS[0] || target);
        setDataVersion(v => v + 1);
      })
      .catch((err) => {
        console.warn("Argus live state unavailable; using bundled sample data.", err);
        if (!cancelled) setDataStatus("mock");
      });
    const onPop = () => setPage(pageFromLocation());
    window.addEventListener("popstate", onPop);
    return () => { cancelled = true; window.removeEventListener("popstate", onPop); };
  }, []);

  const navigatePage = (id) => {
    setPage(id);
    const path = pathForPage(id);
    if (window.location.pathname !== path) {
      window.history.pushState({ page: id }, "", path);
    }
  };

  const sections = ["OVERVIEW", "EXPLORE", "EXECUTE", "OBSERVE", "DEVELOPMENT", "SYSTEM"];

  return (
    <div
      className="app"
      data-density={t.density}
      data-accent={t.accent}
    >
      {/* TOPBAR - full width */}
      <div className="topbar">
        <div className="brand">
          <span className="brand-mark" />
          <div>
            <div className="brand-name">ArgusEngine</div>
            <div className="brand-build">v0.7.4-mvp · build #2841</div>
          </div>
        </div>
        <div className="target-select">
          <div>
            <div className="label">Active Target</div>
            <div className="name">{target.name}</div>
          </div>
          <span className="scope-count">{target.scopes} scopes</span>
          <span style={{ color: "var(--fg-3)", marginLeft: 4 }}>{ICONS.caretDown}</span>
        </div>
        <div className="search">
          {ICONS.search}
          <input
            className="search-input"
            placeholder="search.assets / .events / .tasks   ·   :asset value:^api  ·  $worker JsExtractor"
          />
          <span className="search-kbd">⌘K</span>
        </div>
        <div className="top-stats">
          <TopStat l="Assets" v={fmtNum(ASSETS.length)} tone="green" />
          <TopStat l="Tasks" v={fmtNum(TASKS.length)} tone="cyan" />
          <TopStat l="Errors" v={(TASKS.filter(t => /fail|error/i.test(t.state)).length || 0).toString()} tone="amber" />
          <TopStat l="Findings" v={ASSETS.filter(a => a.type === "FindingCandidate").length.toString()} tone="magenta" />
        </div>
        <div className="top-icons">
          <button className="icon-btn" title="Notifications" style={{ position: "relative" }}>
            {ICONS.bell}
            <span style={{ position: "absolute", top: 4, right: 4, width: 5, height: 5, background: "var(--red)" }} />
          </button>
          <button className="icon-btn" title="Settings">{ICONS.settings}</button>
        </div>
      </div>

      {/* BODY: sidebar + main */}
      <div className="app-body">

      {/* SIDEBAR */}
      <ResizableSidebar id="main-sidebar" defaultWidth={188} minWidth={48} maxWidth={340}>
        <div className="sidebar" style={{ flex: 1, padding: "4px 0", overflow: "auto" }}>
          {sections.map(sec => (
            <React.Fragment key={sec}>
              <div className="nav-section-label">{sec}</div>
              {NAV.filter(n => n.section === sec).map(n => (
                <div
                  key={n.id}
                  className={"nav-item" + (page === n.id ? " active" : "")}
                  onClick={() => navigatePage(n.id)}
                >
                  <span className="nav-icon">{ICONS[n.icon]}</span>
                  <span>{n.label}</span>
                  {n.live && <span className="badge live">●  {dataStatus === "live" ? "LIVE" : "SIM"}</span>}
                  {!n.live && (navBadge(n.id) || n.badge) && <span className={"badge" + (n.alert ? " alert" : "")}>{navBadge(n.id) || n.badge}</span>}
                </div>
              ))}
            </React.Fragment>
          ))}
          <div className="sidebar-bottom">
            <div className="row"><span>BUS</span><b style={{ color: "var(--green)" }}>● HEALTHY</b></div>
            <div className="row"><span>QUEUE</span><b>{TASKS.filter(t => ["Queued","Requested","RetryPending"].includes(t.state)).length}</b></div>
            <div className="row"><span>STORE</span><b>91% free</b></div>
            <div className="row"><span>WORKERS</span><b>{WORKER_INSTANCES.length}</b></div>
          <div className="row"><span>DATA</span><b>{dataStatus.toUpperCase()}</b></div>
          </div>
        </div>
      </ResizableSidebar>

      {/* MAIN */}
      <div className="main" style={{ flex: 1, minWidth: 0 }} key={`${page}-${dataVersion}`}>
        {page === "command" && <CommandCenter liveTick={liveTick} events={liveEvents} />}
        {page === "assets" && <AssetExplorer liveTick={liveTick} />}
        {page === "workers" && <WorkersPage />}
        {page === "tasks" && <TasksPage />}
        {page === "ops" && <OpsPage />}
        {page === "events" && <EventsPage liveEvents={liveEvents} />}
        {page === "agents" && <AgentsPage liveTick={liveTick} />}
        {page === "agtasks" && <AgentTasksPage />}
        {!["command", "assets", "workers", "tasks", "ops", "events", "agents", "agtasks"].includes(page) && (
          <PlaceholderPage page={NAV.find(n => n.id === page)} />
        )}
      </div>

      </div>{/* end .app-body */}

      {/* STATUSBAR */}
      <div className="statusbar">
        <span className="ss"><span className="dot green pulse" /><b>OPERATIONAL</b></span>
        <span className="ss">target<b>{target.id}</b></span>
        <span className="ss">page<b>{page}</b></span>
        <span className="ss">data<b>{dataStatus}</b></span>
        <span className="ss">bus.lag<b>14ms</b></span>
        <span className="ss">store.iops<b>2.4k</b></span>
        <span className="ss">live.events<b style={{ color: "var(--accent)" }}>{liveEvents.length}</b></span>
        <span className="spacer" />
        <span className="ss">{new Date(clock).toISOString().slice(0, 19).replace("T", " ")}Z</span>
        <span className="ss">utc</span>
      </div>

      {/* TWEAKS */}
      <ArgusTweaks t={t} setTweak={setTweak} />
    </div>
  );
}

function TopStat({ l, v, tone }) {
  return (
    <div className="top-stat">
      <div className="top-stat-label">{l}</div>
      <div className={"top-stat-value " + (tone || "")}>{v}</div>
    </div>
  );
}

function PlaceholderPage({ page }) {
  if (!page) return null;
  // Build a context-aware placeholder so the page doesn't feel empty.
  const variants = {
    targets: { title: "Targets · Scope & rules", body: <TargetsList /> },
    findings: { title: "Findings · Triage queue", body: <FindingsList /> },
    atypes: { title: "Asset Types · Schema catalog", body: <AssetTypesList /> },
    wtypes: { title: "Worker Types · Capability catalog", body: <WorkerTypesList /> },
    subs: { title: "Worker Subscriptions · Event routing", body: <SubscriptionsList /> },
    contexts: { title: "Worker Checkpoints · Resumable", body: <CheckpointsList /> },
    schedules: { title: "Agent Schedules · Cron view", body: <SchedulesList /> },
    settings: { title: "Settings", body: <PlainStub label="System settings & integrations" /> },
  };
  const v = variants[page.id] || { title: page.label, body: <PlainStub label={page.label} /> };
  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", minHeight: 0 }}>
      <div className="page-tabs">
        <div className="page-tab active"><span>{v.title}</span></div>
        <div className="page-tab-spacer" />
      </div>
      <div style={{ flex: 1, overflow: "auto" }}>{v.body}</div>
    </div>
  );
}

function PlainStub({ label }) {
  return (
    <div style={{ padding: 40, fontFamily: "var(--font-mono)", color: "var(--fg-3)", fontSize: 12 }}>
      <div style={{ border: "1px dashed var(--line-2)", padding: 24, maxWidth: 600 }}>
        <div className="mono-label">// {label}</div>
        <div style={{ marginTop: 10, color: "var(--fg-2)", fontSize: 11.5 }}>
          This module exists in the MVP nav but the focus of this prototype is the asset explorer + live ops view. Visit Assets, Command Center, Workers, Tasks, or Events for the wired views.
        </div>
      </div>
    </div>
  );
}

// ─── simple sub-pages

function TargetsList() {
  return (
    <table className="asset-grid" style={{ width: "100%" }}>
      <thead>
        <tr><th>Target</th><th>Status</th><th>Scopes</th><th>Assets</th><th>Findings</th><th>Last Activity</th></tr>
      </thead>
      <tbody>
        {TARGETS.map(t => (
          <tr key={t.id}>
            <td className="c-value"><span style={{ color: "var(--fg-0)" }}>{t.name}</span> <span style={{ color: "var(--fg-3)", fontSize: 10 }}>{t.id}</span></td>
            <td><Pill tone={t.status === "active" ? "green" : "amber"}>{t.status}</Pill></td>
            <td className="tabular">{t.scopes}</td>
            <td className="tabular">{t.assets.toLocaleString()}</td>
            <td className="tabular" style={{ color: "var(--magenta)" }}>{t.findings}</td>
            <td className="tabular" style={{ color: "var(--fg-2)" }}>{Math.floor(Math.random() * 30) + 1}m ago</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function FindingsList() {
  const findings = ASSETS.filter(a => a.type === "FindingCandidate").sort((a, b) => b.risk - a.risk);
  return (
    <div style={{ padding: 10, display: "flex", flexDirection: "column", gap: 1, background: "var(--line-1)" }}>
      {findings.map(f => (
        <div key={f.id} style={{ display: "grid", gridTemplateColumns: "40px 90px 1fr 200px 100px 60px", gap: 12, padding: "8px 12px", background: "var(--bg-1)", alignItems: "center" }}>
          <span style={{ color: f.risk > 80 ? "var(--red)" : "var(--amber)", fontSize: 16 }}>⚑</span>
          <Pill tone={f.risk > 80 ? "red" : f.risk > 60 ? "amber" : "dim"}>R-{f.risk}</Pill>
          <div>
            <div className="mono" style={{ color: "var(--fg-0)" }}>{f.value}</div>
            <div className="mono" style={{ color: "var(--fg-3)", fontSize: 10, marginTop: 2 }}>on <span style={{ color: "var(--cyan)" }}>{f.parentValue}</span></div>
          </div>
          <div className="mono" style={{ color: "var(--magenta)", fontSize: 10.5 }}>via {f.worker}</div>
          <div>{f.tags.map(t => <TagChip key={t} tag={t} />)}</div>
          <div style={{ display: "flex", gap: 4 }}>
            <button className="btn ghost tiny">Triage</button>
          </div>
        </div>
      ))}
    </div>
  );
}

function AssetTypesList() {
  return (
    <div style={{ padding: 10, display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(320px, 1fr))", gap: 1, background: "var(--line-1)" }}>
      {ASSET_TYPES.map(t => {
        const n = ASSETS.filter(a => a.type === t.id).length;
        const producers = WORKER_TYPES.filter(w => w.outputs.includes(t.id));
        const consumers = WORKER_TYPES.filter(w => w.inputs.includes(t.id));
        return (
          <div key={t.id} style={{ background: "var(--bg-1)", padding: 12, borderLeft: `2px solid var(--${t.color === "fg-1" ? "accent" : t.color})` }}>
            <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span style={{ fontSize: 18, color: `var(--${t.color === "fg-1" ? "fg-0" : t.color})` }} className="mono">{t.glyph}</span>
              <span style={{ color: "var(--fg-0)", fontWeight: 600 }} className="cond uppercase">{t.id}</span>
              <span style={{ marginLeft: "auto" }} className="mono tabular text-fg-2">{n}</span>
            </div>
            <div className="mono" style={{ color: "var(--fg-3)", fontSize: 10, marginTop: 8, letterSpacing: "0.12em", textTransform: "uppercase" }}>Produced by</div>
            <div style={{ marginTop: 2, fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--magenta)" }}>{producers.map(p => p.id).join(", ") || "—"}</div>
            <div className="mono" style={{ color: "var(--fg-3)", fontSize: 10, marginTop: 6, letterSpacing: "0.12em", textTransform: "uppercase" }}>Consumed by</div>
            <div style={{ marginTop: 2, fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--cyan)" }}>{consumers.map(c => c.id).join(", ") || "—"}</div>
          </div>
        );
      })}
    </div>
  );
}

function WorkerTypesList() {
  return (
    <div style={{ padding: 10, display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(380px, 1fr))", gap: 1, background: "var(--line-1)" }}>
      {WORKER_TYPES.map(w => (
        <div key={w.id} style={{ background: "var(--bg-1)", padding: 12, borderTop: `2px solid var(--${w.color})` }}>
          <div style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
            <span style={{ color: `var(--${w.color})`, fontWeight: 700 }} className="cond uppercase">{w.id}</span>
            <Pill tone="dim">{w.cat}</Pill>
            <span className="mono" style={{ marginLeft: "auto", fontSize: 10, color: "var(--fg-3)" }}>v1.4.2 · system</span>
          </div>
          <div className="mono" style={{ fontSize: 10.5, color: "var(--fg-2)", marginTop: 8 }}>
            <span style={{ color: "var(--cyan)" }}>{w.inputs.join(", ")}</span>
            <span style={{ margin: "0 6px", color: "var(--fg-3)" }}>→</span>
            <span style={{ color: "var(--magenta)" }}>{w.outputs.join(", ") || "—"}</span>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 1fr 1fr", gap: 6, marginTop: 8 }}>
            <Tiny l="instances" v={WORKER_INSTANCES.filter(i => i.type === w.id).length} />
            <Tiny l="concurrency" v="8" />
            <Tiny l="retries" v="3" />
            <Tiny l="checkpoint" v={w.cat === "Spider" || w.cat === "Discovery" ? "yes" : "no"} />
          </div>
        </div>
      ))}
    </div>
  );
}

function SubscriptionsList() {
  const subs = [];
  for (const wt of WORKER_TYPES) {
    for (const at of wt.inputs.filter(x => x !== "*")) {
      subs.push({ worker: wt, asset: at, event: "AssetDiscovered", filter: at === "Url" ? "scope=InScope" : "—", priority: at === "FindingCandidate" ? "High" : "Normal", enabled: true });
      subs.push({ worker: wt, asset: at, event: "AssetConfirmed", filter: "confidence>=80", priority: "Normal", enabled: Math.random() > 0.2 });
    }
  }
  return (
    <table className="asset-grid" style={{ width: "100%" }}>
      <thead>
        <tr>
          <th></th><th>Worker Type</th><th>Asset Type</th><th>Event</th><th>Filter Expr</th><th>Priority</th><th>Concurrency</th><th>Rate Limit</th>
        </tr>
      </thead>
      <tbody>
        {subs.map((s, i) => (
          <tr key={i}>
            <td className="c-check"><span className={"cell-checkbox" + (s.enabled ? " on" : "")} /></td>
            <td className="c-type"><span style={{ color: `var(--${s.worker.color})` }}>{s.worker.id}</span></td>
            <td className="c-type">{s.asset}</td>
            <td className="c-status"><Pill tone={s.event === "AssetConfirmed" ? "green" : "cyan"}>{s.event}</Pill></td>
            <td className="c-value mono" style={{ color: "var(--amber)" }}>{s.filter}</td>
            <td className="c-status"><Pill tone={s.priority === "High" ? "magenta" : "dim"}>{s.priority}</Pill></td>
            <td className="c-conf">8</td>
            <td className="c-int">60/m</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function SchedulesList() {
  const items = [];
  for (const a of AGENTS) {
    for (const r of a.recurring) items.push({ ...r, agent: a });
  }
  return (
    <div style={{ padding: 12 }}>
      <div className="mono-label" style={{ marginBottom: 8 }}>RECURRING JOBS · {items.length} · across {AGENTS.length} agents</div>
      <table className="asset-grid" style={{ width: "100%" }}>
        <thead>
          <tr>
            <th style={{ width: 22 }}></th>
            <th style={{ width: 140 }}>Agent</th>
            <th>Job</th>
            <th style={{ width: 160 }}>Schedule (cron)</th>
            <th style={{ width: 100 }}>Last Run</th>
            <th style={{ width: 100 }}>Next Run</th>
            <th style={{ width: 80 }}>Status</th>
          </tr>
        </thead>
        <tbody>
          {items.map((r, i) => (
            <tr key={i}>
              <td className="c-check"><span className={"cell-checkbox" + (r.enabled ? " on" : "")} /></td>
              <td><AgentChip id={r.agent.id} /></td>
              <td style={{ color: "var(--fg-0)" }}>{r.label}</td>
              <td style={{ color: "var(--amber)" }} className="mono">{r.cron}</td>
              <td className="tabular" style={{ color: "var(--fg-2)" }}>{r.lastRun ? fmtTime(r.lastRun) + " ago" : "never"}</td>
              <td className="tabular" style={{ color: "var(--cyan)" }}>{r.enabled ? "in ~" + (Math.floor(Math.abs(Math.sin(i + 1)) * 30) + 1) + "m" : "—"}</td>
              <td><Pill tone={r.enabled ? "green" : "dim"}>{r.enabled ? "active" : "paused"}</Pill></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CheckpointsList() {
  const cps = TASKS.filter(t => t.state === "Checkpointed").slice(0, 30);
  return (
    <div style={{ padding: 10, display: "flex", flexDirection: "column", gap: 1, background: "var(--line-1)" }}>
      {cps.map(t => (
        <div key={t.id} style={{ background: "var(--bg-1)", padding: 12 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <Pill tone="amber">CHECKPOINTED</Pill>
            <span style={{ color: "var(--magenta)", fontWeight: 600 }} className="cond uppercase">{t.type}</span>
            <span className="mono" style={{ color: "var(--fg-3)", fontSize: 10 }}>{t.id}</span>
            <span style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
              <button className="btn ghost tiny">Resume</button>
              <button className="btn danger tiny">Abandon</button>
            </span>
          </div>
          <div className="mono" style={{ marginTop: 6, fontSize: 11 }}>
            <span style={{ color: "var(--fg-3)" }}>on</span> <span style={{ color: "var(--cyan)" }}>{t.assetValue}</span>
          </div>
          <div style={{ marginTop: 6, display: "flex", gap: 10, alignItems: "center" }}>
            <Bar value={t.progress} tone="amber" width={140} />
            <span className="mono tabular" style={{ color: "var(--amber)" }}>{t.progress}%</span>
            <span className="mono" style={{ color: "var(--fg-3)", marginLeft: "auto", fontSize: 10 }}>
              lock expires <span style={{ color: "var(--red)" }}>32s</span> · cursor <span style={{ color: "var(--accent)" }}>{t.checkpointJson}</span>
            </span>
          </div>
        </div>
      ))}
    </div>
  );
}

// ─── TWEAKS PANEL ───────────────────────────────────────────────────
function ArgusTweaks({ t, setTweak }) {
  return (
    <TweaksPanel title="Tweaks">
      <TweakSection label="Display" />
      <TweakRadio
        label="Density"
        value={t.density}
        onChange={(v) => setTweak("density", v)}
        options={["comfortable", "compact", "ultra"]}
      />
      <TweakRadio
        label="Accent"
        value={t.accent}
        onChange={(v) => setTweak("accent", v)}
        options={["amber", "cyan", "green", "magenta"]}
      />
      <TweakSection label="Live Stream" />
      <TweakToggle label="Event ticker" value={t.ticker} onChange={(v) => setTweak("ticker", v)} />
      <TweakSlider label="Tick speed" min={0.25} max={4} step={0.25} value={t.tickSpeed} unit="×" onChange={(v) => setTweak("tickSpeed", v)} />
    </TweaksPanel>
  );
}

// Render
ReactDOM.createRoot(document.getElementById("root")).render(<App />);
