/* eslint-disable */
// EVENTS — full event stream w/ correlation.

function EventsPage({ liveEvents }) {
  const [typeFilter, setTypeFilter] = useState(new Set());
  const [selectedEvent, setSelectedEvent] = useState(EVENTS[0]);
  const events = (liveEvents && liveEvents.length ? liveEvents.slice(0, 30).concat(EVENTS) : EVENTS).slice(0, 300);

  const counts = {};
  for (const e of EVENTS) counts[e.type] = (counts[e.type] || 0) + 1;

  const filtered = typeFilter.size ? events.filter(e => typeFilter.has(e.type)) : events;

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="page-tabs">
        <div className="page-tab active">
          <span>Live Stream</span>
          <span className="tab-count">{EVENTS.length}</span>
        </div>
        <div className="page-tab">
          <span>Correlation Chains</span>
          <span className="tab-count">23</span>
        </div>
        <div className="page-tab">
          <span>Dead Letter</span>
          <span className="tab-count" style={{ color: "var(--red)" }}>4</span>
        </div>
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <span className="dot green pulse" />
          <span className="mono" style={{ fontSize: 10, color: "var(--fg-2)" }}>{liveEvents?.length || 0} new since open</span>
        </div>
      </div>

      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        {/* Type facet */}
        <ResizablePanel id="events-facets" side="left" defaultWidth={220} minWidth={120} maxWidth={360} label="Filters">
        <div className="facets" style={{ flex: 1, overflowY: "auto" }}>
          <div className="facet-head" style={{ borderBottom: "1px solid var(--line-1)" }}>
            <span>Event Type</span>
            <span className="count">{EVENT_TYPES.length}</span>
          </div>
          {EVENT_TYPES.map(et => (
            <div
              key={et.id}
              className={"facet-row" + (typeFilter.has(et.id) ? " active" : "")}
              onClick={() => {
                setTypeFilter(s => {
                  const n = new Set(s);
                  if (n.has(et.id)) n.delete(et.id); else n.add(et.id);
                  return n;
                });
              }}
            >
              <div className="check" />
              <span className="label">
                <span style={{ color: `var(--${et.color})`, marginRight: 6 }}>●</span>
                <span style={{ fontSize: 10.5 }}>{et.id}</span>
              </span>
              <span className="n">{counts[et.id] || 0}</span>
            </div>
          ))}

          <div className="facet-head" style={{ marginTop: 6 }}>
            <span>Severity</span>
            <span className="count">5</span>
          </div>
          {[["Critical", "red"], ["Error", "red"], ["Warn", "amber"], ["Info", "cyan"], ["Debug", "dim"]].map(([s, c]) => (
            <div key={s} className="facet-row">
              <div className="check" />
              <span className="label"><span className={"dot " + c} style={{ marginRight: 6 }} />{s}</span>
              <span className="n">—</span>
            </div>
          ))}
        </div>
        </ResizablePanel>

        {/* Stream */}
        <div style={{ overflow: "auto", background: "var(--bg-0)", fontFamily: "var(--font-mono)", flex: 1, minWidth: 0 }}>
          {filtered.map(e => {
            const a = ASSETS.find(x => x.id === e.assetId);
            const w = WORKER_INSTANCES.find(x => x.id === e.worker);
            return (
              <div
                key={e.id}
                onClick={() => setSelectedEvent(e)}
                style={{
                  display: "grid",
                  gridTemplateColumns: "12px 100px 90px 180px 1fr 70px",
                  gap: 10,
                  padding: "3px 12px",
                  borderBottom: "1px solid var(--line-0)",
                  alignItems: "baseline",
                  fontSize: 11,
                  cursor: "pointer",
                  background: selectedEvent?.id === e.id ? "var(--bg-2)" : "transparent",
                }}
              >
                <span className={"dot " + e.color} style={{ marginTop: 2 }} />
                <span style={{ color: "var(--fg-3)", fontSize: 10 }} className="tabular">{fmtClock(e.t)}</span>
                <span style={{ color: "var(--fg-3)", fontSize: 10 }}>{e.id}</span>
                <span style={{ color: `var(--${e.color})`, fontSize: 10.5, letterSpacing: "0.04em" }}>{e.type}</span>
                <span style={{ color: "var(--fg-1)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                  <span style={{ color: "var(--magenta)" }}>{w?.type || "—"}</span>
                  {" → "}
                  <span style={{ color: "var(--fg-3)" }}>{e.assetType}</span>
                  {" "}
                  <span style={{ color: "var(--cyan)" }}>{e.assetValue}</span>
                </span>
                <span style={{ color: "var(--fg-3)", fontSize: 10, textAlign: "right" }}>{fmtTime(e.t)} ago</span>
              </div>
            );
          })}
        </div>

        {/* Event detail */}
        <ResizablePanel id="events-inspector" side="right" defaultWidth={360} minWidth={200} maxWidth={580} label="Event Detail">
        <div className="inspector" style={{ border: "none", flex: 1 }}>
          <div className="inspector-head">
            <div className="type-row">
              <Pill tone="dim">Event</Pill>
              <Pill tone={selectedEvent.color}>{selectedEvent.type}</Pill>
              <span className="asset-id">{selectedEvent.id}</span>
            </div>
            <div className="mono" style={{ fontSize: 11, color: "var(--fg-2)", marginTop: 6 }}>
              {new Date(selectedEvent.t).toISOString().replace("T", " ").slice(0, -5)}Z
            </div>
          </div>

          <div className="inspector-body">
            <div className="section-label">ENVELOPE</div>
            <table className="kv-table">
              <tbody>
                <tr><td>event_id</td><td>{selectedEvent.id}</td></tr>
                <tr><td>event_type</td><td style={{ color: `var(--${selectedEvent.color})` }}>{selectedEvent.type}</td></tr>
                <tr><td>correlation</td><td>C-{selectedEvent.id.slice(2, 8)}</td></tr>
                <tr><td>causation</td><td>E-{(parseInt(selectedEvent.id.slice(2), 16) + 1).toString(16).toUpperCase()}</td></tr>
                <tr><td>chain_id</td><td>D-{selectedEvent.assetId.slice(2)}</td></tr>
                <tr><td>target_id</td><td>tgt_chronix</td></tr>
                <tr><td>asset_id</td><td>{selectedEvent.assetId}</td></tr>
                <tr><td>worker_inst</td><td style={{ color: "var(--magenta)" }}>{selectedEvent.worker}</td></tr>
              </tbody>
            </table>

            <div className="section-label">PAYLOAD</div>
            <pre style={{
              margin: 0, padding: "0 12px 12px", fontFamily: "var(--font-mono)", fontSize: 10.5,
              color: "var(--fg-2)", whiteSpace: "pre-wrap"
            }}>
{JSON.stringify({
  asset: {
    id: selectedEvent.assetId,
    type: selectedEvent.assetType,
    value: selectedEvent.assetValue,
  },
  source: {
    worker_type: WORKER_INSTANCES.find(w => w.id === selectedEvent.worker)?.type,
    worker_instance: selectedEvent.worker,
  },
  scope: { status: "InScope" },
}, null, 2)}
            </pre>

            <div className="section-label">DOWNSTREAM · {Math.floor(Math.random() * 4) + 2} workers matched</div>
            <div style={{ padding: "4px 12px 14px" }}>
              {WORKER_TYPES.slice(2, 6).map(wt => (
                <div key={wt.id} className="mono" style={{ display: "flex", justifyContent: "space-between", padding: "3px 0", fontSize: 11, color: "var(--fg-2)" }}>
                  <span style={{ color: `var(--${wt.color})` }}>{wt.id}</span>
                  <span style={{ color: "var(--fg-3)", fontSize: 10 }}>+1 task queued</span>
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

Object.assign(window, { EventsPage });
