/* eslint-disable */
// COMMAND CENTER — global telemetry.

function CommandCenter({ liveTick, events }) {
  const totalAssets = ASSETS.length;
  const totalFindings = ASSETS.filter(a => a.type === "FindingCandidate").length;
  const newAssets = ASSETS.filter(a => Date.now() - a.firstSeen < 3600 * 1000).length;
  const tasksRunning = TASKS.filter(t => t.state === "Running" || t.state === "Leased").length;
  const tasksFailed = TASKS.filter(t => t.state === "Failed").length;
  const workersOnline = WORKER_INSTANCES.filter(w => w.status !== "Stopped" && w.status !== "Failed").length;

  const eventHistogram = useMemo(() => {
    const buckets = new Array(60).fill(0);
    EVENTS.forEach(e => {
      const m = Math.floor((Date.now() - e.t) / 60000);
      if (m >= 0 && m < 60) buckets[m]++;
    });
    return buckets.reverse();
  }, []);

  return (
    <div className="cc-grid">
      {/* KPIs */}
      <Kpi l="Assets · total" v={totalAssets.toLocaleString()} delta="+183 / hr" sub={["in-scope " + ASSETS.filter(a => a.status === "InScope").length, "new " + newAssets]} />
      <Kpi l="Workers online" v={workersOnline + "/" + WORKER_INSTANCES.length} delta="+0" tone="cyan" sub={["busy " + WORKER_INSTANCES.filter(w => w.status === "Busy").length, "degraded " + WORKER_INSTANCES.filter(w => w.status === "Degraded").length]} />
      <Kpi l="Tasks running" v={tasksRunning} delta={"+" + Math.floor(liveTick % 7)} tone="amber" sub={["queued " + TASKS.filter(t => t.state === "Queued").length, "checkpoint " + TASKS.filter(t => t.state === "Checkpointed").length]} />
      <Kpi l="Findings" v={totalFindings} delta="+2" tone="magenta" sub={["high " + ASSETS.filter(a => a.type === "FindingCandidate" && a.risk > 80).length, "today 11"]} />

      {/* main row */}
      <div className="cc-cell" style={{ gridColumn: "1 / 3", gridRow: "2 / 3" }}>
        <CardHeader title="Event Throughput · Last 60 min" corner={<><span>peak {Math.max(...eventHistogram)}/m</span><span className="dot green pulse" />LIVE</>} />
        <div style={{ padding: 12, flex: 1, display: "flex", flexDirection: "column" }}>
          <EventThroughput data={eventHistogram} />
        </div>
      </div>

      <div className="cc-cell" style={{ gridColumn: "3 / 5", gridRow: "2 / 3" }}>
        <CardHeader title="Asset Pipeline — discoveries by type" corner={<span className="mono" style={{ fontSize: 10 }}>{ASSETS.length} total</span>} />
        <div style={{ padding: 12, flex: 1, overflow: "auto" }}>
          <AssetPipeline />
        </div>
      </div>

      {/* bottom row */}
      <div className="cc-cell" style={{ gridColumn: "1 / 3", gridRow: "3 / 4" }}>
        <CardHeader title="Worker Fleet" corner={<><span>{WORKER_INSTANCES.length} instances</span><span>·</span><span className="text-cyan">{WORKER_TYPES.length} types</span></>} />
        <div style={{ flex: 1, overflow: "auto" }}>
          <WorkerFleetMini />
        </div>
      </div>

      <div className="cc-cell" style={{ gridColumn: "3 / 5", gridRow: "3 / 4" }}>
        <CardHeader title="Live Event Stream" corner={<><span className="text-fg-3">{events?.length || EVENTS.length} buffered</span><span className="dot green pulse" /></>} />
        <div style={{ flex: 1, overflow: "auto" }}>
          {EVENTS.slice(0, 40).map(e => <EventRow key={e.id} event={e} />)}
        </div>
      </div>
    </div>
  );
}

function Kpi({ l, v, delta, tone, sub }) {
  return (
    <div className="cc-cell kpi">
      <div>
        <div className="l">{l}</div>
        <div className={"v " + (tone === "accent" ? "accent" : "")} style={{ color: tone === "cyan" ? "var(--cyan)" : tone === "amber" ? "var(--amber)" : tone === "magenta" ? "var(--magenta)" : undefined }}>{v}</div>
      </div>
      {delta && <div className={"delta" + (delta.startsWith("-") ? " down" : "")}>{delta}</div>}
      {sub && <div className="sub">{sub.map((s, i) => <span key={i}>{s}</span>)}</div>}
    </div>
  );
}

function CardHeader({ title, corner }) {
  return (
    <div className="card-header">
      <span className="pre-tick" />
      <span>{title}</span>
      {corner && <span className="corner">{corner}</span>}
    </div>
  );
}

function EventThroughput({ data }) {
  const max = Math.max(...data, 1);
  return (
    <>
      <div style={{ display: "flex", alignItems: "flex-end", gap: 1, height: 130, flex: 1 }}>
        {data.map((n, i) => {
          const h = (n / max) * 100;
          return (
            <div key={i} style={{ flex: 1, height: Math.max(2, h) + "%", background: i === data.length - 1 ? "var(--accent)" : "var(--cyan)", opacity: 0.4 + (i / data.length) * 0.6 }} />
          );
        })}
      </div>
      <div style={{ display: "flex", justifyContent: "space-between", paddingTop: 4, fontFamily: "var(--font-mono)", fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.06em" }}>
        <span>-60m</span><span>-45m</span><span>-30m</span><span>-15m</span><span>NOW</span>
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(5, 1fr)", gap: 1, marginTop: 12, background: "var(--line-1)" }}>
        {[
          ["AssetDiscovered", "cyan", 1247],
          ["AssetConfirmed", "green", 892],
          ["WorkerTaskCompleted", "violet", 1830],
          ["WorkerTaskFailed", "red", 22],
          ["FindingCreated", "magenta", 14],
        ].map(([k, c, n]) => (
          <div key={k} style={{ background: "var(--bg-1)", padding: "8px 10px" }}>
            <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.1em", textTransform: "uppercase" }}>{k}</div>
            <div className="mono" style={{ fontSize: 16, color: `var(--${c})`, marginTop: 2 }}>{n}</div>
          </div>
        ))}
      </div>
    </>
  );
}

function AssetPipeline() {
  // Stage flow visualization: Domain -> Subdomain -> URL/Page -> JS/API -> Finding
  const stages = [
    { name: "Domains", types: ["Domain"], color: "fg-1" },
    { name: "Subdomains", types: ["Subdomain"], color: "cyan" },
    { name: "Pages", types: ["HtmlPage", "Url"], color: "violet" },
    { name: "JS & APIs", types: ["JavaScriptFile", "ApiEndpoint", "JsonDocument"], color: "amber" },
    { name: "Findings", types: ["FindingCandidate"], color: "magenta" },
  ];
  const counts = stages.map(s => ASSETS.filter(a => s.types.includes(a.type)).length);
  const max = Math.max(...counts);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
      {stages.map((s, i) => (
        <div key={s.name} style={{ display: "grid", gridTemplateColumns: "98px 1fr 60px 50px", gap: 8, alignItems: "center" }}>
          <div className="mono" style={{ fontSize: 11, color: "var(--fg-1)", letterSpacing: "0.05em" }}>
            <span style={{ color: `var(--${s.color})`, marginRight: 6 }}>▶</span>{s.name}
          </div>
          <div style={{ height: 16, background: "var(--bg-2)", border: "1px solid var(--line-1)", position: "relative", overflow: "hidden" }}>
            <div style={{ position: "absolute", inset: 0, width: ((counts[i] / max) * 100) + "%", background: `var(--${s.color})`, opacity: 0.7 }} />
            <div style={{ position: "absolute", left: 6, top: 1, fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--fg-0)", letterSpacing: "0.04em" }}>
              {counts[i]} assets
            </div>
          </div>
          <div className="mono tabular" style={{ fontSize: 10, color: "var(--fg-3)", textAlign: "right" }}>
            +{Math.floor(counts[i] / 8)} /h
          </div>
          <div>
            <Spark data={Array.from({ length: 12 }, () => 1 + Math.floor(Math.random() * 9))} tone={s.color === "fg-1" ? "accent" : s.color} width={50} height={14} />
          </div>
        </div>
      ))}
      <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.14em", textTransform: "uppercase", marginTop: 8, paddingTop: 8, borderTop: "1px solid var(--line-1)" }}>
        Worker Subscriptions · matched / asset event
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: 4, fontFamily: "var(--font-mono)", fontSize: 10.5 }}>
        {[
          ["AssetDiscovered → Subdomain", 5],
          ["AssetConfirmed → HtmlPage", 4],
          ["AssetDiscovered → JavaScriptFile", 3],
          ["AssetDiscovered → Url", 4],
          ["AssetConfirmed → ApiEndpoint", 2],
          ["AssetHighValueMarked → *", 8],
        ].map(([k, n]) => (
          <div key={k} style={{ display: "flex", justifyContent: "space-between", padding: "3px 6px", background: "var(--bg-2)", border: "1px solid var(--line-1)" }}>
            <span style={{ color: "var(--fg-2)" }}>{k}</span>
            <span style={{ color: "var(--accent)" }}>{n}w</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function WorkerFleetMini() {
  return (
    <table style={{ width: "100%", borderCollapse: "collapse", fontFamily: "var(--font-mono)", fontSize: 11 }}>
      <thead>
        <tr style={{ background: "var(--bg-2)", borderBottom: "1px solid var(--line-2)" }}>
          {["Type / Instance", "Status", "Tasks", "Throughput", "30s sparkline", "Errors", "Heartbeat"].map(h => (
            <th key={h} style={{ textAlign: "left", padding: "5px 10px", fontSize: 9.5, color: "var(--fg-3)", fontWeight: 500, letterSpacing: "0.12em", textTransform: "uppercase" }}>{h}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {WORKER_INSTANCES.map(w => (
          <tr key={w.id} style={{ borderBottom: "1px solid var(--line-0)", height: 22 }}>
            <td style={{ padding: "2px 10px" }}>
              <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                <span style={{ color: `var(--${w.color})`, fontWeight: 600 }}>{w.type}</span>
                <span style={{ color: "var(--fg-3)", fontSize: 10 }}>{w.id}</span>
              </div>
            </td>
            <td style={{ padding: "2px 10px" }}>
              <span className={"dot " + (w.status === "Healthy" ? "green" : w.status === "Busy" ? "cyan" : w.status === "Degraded" ? "amber" : "red")} style={{ marginRight: 5 }} />
              <span style={{ color: "var(--fg-1)", fontSize: 10.5 }}>{w.status}</span>
            </td>
            <td style={{ padding: "2px 10px", color: "var(--fg-0)" }}>
              <span className="tabular">{w.running}</span><span style={{ color: "var(--fg-3)" }}>/{w.concurrency}</span>
            </td>
            <td style={{ padding: "2px 10px", color: "var(--accent)" }} className="tabular">{w.throughput}/m</td>
            <td style={{ padding: "2px 10px" }}><Spark data={w.hist} tone={w.color} width={70} height={14} /></td>
            <td style={{ padding: "2px 10px" }}>
              <span style={{ color: w.errRate > 2 ? "var(--red)" : w.errRate > 1 ? "var(--amber)" : "var(--fg-2)" }} className="tabular">
                {w.errRate.toFixed(2)}%
              </span>
            </td>
            <td style={{ padding: "2px 10px", color: "var(--fg-2)" }} className="tabular">{w.heartbeat}s</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function EventRow({ event }) {
  const a = ASSETS.find(x => x.id === event.assetId);
  const w = WORKER_INSTANCES.find(x => x.id === event.worker);
  return (
    <div className="event-row">
      <span className="t">{fmtClock(event.t)}</span>
      <span className={"k " + event.color}>{event.type.replace(/([A-Z])/g, ' $1').trim().split(' ').slice(-2).join(' ')}</span>
      <span className="body">
        <span className="worker">{w?.type || "—"}</span>
        {" "}
        <span style={{ color: "var(--fg-3)" }}>{event.assetType}</span>
        {" "}
        <span className="host">{event.assetValue}</span>
      </span>
    </div>
  );
}

Object.assign(window, { CommandCenter, EventRow, CardHeader });
