/* eslint-disable */
// TASKS — task runs with checkpointed progress.

function TasksPage() {
  const [filter, setFilter] = useState("all");
  const [selected, setSelected] = useState(TASKS[0]);

  const filtered = filter === "all" ? TASKS
                 : filter === "active" ? TASKS.filter(t => ["Running", "Leased", "Checkpointed", "Queued"].includes(t.state))
                 : filter === "failed" ? TASKS.filter(t => ["Failed", "HeartbeatLost", "RetryPending"].includes(t.state))
                 : TASKS.filter(t => t.state === "Checkpointed");

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="page-tabs">
        {[
          ["all", "All Tasks", TASKS.length],
          ["active", "Active", TASKS.filter(t => ["Running", "Leased", "Checkpointed", "Queued"].includes(t.state)).length],
          ["failed", "Failed / Retrying", TASKS.filter(t => ["Failed", "HeartbeatLost", "RetryPending"].includes(t.state)).length],
          ["check", "Resumable", TASKS.filter(t => t.state === "Checkpointed").length],
        ].map(([id, label, n]) => (
          <div key={id} className={"page-tab " + (filter === id ? "active" : "")} onClick={() => setFilter(id)}>
            <span>{label}</span>
            <span className="tab-count">{n}</span>
          </div>
        ))}
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <button className="btn ghost tiny">Pause All</button>
          <button className="btn ghost tiny">Drain</button>
          <span className="dot green pulse" />
        </div>
      </div>

      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <ArgusDataGrid
            columns={[
              { key: "_icon", label: "", className: "c-icon", width: 18 },
              { key: "id", label: "Task ID", className: "c-type", width: 80, render: (t) => <span style={{ color: "var(--accent)" }}>{t.id}</span> },
              { key: "type", label: "Worker Type", className: "c-type", width: 120, render: (t) => <span style={{ color: "var(--fg-1)" }}>{t.type}</span> },
              { key: "worker", label: "Worker Instance", className: "c-worker", width: 160 },
              { key: "asset", label: "Asset", className: "c-value", width: 200, render: (t) => <><TypeGlyph type={t.assetType} /> <span className="mono" style={{ fontSize: 10.5, color: "var(--fg-2)" }}>{t.assetType}</span> <span style={{ color: "var(--fg-0)" }}>{t.assetValue}</span></> },
              { key: "state", label: "State", className: "c-status", width: 100, render: (t) => <TaskStatePill state={t.state} /> },
              { key: "progress", label: "Progress", className: "c-conf", width: 110, render: (t) => (
                <span style={{ display: "inline-flex", alignItems: "center", gap: 5 }}>
                  <Bar value={t.progress} tone={t.state === "Failed" ? "red" : t.state === "Checkpointed" ? "amber" : "cyan"} width={62} />
                  <span className="tabular" style={{ fontSize: 10, color: "var(--fg-2)", width: 28 }}>{t.progress}%</span>
                </span>
              )},
              { key: "attempt", label: "Att.", className: "c-risk", width: 60, render: (t) => <span className="tabular">{t.attempt}/{t.maxAttempts}</span> },
              { key: "duration", label: "Duration", className: "c-int", width: 80, render: (t) => <span className="tabular">{fmtDur(t.duration)}</span> },
              { key: "startedAt", label: "Started", className: "c-seen", width: 90, render: (t) => <span className="tabular">{fmtTime(t.startedAt)} ago</span> },
            ]}
            rows={filtered.slice(0, 220)}
            rowKey="id"
            selectedId={selected?.id}
            onSelect={setSelected}
          />
        </div>

        <ResizablePanel id="tasks-inspector" side="right" defaultWidth={380} minWidth={220} maxWidth={600} label="Task Detail">
        <div style={{ background: "var(--bg-1)", display: "flex", flexDirection: "column", overflow: "hidden", flex: 1 }}>
          <div style={{ padding: "12px 14px", borderBottom: "1px solid var(--line-1)", background: "linear-gradient(180deg, var(--bg-2), var(--bg-1))" }}>
            <div style={{ display: "flex", gap: 6, alignItems: "center", marginBottom: 6 }}>
              <Pill tone="dim">Task Run</Pill>
              <TaskStatePill state={selected.state} />
              <span className="mono" style={{ marginLeft: "auto", color: "var(--fg-3)", fontSize: 10 }}>{selected.id}</span>
            </div>
            <div className="mono" style={{ fontSize: 14, color: "var(--fg-0)" }}>{selected.type}</div>
            <div className="mono" style={{ fontSize: 11, color: "var(--fg-2)", marginTop: 4 }}>
              on <span style={{ color: "var(--cyan)" }}>{selected.assetValue}</span>
            </div>
          </div>

          <div style={{ overflow: "auto", flex: 1 }}>
            <div className="section-label">EXECUTION</div>
            <table className="kv-table">
              <tbody>
                <tr><td>Worker</td><td style={{ color: "var(--magenta)" }}>{selected.worker}</td></tr>
                <tr><td>Lease Owner</td><td>{selected.worker}</td></tr>
                <tr><td>Attempt</td><td>{selected.attempt} / {selected.maxAttempts}</td></tr>
                <tr><td>Started</td><td>{fmtTime(selected.startedAt)} ago</td></tr>
                <tr><td>Duration</td><td>{fmtDur(selected.duration)}</td></tr>
                <tr><td>Progress</td><td>{selected.progress}%</td></tr>
                {selected.errorCode && <tr><td>Error</td><td style={{ color: "var(--red)" }}>{selected.errorCode}</td></tr>}
              </tbody>
            </table>

            {selected.checkpointJson && (
              <>
                <div className="section-label">CHECKPOINT · resumable</div>
                <div style={{ padding: "0 12px 12px" }}>
                  <div style={{ background: "var(--bg-0)", border: "1px solid var(--line-1)", padding: "8px 10px" }}>
                    <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.12em" }}>RESUME TOKEN · v3</div>
                    <pre className="mono" style={{ margin: "4px 0 0", fontSize: 10.5, color: "var(--amber)", whiteSpace: "pre-wrap" }}>{selected.checkpointJson}</pre>
                  </div>
                  <div style={{ display: "flex", gap: 4, marginTop: 8 }}>
                    <button className="btn ghost tiny">Resume Now</button>
                    <button className="btn ghost tiny">Inspect Cursor</button>
                    <button className="btn danger tiny">Abandon</button>
                  </div>
                  <div className="mono" style={{ marginTop: 8, fontSize: 10, color: "var(--fg-3)", lineHeight: 1.6 }}>
                    Lock owner crashed at <span style={{ color: "var(--fg-1)" }}>{fmtTime(selected.startedAt + selected.duration)}</span> ago.
                    Lease expires in <span style={{ color: "var(--amber)" }}>32s</span>; another worker instance of type <span style={{ color: "var(--magenta)" }}>{selected.type}</span> may claim and resume from this checkpoint.
                  </div>
                </div>
              </>
            )}

            <div className="section-label">STEP TRACE</div>
            <div style={{ padding: "4px 12px" }}>
              {[
                ["lease.acquire", "completed", "8ms"],
                ["resolve.scope", "completed", "12ms"],
                ["fetch.headers", "completed", "184ms"],
                ["parse.document", "completed", "47ms"],
                ["extract.links", selected.state === "Running" ? "running" : "completed", "—"],
                ["checkpoint.write", selected.state === "Checkpointed" ? "completed" : "—", "—"],
                ["emit.assets", selected.state === "Succeeded" ? "completed" : "—", "—"],
              ].map(([s, st, d], i) => (
                <div key={i} className="mono" style={{ display: "grid", gridTemplateColumns: "10px 1fr 70px 40px", gap: 6, padding: "2px 0", fontSize: 10.5, alignItems: "center" }}>
                  <span style={{
                    width: 8, height: 8,
                    background: st === "completed" ? "var(--green)" : st === "running" ? "var(--accent)" : "var(--line-2)",
                  }} />
                  <span style={{ color: "var(--fg-1)" }}>{s}</span>
                  <span style={{ color: st === "completed" ? "var(--green)" : st === "running" ? "var(--accent)" : "var(--fg-3)" }}>{st}</span>
                  <span className="tabular" style={{ color: "var(--fg-3)", textAlign: "right" }}>{d}</span>
                </div>
              ))}
            </div>
          </div>
        </div>
        </ResizablePanel>
      </div>
    </div>
  );
}

function TaskStateGlyph({ state }) {
  const map = {
    Running: <span className="dot cyan pulse" />,
    Queued: <span className="dot amber" />,
    Leased: <span className="dot cyan" />,
    Checkpointed: <span className="dot amber pulse" />,
    Succeeded: <span className="dot green" />,
    Failed: <span className="dot red" />,
    RetryPending: <span className="dot amber" />,
    HeartbeatLost: <span className="dot red pulse" />,
  };
  return map[state] || <span className="dot" />;
}

function TaskStatePill({ state }) {
  const map = {
    Running: "cyan", Queued: "dim", Leased: "cyan",
    Checkpointed: "amber", Succeeded: "green", Failed: "red",
    RetryPending: "amber", HeartbeatLost: "red", Expired: "dim",
  };
  return <Pill tone={map[state]}>{state}</Pill>;
}

Object.assign(window, { TasksPage });
