/* eslint-disable */
// ASSETS PAGE — the centerpiece grid.

function AssetExplorer({ liveTick }) {
  const [selected, setSelected] = useState(() => ASSETS.find((a) => a.type === "JavaScriptFile") || ASSETS[0]);
  const [multiSel, setMultiSel] = useState(new Set());
  const [filters, setFilters] = useState({
    types: new Set(),
    statuses: new Set(),
    tags: new Set(),
    workers: new Set(),
    riskMin: 0,
  });
  const [sortKey, setSortKey] = useState("interest");
  const [sortDir, setSortDir] = useState("desc");
  const [tokens, setTokens] = useState([
    { k: "scope", v: "InScope" },
    { k: "type", v: "ApiEndpoint,JavaScriptFile,HtmlPage" },
  ]);
  const [search, setSearch] = useState("");
  const [view, setView] = useState("table"); // table | chain | timeline
  const [flashRow, setFlashRow] = useState(null);
  const [inspTab, setInspTab] = useState("detail");

  // Live tick: occasionally bump a random asset's lastSeen and flash it.
  useEffect(() => {
    if (!liveTick) return;
    const a = ASSETS[(liveTick * 31) % ASSETS.length];
    setFlashRow(a.id);
    const t = setTimeout(() => setFlashRow(null), 1800);
    return () => clearTimeout(t);
  }, [liveTick]);

  // ---- counts for facets ----
  const counts = useMemo(() => {
    const byType = {};
    const byStatus = {};
    const byTag = {};
    const byWorker = {};
    for (const a of ASSETS) {
      byType[a.type] = (byType[a.type] || 0) + 1;
      byStatus[a.status] = (byStatus[a.status] || 0) + 1;
      for (const t of a.tags) byTag[t] = (byTag[t] || 0) + 1;
      byWorker[a.worker] = (byWorker[a.worker] || 0) + 1;
    }
    return { byType, byStatus, byTag, byWorker };
  }, []);

  // ---- filtered & sorted rows ----
  const rows = useMemo(() => {
    let r = ASSETS;
    if (filters.types.size) r = r.filter((a) => filters.types.has(a.type));
    if (filters.statuses.size) r = r.filter((a) => filters.statuses.has(a.status));
    if (filters.tags.size) r = r.filter((a) => a.tags.some((t) => filters.tags.has(t)));
    if (filters.workers.size) r = r.filter((a) => filters.workers.has(a.worker));
    if (filters.riskMin > 0) r = r.filter((a) => a.risk >= filters.riskMin);
    if (search) r = r.filter((a) => a.value.toLowerCase().includes(search.toLowerCase()));
    // apply tokens
    for (const t of tokens) {
      if (t.k === "type") {
        const vals = t.v.split(",");
        r = r.filter((a) => vals.includes(a.type));
      }
      if (t.k === "scope" || t.k === "status") r = r.filter((a) => a.status === t.v || a.scope === t.v);
      if (t.k === "tag") r = r.filter((a) => a.tags.includes(t.v));
      if (t.k === "risk>=") r = r.filter((a) => a.risk >= +t.v);
    }
    r = [...r].sort((a, b) => {
      const dir = sortDir === "asc" ? 1 : -1;
      const av = a[sortKey];
      const bv = b[sortKey];
      if (typeof av === "number") return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
    return r;
  }, [filters, sortKey, sortDir, tokens, search]);

  const toggleSet = (k, v) =>
    setFilters((f) => {
      const s = new Set(f[k]);
      if (s.has(v)) s.delete(v); else s.add(v);
      return { ...f, [k]: s };
    });

  const toggleSort = (k) => {
    if (sortKey === k) setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    else { setSortKey(k); setSortDir("desc"); }
  };

  const sortArrow = (k) => sortKey === k ? (sortDir === "asc" ? "▲" : "▼") : "";

  const onRowClick = (a, e) => {
    if (e.metaKey || e.ctrlKey) {
      setMultiSel((s) => {
        const ns = new Set(s);
        if (ns.has(a.id)) ns.delete(a.id); else ns.add(a.id);
        return ns;
      });
    } else {
      setSelected(a);
      setMultiSel(new Set());
    }
  };

  // page tabs
  const [pageTab, setPageTab] = useState("all");

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      {/* page tabs */}
      <div className="page-tabs">
        {[
          ["all", "All Assets", rows.length],
          ["new", "New", ASSETS.filter(a => a.status === "New").length],
          ["high", "High-Value", ASSETS.filter(a => a.interest >= 70).length],
          ["findings", "Findings", ASSETS.filter(a => a.type === "FindingCandidate").length],
          ["oos", "Out-of-Scope", ASSETS.filter(a => a.status === "OutOfScope").length],
        ].map(([id, label, n]) => (
          <div key={id} className={`page-tab ${pageTab === id ? "active" : ""}`} onClick={() => setPageTab(id)}>
            <span>{label}</span>
            <span className="tab-count">{fmtNum(n)}</span>
          </div>
        ))}
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <span className="mono-label">stream</span>
          <span className="dot green pulse" />
        </div>
      </div>

      {/* toolbar */}
      <div className="asset-toolbar">
        <div className="qsearch">
          {ICONS.search}
          {tokens.map((t, i) => (
            <span key={i} className="token">
              <span className="k">{t.k}:</span>
              <span>{t.v}</span>
              <span className="x" onClick={() => setTokens(tokens.filter((_, j) => j !== i))}>×</span>
            </span>
          ))}
          <input
            placeholder="filter assets — key:value or text…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <span className="search-kbd">/</span>
        </div>

        <div className="viewmode">
          {["table", "chain", "timeline"].map(v => (
            <button key={v} className={view === v ? "active" : ""} onClick={() => setView(v)}>{v}</button>
          ))}
        </div>

        <button className="btn ghost tiny">{ICONS.filter}<span>Saved Filters</span></button>
        <button className="btn ghost tiny">{ICONS.download}<span>Export</span></button>

        <div style={{ flex: 1 }} />

        <div className="toolbar-stat">
          <span className="l">Visible</span>
          <span className="v accent tabular">{rows.length.toLocaleString()}</span>
        </div>
        <div className="toolbar-stat">
          <span className="l">Total</span>
          <span className="v tabular">{ASSETS.length.toLocaleString()}</span>
        </div>
        <div className="toolbar-stat">
          <span className="l">In-Scope</span>
          <span className="v tabular">{ASSETS.filter(a => a.status === "InScope").length}</span>
        </div>
        <div className="toolbar-stat">
          <span className="l">Hot</span>
          <span className="v" style={{ color: "var(--magenta)" }}>{ASSETS.filter(a => a.tags.includes("hot")).length}</span>
        </div>
      </div>

      {/* main 3-pane */}
      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        <ResizablePanel id="assets-facets" side="left" defaultWidth={220} minWidth={120} maxWidth={360} label="Filters">
          <FacetsPanel filters={filters} setFilters={setFilters} toggleSet={toggleSet} counts={counts} />
        </ResizablePanel>

        <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0, minHeight: 0 }}>
          {view === "table" && (
            <AssetGrid
              rows={rows} selected={selected} setSelected={setSelected}
              multiSel={multiSel} setMultiSel={setMultiSel}
              onRowClick={onRowClick} sortKey={sortKey} sortDir={sortDir} toggleSort={toggleSort}
              sortArrow={sortArrow} flashRow={flashRow}
            />
          )}
          {view === "chain" && <ChainView rows={rows} selected={selected} setSelected={setSelected} />}
          {view === "timeline" && <TimelineView rows={rows} selected={selected} setSelected={setSelected} />}
        </div>

        <ResizablePanel id="assets-inspector" side="right" defaultWidth={360} minWidth={200} maxWidth={600} label="Inspector">
          <Inspector asset={selected} tab={inspTab} setTab={setInspTab} />
        </ResizablePanel>
      </div>
    </div>
  );
}

// ============================================================ FACETS

function FacetsPanel({ filters, setFilters, toggleSet, counts }) {
  // histogram for risk
  const hist = useMemo(() => {
    const buckets = new Array(10).fill(0);
    for (const a of ASSETS) buckets[Math.min(9, Math.floor(a.risk / 10))]++;
    return buckets;
  }, []);

  return (
    <div className="facets">
      <FacetGroup label="Asset Type" count={ASSET_TYPES.length}>
        {ASSET_TYPES.filter((t) => counts.byType[t.id]).map((t) => (
          <FacetRow
            key={t.id}
            active={filters.types.has(t.id)}
            onClick={() => toggleSet("types", t.id)}
            label={<><span className={`mono text-${t.color}`} style={{ marginRight: 6, display: "inline-block", width: 12 }}>{t.glyph}</span>{t.id}</>}
            n={counts.byType[t.id]}
            bar={counts.byType[t.id] / Math.max(...Object.values(counts.byType))}
          />
        ))}
      </FacetGroup>

      <FacetGroup label="Status" count={STATUSES.length}>
        {STATUSES.map((s) => counts.byStatus[s.id] && (
          <FacetRow
            key={s.id}
            active={filters.statuses.has(s.id)}
            onClick={() => toggleSet("statuses", s.id)}
            label={<><span className={`dot ${s.color === "dim" ? "" : s.color}`} style={{ marginRight: 6 }} />{s.id}</>}
            n={counts.byStatus[s.id] || 0}
            bar={(counts.byStatus[s.id] || 0) / ASSETS.length}
          />
        ))}
      </FacetGroup>

      <FacetGroup label="Risk Score" count="0–100">
        <div className="facet-range">
          <div className="hist">
            {hist.map((b, i) => (
              <div
                key={i}
                className={"b" + (filters.riskMin / 10 <= i ? " hl" : "")}
                style={{ height: `${Math.min(100, b * 4)}%` }}
              />
            ))}
          </div>
          <div className="track">
            <div className="selected" style={{ left: filters.riskMin + "%", right: 0 }} />
          </div>
          <div className="legend">
            <span>min: {filters.riskMin}</span>
            <span>max: 100</span>
          </div>
          <input
            type="range" min="0" max="100" step="10"
            value={filters.riskMin}
            onChange={(e) => setFilters({ ...filters, riskMin: +e.target.value })}
            style={{ width: "100%" }}
          />
        </div>
      </FacetGroup>

      <FacetGroup label="Tags" count={Object.keys(counts.byTag).length}>
        {Object.entries(counts.byTag)
          .sort((a, b) => b[1] - a[1])
          .slice(0, 12)
          .map(([t, n]) => (
            <FacetRow
              key={t}
              active={filters.tags.has(t)}
              onClick={() => toggleSet("tags", t)}
              label={<TagChip tag={t} />}
              n={n}
              bar={n / Math.max(...Object.values(counts.byTag))}
            />
          ))}
      </FacetGroup>

      <FacetGroup label="Discovered By" count={Object.keys(counts.byWorker).length}>
        {Object.entries(counts.byWorker)
          .sort((a, b) => b[1] - a[1])
          .map(([w, n]) => (
            <FacetRow
              key={w}
              active={filters.workers.has(w)}
              onClick={() => toggleSet("workers", w)}
              label={<span className="mono" style={{ color: "var(--fg-1)" }}>{w}</span>}
              n={n}
              bar={n / Math.max(...Object.values(counts.byWorker))}
            />
          ))}
      </FacetGroup>
    </div>
  );
}

function FacetGroup({ label, count, children }) {
  const [open, setOpen] = useState(true);
  return (
    <div className="facet-group">
      <div className="facet-head" onClick={() => setOpen(!open)}>
        {open ? ICONS.caretDown : ICONS.caretRight}
        <span>{label}</span>
        <span className="count">{count}</span>
      </div>
      {open && <div className="facet-body">{children}</div>}
    </div>
  );
}

function FacetRow({ active, onClick, label, n, bar }) {
  return (
    <div className={"facet-row" + (active ? " active" : "")} onClick={onClick}>
      <div className="check" />
      <span className="label">{label}</span>
      <span className="mini-bar"><span className="f" style={{ width: Math.round(bar * 100) + "%" }} /></span>
      <span className="n">{n}</span>
    </div>
  );
}

// ============================================================ GRID

function AssetGrid({ rows, selected, setSelected, multiSel, setMultiSel, onRowClick, sortKey, sortDir, sortArrow, toggleSort, flashRow }) {
  const columns = [
    { key: "_check", label: "", className: "c-check", width: 22 },
    { key: "_icon", label: "", className: "c-icon", width: 18 },
    { key: "type", label: "Type", className: "c-type", width: 88 },
    { key: "value", label: "Value", className: "c-value", width: 280 },
    { key: "status", label: "Status", className: "c-status", width: 92 },
    { key: "scope", label: "Scope", className: "c-scope", width: 78 },
    { key: "confidence", label: "Conf", className: "c-conf", width: 84 },
    { key: "risk", label: "Risk", className: "c-risk", width: 64 },
    { key: "interest", label: "Interest", className: "c-int", width: 68 },
    { key: "tags", label: "Tags", className: "c-tags", width: 180 },
    { key: "parentValue", label: "Parent", className: "c-parent", width: 200 },
    { key: "worker", label: "Worker", className: "c-worker", width: 130 },
    { key: "lastSeen", label: "Last Seen", className: "c-seen", width: 90 },
  ];

  const renderers = {
    "_check": (a) => (
      <span
        className={"cell-checkbox" + (multiSel.has(a.id) ? " on" : "")}
        onClick={(e) => {
          e.stopPropagation();
          setMultiSel((s) => {
            const ns = new Set(s);
            if (ns.has(a.id)) ns.delete(a.id); else ns.add(a.id);
            return ns;
          });
        }}
      />
    ),
    "_icon": (a) => <TypeGlyph type={a.type} />,
    "type": (a) => <span className="mono" style={{ color: "var(--fg-2)", fontSize: 10.5 }}>{a.type}</span>,
    "value": (a) => <ValueCell asset={a} />,
    "status": (a) => <StatusPill status={a.status} />,
    "scope": (a) => a.scope === "InScope"
      ? <span style={{ color: "var(--green)" }}>IN</span>
      : a.scope === "OutOfScope" ? <span style={{ color: "var(--fg-3)" }}>OUT</span>
      : <span style={{ color: "var(--fg-2)" }}>{a.scope}</span>,
    "confidence": (a) => (
      <span style={{ display: "inline-flex", alignItems: "center", gap: 4 }}>
        <Bar value={a.confidence} tone={a.confidence > 85 ? "green" : a.confidence > 60 ? "amber" : "red"} width={36} />
        <span className="tabular" style={{ fontSize: 10, color: "var(--fg-2)" }}>{a.confidence}</span>
      </span>
    ),
    "risk": (a) => (
      <span className="risk-num">
        <span className="n tabular" style={{ color: a.risk > 70 ? "var(--red)" : a.risk > 40 ? "var(--amber)" : "var(--fg-2)" }}>{a.risk}</span>
        <LED value={a.risk} max={8} tone={a.risk > 70 ? "red" : a.risk > 40 ? "amber" : "green"} />
      </span>
    ),
    "interest": (a) => (
      <span className="risk-num">
        <span className="n tabular" style={{ color: a.interest > 70 ? "var(--magenta)" : "var(--fg-1)" }}>{a.interest}</span>
        <LED value={a.interest} max={8} tone={a.interest > 70 ? "magenta" : "cyan"} />
      </span>
    ),
    "tags": (a) => (
      <>
        {a.tags.slice(0, 4).map((t) => <TagChip key={t} tag={t} />)}
        {a.tags.length > 4 && <span className="mono" style={{ color: "var(--fg-3)", fontSize: 10 }}>+{a.tags.length - 4}</span>}
      </>
    ),
    "parentValue": (a) => a.parentValue && <span className="mono" style={{ fontSize: 10.5 }}>↑ {a.parentValue}</span>,
    "worker": (a) => <span className="mono" style={{ fontSize: 10.5 }}>{a.worker}</span>,
    "lastSeen": (a) => <span className="tabular">{fmtTime(a.lastSeen)} ago</span>,
  };

  return (
    <>
      <ArgusDataGrid
        columns={columns}
        rows={rows.slice(0, 200)}
        rowKey="id"
        selectedId={selected?.id}
        onSelect={onRowClick}
        multiSel={multiSel}
        onMultiSel={setMultiSel}
        sortKey={sortKey}
        sortDir={sortDir}
        onSort={toggleSort}
      />
      {multiSel.size > 0 && (
        <div className="bulkbar">
          <span className="count">{multiSel.size}</span>
          <span className="mono-label" style={{ letterSpacing: "0.14em" }}>Selected</span>
          <span style={{ width: 1, height: 18, background: "var(--line-2)", margin: "0 4px" }} />
          <button className="btn ghost tiny">Tag…</button>
          <button className="btn ghost tiny">Enqueue Worker…</button>
          <button className="btn ghost tiny">Mark High-Value</button>
          <button className="btn ghost tiny">Move to Scope</button>
          <button className="btn danger tiny">Reject</button>
          <button className="btn primary tiny">Export</button>
          <span className="icon-btn" onClick={() => setMultiSel(new Set())}>×</span>
        </div>
      )}
    </>
  );
}

// ============================================================ CHAIN (graph)

function ChainView({ rows, selected, setSelected }) {
  // Build a hierarchy: group by chain
  // Simple radial-ish 3-col layout
  const root = ASSETS.find(a => a.type === "Domain");
  const subs = ASSETS.filter(a => a.type === "Subdomain").slice(0, 18);
  const childMap = {};
  for (const a of ASSETS) {
    if (a.parent) {
      (childMap[a.parent] = childMap[a.parent] || []).push(a);
    }
  }

  return (
    <div style={{ flex: 1, overflow: "auto", background: "var(--bg-0)", padding: 24, position: "relative" }}>
      <svg width="100%" height="100%" style={{ position: "absolute", inset: 0, pointerEvents: "none" }}>
        <defs>
          <pattern id="grid" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M 20 0 L 0 0 0 20" fill="none" stroke="var(--line-0)" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="100%" height="100%" fill="url(#grid)" />
      </svg>

      <div style={{ position: "relative", display: "grid", gridTemplateColumns: "200px 280px 1fr", gap: 24, minWidth: 900 }}>
        {/* Root */}
        <div style={{ display: "flex", flexDirection: "column", justifyContent: "center" }}>
          <ChainNode asset={root} large selected={selected?.id === root?.id} onClick={() => setSelected(root)} />
        </div>

        {/* Subs */}
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <div className="mono-label" style={{ marginBottom: 4 }}>Subdomains · {subs.length}</div>
          {subs.map((s) => (
            <div key={s.id} style={{ position: "relative" }}>
              <span style={{ position: "absolute", left: -24, top: "50%", width: 24, height: 1, background: "var(--line-2)" }} />
              <ChainNode asset={s} selected={selected?.id === s.id} onClick={() => setSelected(s)} />
            </div>
          ))}
        </div>

        {/* Children of selected */}
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <div className="mono-label" style={{ marginBottom: 4 }}>
            Discovered from <span style={{ color: "var(--fg-0)" }}>{selected?.value}</span>
          </div>
          {(childMap[selected?.id] || []).slice(0, 20).map((c) => (
            <div key={c.id} style={{ position: "relative" }}>
              <span style={{ position: "absolute", left: -24, top: "50%", width: 24, height: 1, background: "var(--line-2)" }} />
              <ChainNode asset={c} small selected={false} onClick={() => setSelected(c)} />
            </div>
          ))}
          {!(childMap[selected?.id] || []).length && (
            <div className="mono" style={{ color: "var(--fg-3)", fontSize: 11, padding: "8px 0" }}>
              ── leaf node; no children produced ──
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function ChainNode({ asset, large, small, selected, onClick }) {
  if (!asset) return null;
  return (
    <div
      onClick={onClick}
      style={{
        background: selected ? "var(--bg-3)" : "var(--bg-1)",
        border: `1px solid ${selected ? "var(--accent)" : "var(--line-1)"}`,
        padding: small ? "4px 8px" : large ? "12px 14px" : "6px 10px",
        cursor: "pointer",
        display: "flex",
        flexDirection: "column",
        gap: 3,
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
        <TypeGlyph type={asset.type} />
        <span className="mono" style={{ fontSize: small ? 9.5 : 10, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.08em" }}>{asset.type}</span>
        <span style={{ marginLeft: "auto" }}><StatusPill status={asset.status} /></span>
      </div>
      <div className="mono" style={{ fontSize: large ? 14 : small ? 11 : 12, color: "var(--fg-0)", wordBreak: "break-all", lineHeight: 1.25 }}>
        {asset.value}
      </div>
      {!small && (
        <div style={{ display: "flex", gap: 8, alignItems: "center", marginTop: 2, fontSize: 9.5, color: "var(--fg-3)" }} className="mono">
          <span>RISK <span style={{ color: "var(--fg-1)" }}>{asset.risk}</span></span>
          <span>INT <span style={{ color: "var(--fg-1)" }}>{asset.interest}</span></span>
          <span>via <span style={{ color: "var(--magenta)" }}>{asset.worker}</span></span>
        </div>
      )}
    </div>
  );
}

// ============================================================ TIMELINE

function TimelineView({ rows, selected, setSelected }) {
  // Group by hour bucket
  const now = Date.now();
  const buckets = {};
  for (const a of rows) {
    const h = Math.floor((now - a.firstSeen) / (3600 * 1000));
    buckets[h] = buckets[h] || [];
    buckets[h].push(a);
  }
  const keys = Object.keys(buckets).map(Number).sort((a, b) => a - b);
  const maxN = Math.max(1, ...keys.map(k => buckets[k].length));

  return (
    <div style={{ flex: 1, overflow: "auto", background: "var(--bg-0)" }}>
      <div style={{ padding: 12 }}>
        <div className="mono-label" style={{ marginBottom: 8 }}>Discovery timeline · last 60h · {rows.length} assets</div>
        {/* histogram */}
        <div style={{ display: "flex", alignItems: "end", height: 80, gap: 2, padding: "0 0 10px 0", borderBottom: "1px solid var(--line-1)", marginBottom: 8 }}>
          {Array.from({ length: 60 }, (_, i) => {
            const n = (buckets[i] || []).length;
            const h = (n / maxN) * 100;
            const hot = (buckets[i] || []).some(a => a.interest > 70);
            return (
              <div key={i} style={{
                flex: 1,
                height: Math.max(2, h) + "%",
                background: hot ? "var(--magenta)" : "var(--accent)",
                opacity: hot ? 1 : 0.5,
              }} title={`${i}h ago · ${n}`} />
            );
          })}
        </div>
        {/* rows grouped */}
        {keys.slice(0, 12).map(k => (
          <div key={k} style={{ marginBottom: 8 }}>
            <div className="mono" style={{ color: "var(--fg-3)", fontSize: 10, letterSpacing: "0.12em", padding: "2px 0" }}>
              T-{k}h · {buckets[k].length} assets
            </div>
            {buckets[k].slice(0, 8).map(a => (
              <div
                key={a.id}
                onClick={() => setSelected(a)}
                style={{
                  display: "flex", gap: 12, alignItems: "center",
                  padding: "3px 8px", fontFamily: "var(--font-mono)", fontSize: 11,
                  borderLeft: `2px solid ${a.interest > 70 ? "var(--magenta)" : "var(--line-2)"}`,
                  marginLeft: 8, cursor: "pointer",
                  background: selected?.id === a.id ? "var(--bg-2)" : "transparent",
                }}
              >
                <TypeGlyph type={a.type} />
                <span style={{ color: "var(--fg-3)", fontSize: 10, width: 80 }}>{a.type}</span>
                <span style={{ color: "var(--fg-0)", flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{a.value}</span>
                <span style={{ color: "var(--magenta)", fontSize: 10 }}>{a.worker}</span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

// ============================================================ INSPECTOR

function Inspector({ asset, tab, setTab }) {
  if (!asset) return <div className="inspector" />;

  // Build lineage chain (synthetic from chain field)
  const lineage = [
    ...asset.chain.map((v, i) => {
      const a = ASSETS.find(x => x.value === v);
      return { v, type: a?.type || (i === 0 ? "Domain" : "Subdomain"), worker: a?.worker, time: a?.firstSeen };
    }),
    { v: asset.value, type: asset.type, worker: asset.worker, time: asset.firstSeen, current: true },
  ];

  // Synthesize events
  const events = [
    { type: "AssetDiscovered", color: "cyan", t: asset.firstSeen, worker: asset.worker, body: <>by <span className="worker">{asset.worker}</span></> },
    { type: "AssetNormalized", color: "violet", t: asset.firstSeen + 1500, worker: "NormalizationWorker", body: <>canonical key matched</> },
    { type: "AssetConfirmed", color: "green", t: asset.firstSeen + 4200, worker: "HttpProbe", body: <>HTTP 200 · 14kb · 217ms</> },
    asset.interest > 70 ? { type: "AssetHighValueMarked", color: "amber", t: asset.firstSeen + 8000, worker: "AssetScoring", body: <>interest score crossed threshold</> } : null,
    asset.type === "FindingCandidate" ? { type: "FindingCreated", color: "red", t: asset.firstSeen + 12000, worker: asset.worker, body: <>severity: high · evidence captured</> } : null,
    { type: "AssetTagged", color: "violet", t: asset.lastSeen - 2000, worker: "PatternMatch", body: <>+{asset.tags.length} tags</> },
  ].filter(Boolean).reverse();

  return (
    <div className="inspector">
      <div className="inspector-head">
        <div className="type-row">
          <TypeGlyph type={asset.type} />
          <Pill tone="dim">{asset.type}</Pill>
          <StatusPill status={asset.status} />
          {asset.interest >= 70 && <Pill tone="magenta">★ HIGH-VALUE</Pill>}
          <span className="asset-id">{asset.id}</span>
        </div>
        <div className="value-big">{asset.value}</div>
        <div className="submeta">
          <span>via <b>{asset.worker}</b></span>
          <span>first <b>{fmtTime(asset.firstSeen)}</b> ago</span>
          <span>last <b>{fmtTime(asset.lastSeen)}</b> ago</span>
        </div>
      </div>

      <div className="inspector-tabs">
        {["detail", "lineage", "events", "raw"].map(t => (
          <button key={t} className={"insp-tab" + (tab === t ? " active" : "")} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>

      <div className="inspector-body">
        {tab === "detail" && (
          <>
            <div className="section-label">SCORES</div>
            <div className="score-row">
              <div className="score-cell">
                <div className="l">Confidence</div>
                <div className={"v " + (asset.confidence > 85 ? "" : "accent")}>{asset.confidence}</div>
                <div className="b"><Bar value={asset.confidence} tone="green" width={88} /></div>
              </div>
              <div className="score-cell">
                <div className="l">Risk</div>
                <div className={"v " + (asset.risk > 70 ? "red" : "")}>{asset.risk}</div>
                <div className="b"><Bar value={asset.risk} tone={asset.risk > 70 ? "red" : "amber"} width={88} /></div>
              </div>
              <div className="score-cell">
                <div className="l">Interest</div>
                <div className={"v " + (asset.interest > 70 ? "magenta" : "cyan")}>{asset.interest}</div>
                <div className="b"><Bar value={asset.interest} tone={asset.interest > 70 ? "magenta" : "cyan"} width={88} /></div>
              </div>
            </div>

            <div className="section-label">PROPERTIES</div>
            <table className="kv-table">
              <tbody>
                <tr><td>Asset ID</td><td>{asset.id}</td></tr>
                <tr><td>Natural Key</td><td>{asset.type.toLowerCase()}::{asset.value}</td></tr>
                <tr><td>Type</td><td>{asset.type}</td></tr>
                <tr><td>Scope</td><td><span style={{ color: asset.scope === "InScope" ? "var(--green)" : "var(--fg-3)" }}>{asset.scope}</span></td></tr>
                <tr><td>Parent</td><td>{asset.parentValue || "—"}</td></tr>
                <tr><td>Discovered By</td><td><span style={{ color: "var(--magenta)" }}>{asset.worker}</span></td></tr>
                {asset.httpStatus && <tr><td>HTTP Status</td><td>{asset.httpStatus}</td></tr>}
                {asset.contentLength && <tr><td>Content-Length</td><td>{fmtBytes(asset.contentLength)}</td></tr>}
                <tr><td>First Seen</td><td>{new Date(asset.firstSeen).toISOString().slice(0, 19).replace("T", " ")}Z</td></tr>
                <tr><td>Last Seen</td><td>{fmtTime(asset.lastSeen)} ago</td></tr>
              </tbody>
            </table>

            <div className="section-label">TAGS · {asset.tags.length}</div>
            <div style={{ padding: "4px 12px" }}>
              {asset.tags.map(t => <TagChip key={t} tag={t} />)}
              <span className="mono" style={{ color: "var(--fg-3)", marginLeft: 4, fontSize: 10 }}>+ add</span>
            </div>

            <div className="section-label">ACTIONS</div>
            <div style={{ padding: "4px 12px 14px", display: "flex", flexWrap: "wrap", gap: 4 }}>
              <button className="btn ghost tiny">Re-probe</button>
              <button className="btn ghost tiny">Enqueue Spider</button>
              <button className="btn ghost tiny">Mark High-Value</button>
              <button className="btn ghost tiny">Open in Burp</button>
              <button className="btn danger tiny">Mark OOS</button>
            </div>
          </>
        )}

        {tab === "lineage" && (
          <>
            <div className="section-label">DISCOVERY CHAIN · {lineage.length} hops</div>
            <div className="chain-list">
              {lineage.map((l, i) => (
                <div key={i} className={"chain-step" + (l.current ? " current" : "")}>
                  <span className="marker" />
                  <div className="body">
                    <div className="meta">
                      <span style={{ color: "var(--fg-3)" }}>{l.type}</span>
                      {l.worker && <span style={{ marginLeft: 8 }}>via <span style={{ color: "var(--magenta)" }}>{l.worker}</span></span>}
                      {l.time && <span style={{ marginLeft: 8 }}>{fmtTime(l.time)} ago</span>}
                    </div>
                    <div className="val">{l.v}</div>
                  </div>
                </div>
              ))}
            </div>

            <div className="section-label">SIBLINGS · same parent</div>
            <div style={{ padding: "2px 12px 12px" }}>
              {ASSETS.filter(a => a.parent === asset.parent && a.id !== asset.id).slice(0, 5).map(a => (
                <div key={a.id} className="mono" style={{ fontSize: 11, padding: "3px 0", color: "var(--fg-2)", display: "flex", gap: 6 }}>
                  <TypeGlyph type={a.type} />
                  <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis" }}>{a.value}</span>
                </div>
              ))}
            </div>
          </>
        )}

        {tab === "events" && (
          <>
            <div className="section-label">EVENT TRAIL · {events.length}</div>
            <div>
              {events.map((e, i) => (
                <div key={i} className="event-row">
                  <span className="t">{fmtTime(e.t)}</span>
                  <span className={"k " + e.color}>{e.type}</span>
                  <span className="body">{e.body}</span>
                </div>
              ))}
            </div>
          </>
        )}

        {tab === "raw" && (
          <pre style={{
            margin: 0, padding: 12, fontFamily: "var(--font-mono)", fontSize: 10.5,
            color: "var(--fg-2)", whiteSpace: "pre-wrap", wordBreak: "break-all",
          }}>
{JSON.stringify({
  assetId: asset.id,
  type: asset.type,
  value: asset.value,
  parent: asset.parent,
  status: asset.status,
  scope: asset.scope,
  confidence: asset.confidence,
  risk: asset.risk,
  interest: asset.interest,
  tags: asset.tags,
  discoveredBy: asset.worker,
  firstSeenAt: new Date(asset.firstSeen).toISOString(),
  lastSeenAt: new Date(asset.lastSeen).toISOString(),
  metadata: {
    httpStatus: asset.httpStatus,
    contentLength: asset.contentLength,
  },
}, null, 2)}
          </pre>
        )}
      </div>
    </div>
  );
}

Object.assign(window, { AssetExplorer });
