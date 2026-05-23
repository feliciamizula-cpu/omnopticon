/* eslint-disable */
// WORKERS — fleet swimlane + type cards.

function WorkersPage() {
  const [selectedType, setSelectedType] = useState(null);

  // group worker instances by type
  const byType = useMemo(() => {
    const m = {};
    for (const w of WORKER_INSTANCES) {
      (m[w.type] = m[w.type] || []).push(w);
    }
    return m;
  }, []);

  return (
    <div className="workers-page">
      {/* Top: worker type cards grid */}
      <div className="swimlane">
        <div style={{ padding: 10 }}>
          <div className="mono-label" style={{ marginBottom: 8 }}>Worker Types · {WORKER_TYPES.length} · grouped by category</div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(290px, 1fr))", gap: 1, background: "var(--line-1)" }}>
            {WORKER_TYPES.map(wt => {
              const insts = byType[wt.id] || [];
              const running = insts.reduce((s, i) => s + i.running, 0);
              const capacity = insts.reduce((s, i) => s + i.concurrency, 0);
              const tp = insts.reduce((s, i) => s + i.throughput, 0);
              const errAvg = insts.reduce((s, i) => s + i.errRate, 0) / Math.max(1, insts.length);
              const issuesN = insts.filter(i => i.status === "Degraded" || i.status === "Failed").length;
              return (
                <div
                  key={wt.id}
                  onClick={() => setSelectedType(wt.id === selectedType ? null : wt.id)}
                  style={{
                    background: selectedType === wt.id ? "var(--bg-3)" : "var(--bg-1)",
                    padding: "8px 10px",
                    cursor: "pointer",
                    borderTop: `2px solid var(--${wt.color})`,
                  }}
                >
                  <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                    <span style={{ color: `var(--${wt.color})`, fontWeight: 600, fontSize: 12 }} className="cond uppercase">{wt.id}</span>
                    <span className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)" }}>{wt.cat}</span>
                    <span style={{ marginLeft: "auto" }}>
                      {issuesN > 0
                        ? <Pill tone="amber">⚠ {issuesN}</Pill>
                        : <Pill tone="green">●  OK</Pill>}
                    </span>
                  </div>
                  <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 1fr", gap: 6, marginTop: 8 }}>
                    <Tiny l="instances" v={insts.length} />
                    <Tiny l="tasks" v={`${running}/${capacity}`} />
                    <Tiny l="throughput" v={`${tp}/m`} tone="accent" />
                  </div>
                  <div style={{ marginTop: 6, display: "flex", alignItems: "center", gap: 6 }}>
                    <Spark data={[...(insts[0]?.hist || [3, 4, 5, 6, 5, 7, 8, 7, 9, 8])]} tone={wt.color} width={130} height={18} />
                    <span className="mono tabular" style={{ fontSize: 9.5, color: errAvg > 2 ? "var(--red)" : "var(--fg-3)" }}>err {errAvg.toFixed(2)}%</span>
                  </div>
                  <div style={{ marginTop: 6, fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--fg-3)" }}>
                    <span style={{ color: "var(--cyan)" }}>{wt.inputs.join(", ")}</span>
                    <span style={{ margin: "0 6px", color: "var(--fg-3)" }}>→</span>
                    <span style={{ color: "var(--magenta)" }}>{wt.outputs.join(", ") || "—"}</span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Bottom: swimlane gantt */}
      <div className="swimlane" style={{ borderTop: "1px solid var(--line-2)" }}>
        <div style={{ display: "flex", alignItems: "center", padding: "6px 10px", borderBottom: "1px solid var(--line-1)", gap: 12 }}>
          <span className="mono-label">Worker Activity · last 5 min</span>
          <div style={{ display: "flex", gap: 8, marginLeft: "auto", fontFamily: "var(--font-mono)", fontSize: 10 }}>
            <LegendDot label="Running"     color="accent" />
            <LegendDot label="Completed"   color="green" />
            <LegendDot label="Checkpointed" color="amber" />
            <LegendDot label="Failed"      color="red" />
            <LegendDot label="Discovered Asset" color="magenta" />
          </div>
        </div>
        <Swimlane />
      </div>
    </div>
  );
}

function Tiny({ l, v, tone }) {
  return (
    <div style={{ background: "var(--bg-2)", padding: "3px 6px" }}>
      <div className="mono" style={{ fontSize: 8.5, color: "var(--fg-3)", letterSpacing: "0.16em", textTransform: "uppercase" }}>{l}</div>
      <div className="mono tabular" style={{ fontSize: 12, color: tone === "accent" ? "var(--accent)" : "var(--fg-0)" }}>{v}</div>
    </div>
  );
}

function LegendDot({ label, color }) {
  return (
    <span style={{ display: "inline-flex", alignItems: "center", gap: 4, color: "var(--fg-3)" }}>
      <span style={{ width: 9, height: 9, background: `var(--${color})`, display: "inline-block" }} />
      {label}
    </span>
  );
}

function Swimlane() {
  // Build synthetic timeline tasks for each worker instance
  const TRACK_W = 1200;
  const RANGE_MS = 5 * 60 * 1000;

  const tracks = useMemo(() => {
    const out = {};
    let seed = 7;
    function rand() { seed = (seed * 9301 + 49297) % 233280; return seed / 233280; }
    for (const w of WORKER_INSTANCES) {
      const blocks = [];
      let t = 0;
      while (t < RANGE_MS) {
        const dur = 2000 + rand() * 18000;
        const status = rand() < 0.7 ? "completed" : rand() < 0.5 ? "checkpointed" : rand() < 0.7 ? "failed" : "running";
        blocks.push({ start: t, dur: Math.min(dur, RANGE_MS - t), status, asset: ["api.chronix.io", "/api/v1/users", "vendor.b771.js", "admin.chronix.io"][Math.floor(rand() * 4)] });
        t += dur + 500;
      }
      // mark last one as running
      if (w.status === "Busy" || w.status === "Healthy" && w.running > 0) {
        const last = blocks[blocks.length - 1];
        if (last) last.status = "running";
      }
      out[w.id] = blocks;
    }
    return out;
  }, []);

  return (
    <div style={{ overflow: "auto" }}>
      {/* time axis */}
      <div style={{ display: "grid", gridTemplateColumns: "180px 1fr", borderBottom: "1px solid var(--line-1)", background: "var(--bg-2)", position: "sticky", top: 0, zIndex: 1 }}>
        <div style={{ padding: "4px 12px", fontFamily: "var(--font-mono)", fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.14em" }}>WORKER INSTANCE</div>
        <div style={{ position: "relative", height: 22 }}>
          {[0, 1, 2, 3, 4, 5].map(i => (
            <div key={i} style={{ position: "absolute", left: (i / 5) * 100 + "%", top: 0, bottom: 0, borderLeft: "1px solid var(--line-1)", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 9.5, padding: "4px 4px" }}>
              T-{5 - i}m
            </div>
          ))}
        </div>
      </div>

      {WORKER_INSTANCES.map(w => (
        <div key={w.id} className="swim-row" style={{ display: "grid", gridTemplateColumns: "180px 1fr" }}>
          <div style={{
            padding: "5px 12px", borderBottom: "1px solid var(--line-0)", borderRight: "1px solid var(--line-1)",
            background: "var(--bg-1)", display: "flex", alignItems: "center", gap: 6, fontFamily: "var(--font-mono)", fontSize: 11,
          }}>
            <span className={"dot " + (w.status === "Healthy" ? "green" : w.status === "Busy" ? "cyan" : w.status === "Degraded" ? "amber" : "red")} />
            <span style={{ color: `var(--${w.color})`, fontWeight: 600 }}>{w.type}</span>
            <span style={{ color: "var(--fg-3)", marginLeft: "auto", fontSize: 9.5 }}>{w.id.split("-").pop()}</span>
          </div>
          <div style={{ position: "relative", height: 26, borderBottom: "1px solid var(--line-0)", background: "var(--bg-0)" }}>
            {/* grid */}
            {[0, 1, 2, 3, 4, 5].map(i => (
              <div key={i} style={{ position: "absolute", left: (i / 5) * 100 + "%", top: 0, bottom: 0, borderLeft: "1px solid var(--line-0)" }} />
            ))}
            {(tracks[w.id] || []).map((b, i) => {
              const cls = b.status === "running" ? "running" : b.status === "completed" ? "green" : b.status === "checkpointed" ? "amber" : b.status === "failed" ? "red" : "cyan";
              return (
                <div
                  key={i}
                  className={"task-block " + cls}
                  style={{
                    left: (b.start / RANGE_MS) * 100 + "%",
                    width: Math.max(0.5, (b.dur / RANGE_MS) * 100) + "%",
                  }}
                  title={`${b.status} · ${b.asset}`}
                >
                  {b.dur > 4000 ? b.asset.slice(0, 18) : ""}
                </div>
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

Object.assign(window, { WorkersPage });
