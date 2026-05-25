/* eslint-disable */
// Operations Page — Targets + Assets dual-grid with real-time worker dispatch

const { useState, useEffect, useCallback, useRef, useMemo } = React;

const ACTION_LABELS = {
  'enum-sub': 'Enumerate Subdomains',
  'spider': 'Spider',
  'spider-hl': 'Spider (Headless)',
};

const MOCK_SUBS = ['new-api', 'beta2', 'staging2', 'dev2', 'test2', 'internal2', 'preview2', 'cdn2', 'edge2', 'monitor2'];
const MOCK_PATHS = ['/api/v3/users', '/admin/debug', '/.git/config', '/api/internal/v2', '/graphql/v2', '/api/v2/export'];

const TYPE_GLYPHS = { Domain: '▣', Subdomain: '▤', Ip: '◉', Cidr: '◎', Url: '↗', HtmlPage: '❐', ApiEndpoint: '⌬', JavaScriptFile: 'ƒ', CssFile: '≋', JsonDocument: '{}', Port: '▷', DnsRecord: 'ɴ', Technology: '⌘', FindingCandidate: '⚑' };
const TYPE_COLORS = { Domain: 'cyan', Subdomain: 'cyan', Ip: 'violet', Cidr: 'violet', Url: 'fg-1', HtmlPage: 'fg-1', ApiEndpoint: 'magenta', JavaScriptFile: 'amber', FindingCandidate: 'red', Port: 'violet', Technology: 'fg-1', DnsRecord: 'fg-2' };

function isGuid(id) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(String(id || ''));
}

function normalizeOpsAsset(x) {
  return {
    id: x.assetId || x.id,
    type: x.assetType || x.type || 'Unknown',
    value: x.value || x.url || x.domain || '',
    status: x.status || 'Active',
    scope: x.scopeStatus || x.scope || 'InScope',
    risk: x.riskScore ?? x.risk ?? 0,
    worker: x.sourceWorkerType || x.worker || '',
    firstSeen: x.firstSeenAt || x.firstSeen || '',
    lastSeen: x.lastSeenAt || x.lastSeen || '',
    programId: x.programId || '',
    tags: x.tags || [],
  };
}

function fmtAgo(dateStr) {
  if (!dateStr) return '—';
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  if (mins < 1440) return `${Math.floor(mins / 60)}h ago`;
  return `${Math.floor(mins / 1440)}d ago`;
}

// ── Context Menu ─────────────────────────────────────────────────────────────
function OpsContextMenu({ menu, onClose }) {
  useEffect(() => {
    if (!menu) return;
    const close = () => onClose();
    document.addEventListener('click', close);
    return () => document.removeEventListener('click', close);
  }, [menu, onClose]);

  if (!menu) return null;

  return (
    <div
      style={{
        position: 'fixed', left: menu.x, top: menu.y,
        background: 'var(--bg-2)', border: '1px solid var(--line-2)',
        boxShadow: '0 8px 24px rgba(0,0,0,0.5)', zIndex: 2000,
        minWidth: 210, padding: '4px 0',
      }}
      onClick={e => e.stopPropagation()}
    >
      {menu.label && (
        <div style={{
          padding: '5px 14px 6px', color: 'var(--fg-3)',
          fontFamily: 'var(--font-mono)', fontSize: 9,
          letterSpacing: '0.1em', textTransform: 'uppercase',
          borderBottom: '1px solid var(--line-1)',
        }}>
          {menu.label}
        </div>
      )}
      {menu.items.map((item, i) =>
        item.separator ? (
          <div key={i} style={{ height: 1, background: 'var(--line-1)', margin: '3px 0' }} />
        ) : (
          <div
            key={item.action}
            onClick={() => { if (!item.disabled) { onClose(); item.onSelect(); } }}
            style={{
              padding: '7px 14px',
              fontFamily: 'var(--font-mono)', fontSize: 11,
              color: item.danger ? 'var(--red)' : 'var(--fg-0)',
              cursor: item.disabled ? 'not-allowed' : 'pointer',
              opacity: item.disabled ? 0.35 : 1,
            }}
            onMouseEnter={e => { if (!item.disabled) e.currentTarget.style.background = 'var(--bg-3)'; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
          >
            {item.label}
          </div>
        )
      )}
    </div>
  );
}

// ── Activity Feed ─────────────────────────────────────────────────────────────
function ActivityFeed({ log, onClear }) {
  const endRef = useRef(null);
  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [log.length]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <div style={{ display: 'flex', alignItems: 'center', padding: '0 10px', height: 28, borderBottom: '1px solid var(--line-1)', flexShrink: 0 }}>
        <span style={{ fontFamily: 'var(--font-mono)', fontSize: 9, letterSpacing: '0.12em', color: 'var(--fg-3)', textTransform: 'uppercase' }}>Activity</span>
        <button className="btn ghost tiny" style={{ marginLeft: 'auto', fontSize: 9 }} onClick={onClear}>Clear</button>
      </div>
      <div style={{ flex: 1, overflow: 'auto', padding: '6px 10px', display: 'flex', flexDirection: 'column', gap: 5 }}>
        {log.length === 0 ? (
          <div style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10, paddingTop: 8 }}>// no activity</div>
        ) : log.map((entry, i) => (
          <div key={i} style={{ fontFamily: 'var(--font-mono)', fontSize: 10, display: 'flex', gap: 8, alignItems: 'flex-start', lineHeight: 1.4 }}>
            <span style={{ color: 'var(--fg-3)', whiteSpace: 'nowrap', flexShrink: 0 }}>{entry.time}</span>
            <span style={{ color: entry.type === 'error' ? 'var(--red)' : entry.type === 'success' ? 'var(--green)' : 'var(--fg-2)', wordBreak: 'break-all' }}>
              {entry.msg}
            </span>
          </div>
        ))}
        <div ref={endRef} />
      </div>
    </div>
  );
}

// ── Main Page ─────────────────────────────────────────────────────────────────
function OpsPage() {
  const [selectedTarget, setSelectedTarget] = useState(TARGETS[0] || null);
  const [assets, setAssets] = useState([]);
  const [flashIds, setFlashIds] = useState(new Set());
  const [polling, setPolling] = useState(false);
  const [loadingAssets, setLoadingAssets] = useState(false);
  const [contextMenu, setContextMenu] = useState(null);
  const [actionLog, setActionLog] = useState([]);
  const [selectedAssetId, setSelectedAssetId] = useState(null);
  const [sortKey, setSortKey] = useState('firstSeen');
  const [sortDir, setSortDir] = useState('desc');

  const knownIdsRef = useRef(new Set());
  const pollingRef = useRef(false);

  function addLog(msg, type = 'info') {
    const time = new Date().toTimeString().slice(0, 8);
    setActionLog(prev => [...prev.slice(-199), { time, msg, type }]);
  }

  const fetchAssets = useCallback(async (target, isPolling = false) => {
    if (!target) return;
    if (!isPolling) setLoadingAssets(true);
    try {
      let normalized = [];
      if (isGuid(target.id)) {
        const resp = await fetch(`/ui/ops/assets?programId=${target.id}&take=500`);
        if (resp.ok) {
          const data = await resp.json();
          normalized = (data.items || []).map(normalizeOpsAsset);
        }
      } else {
        normalized = (window.ASSETS || []).map(normalizeOpsAsset);
      }

      const newIds = [];
      for (const a of normalized) {
        if (a.id && !knownIdsRef.current.has(a.id)) {
          newIds.push(a.id);
          knownIdsRef.current.add(a.id);
        }
      }
      if (newIds.length > 0 && isPolling) {
        setFlashIds(prev => { const n = new Set(prev); newIds.forEach(id => n.add(id)); return n; });
        setTimeout(() => { setFlashIds(prev => { const n = new Set(prev); newIds.forEach(id => n.delete(id)); return n; }); }, 2500);
      }
      setAssets(normalized);
    } catch (e) {
      if (!isPolling) addLog('Failed to load assets: ' + e.message, 'error');
    } finally {
      if (!isPolling) setLoadingAssets(false);
    }
  }, []);

  // Reset and reload when target changes
  useEffect(() => {
    pollingRef.current = false;
    setPolling(false);
    setAssets([]);
    setFlashIds(new Set());
    setSelectedAssetId(null);
    knownIdsRef.current = new Set();
    if (selectedTarget) fetchAssets(selectedTarget, false);
  }, [selectedTarget, fetchAssets]);

  // Polling loop
  useEffect(() => {
    if (!polling || !selectedTarget) return;
    pollingRef.current = true;
    const id = setInterval(() => {
      if (!pollingRef.current) { clearInterval(id); return; }
      fetchAssets(selectedTarget, true);
    }, 2000);
    return () => { pollingRef.current = false; clearInterval(id); };
  }, [polling, selectedTarget, fetchAssets]);

  function addMockAsset(overrides) {
    const a = normalizeOpsAsset({
      id: 'mock-' + Date.now() + '-' + Math.random().toString(36).slice(2, 6),
      status: 'Active',
      firstSeen: new Date().toISOString(),
      lastSeen: new Date().toISOString(),
      ...overrides,
    });
    if (knownIdsRef.current.has(a.id)) return;
    knownIdsRef.current.add(a.id);
    setAssets(prev => [...prev, a]);
    setFlashIds(prev => { const n = new Set(prev); n.add(a.id); return n; });
    setTimeout(() => { setFlashIds(prev => { const n = new Set(prev); n.delete(a.id); return n; }); }, 2500);
  }

  function addMockAssets(action, asset) {
    const baseDomain = (asset.value || '').replace(/^https?:\/\//, '').split('/')[0].split(':')[0];

    if (action === 'enum-sub') {
      const delays = [1200, 2200, 3600, 5100, 7800, 10500];
      MOCK_SUBS.slice(0, 6).forEach((sub, i) => {
        setTimeout(() => {
          if (!pollingRef.current) return;
          addMockAsset({ type: 'Subdomain', value: sub + '.' + baseDomain, worker: 'Subfinder' });
          addLog(`+ Subdomain: ${sub}.${baseDomain}`, 'success');
        }, delays[i]);
      });
    } else {
      const workerName = action === 'spider-hl' ? 'HeadlessSpider' : 'HtmlDomSpider';
      const delays = [1200, 2400, 3800, 5400, 8200, 11000];
      MOCK_PATHS.forEach((path, i) => {
        setTimeout(() => {
          if (!pollingRef.current) return;
          const type = i % 3 === 0 ? 'ApiEndpoint' : 'Url';
          const value = 'https://' + baseDomain + path;
          addMockAsset({ type, value, worker: workerName });
          addLog(`+ ${type}: ${value}`, 'success');
        }, delays[i]);
      });
    }
  }

  async function dispatchAction(action, asset) {
    const label = ACTION_LABELS[action] || action;
    addLog(`Dispatching ${label} on ${asset.value}...`);
    setPolling(true);
    pollingRef.current = true;
    try {
      await window.ArgusApi?.enqueueAssetAction(asset, action);
      addLog(`✓ ${label} started for ${asset.value}`, 'success');
    } catch (e) {
      if (e?.message?.includes('sample rows cannot be enqueued')) {
        addLog(`[mock] Simulating ${label} for ${asset.value}`, 'info');
        addMockAssets(action, asset);
      } else {
        addLog(`Error: ${e?.message || e}`, 'error');
        setPolling(false);
        pollingRef.current = false;
      }
    }
  }

  function getTargetContextItems(target) {
    const domainAssets = assets.filter(a => a.type === 'Domain');
    const urlAssets = assets.filter(a => ['Subdomain', 'Url', 'HtmlPage'].includes(a.type));
    const rootAsset = domainAssets[0] || normalizeOpsAsset({ id: 'mock-root-' + target.id, type: 'Domain', value: target.name });
    return [
      {
        action: 'enum-sub',
        label: `Enumerate Subdomains  (${domainAssets.length || 1} domain${(domainAssets.length || 1) !== 1 ? 's' : ''})`,
        onSelect: () => (domainAssets.length ? domainAssets : [rootAsset]).forEach(a => dispatchAction('enum-sub', a)),
      },
      {
        action: 'spider',
        label: `Spider All URLs  (${urlAssets.length} asset${urlAssets.length !== 1 ? 's' : ''})`,
        disabled: urlAssets.length === 0,
        onSelect: () => urlAssets.forEach(a => dispatchAction('spider', a)),
      },
      { separator: true },
      {
        action: 'poll',
        label: polling ? '■ Stop Live Polling' : '▶ Start Live Polling',
        onSelect: () => { const next = !polling; setPolling(next); pollingRef.current = next; },
      },
    ];
  }

  function getAssetContextItems(asset) {
    const items = [];
    if (asset.type === 'Domain') {
      items.push({ action: 'enum-sub', label: ACTION_LABELS['enum-sub'], onSelect: () => dispatchAction('enum-sub', asset) });
    }
    if (['Subdomain', 'Url', 'HtmlPage', 'ApiEndpoint'].includes(asset.type)) {
      items.push({ action: 'spider', label: ACTION_LABELS['spider'], onSelect: () => dispatchAction('spider', asset) });
      items.push({ action: 'spider-hl', label: ACTION_LABELS['spider-hl'], onSelect: () => dispatchAction('spider-hl', asset) });
    }
    if (items.length === 0) {
      items.push({ action: 'none', label: 'No actions for this asset type', disabled: true, onSelect: () => {} });
    }
    return items;
  }

  function openContextMenu(e, row, gridType) {
    e.preventDefault();
    const items = gridType === 'targets' ? getTargetContextItems(row) : getAssetContextItems(row);
    const v = row.value || '';
    const labelText = gridType === 'targets'
      ? `TARGET · ${row.name}`
      : `${(row.type || '').toUpperCase()} · ${v.length > 38 ? v.slice(0, 38) + '…' : v}`;
    setContextMenu({ x: e.clientX, y: e.clientY, label: labelText, items });
  }

  const sortedAssets = useMemo(() => {
    return [...assets].sort((a, b) => {
      let av = a[sortKey] ?? '';
      let bv = b[sortKey] ?? '';
      if (sortKey === 'firstSeen' || sortKey === 'lastSeen') {
        av = av ? new Date(av).getTime() : 0;
        bv = bv ? new Date(bv).getTime() : 0;
      }
      if (av < bv) return sortDir === 'asc' ? -1 : 1;
      if (av > bv) return sortDir === 'asc' ? 1 : -1;
      return 0;
    });
  }, [assets, sortKey, sortDir]);

  function handleSort(key) {
    if (sortKey === key) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    else { setSortKey(key); setSortDir('desc'); }
  }

  // ── Column definitions ────────────────────────────────────────────────────

  const targetCols = [
    {
      key: 'status', label: 'Status', width: 75, noSort: true,
      render: row => <span className={`badge ${row.status === 'active' ? 'green' : row.status === 'paused' ? 'amber' : 'dim'}`}>{row.status}</span>,
    },
    {
      key: 'name', label: 'Target', width: 200,
      render: row => <span style={{ color: 'var(--cyan)', fontFamily: 'var(--font-mono)', fontSize: 12 }}>{row.name}</span>,
    },
    {
      key: 'scopes', label: 'Scopes', width: 65,
      render: row => <span className="tabular">{row.scopes}</span>,
    },
    {
      key: 'assets', label: 'Assets', width: 75,
      render: row => <span className="tabular">{row.assets > 0 ? fmtNum(row.assets) : '—'}</span>,
    },
    {
      key: 'findings', label: 'Findings', width: 80,
      render: row => <span className="tabular" style={{ color: row.findings > 0 ? 'var(--red)' : 'var(--fg-3)' }}>{row.findings || '—'}</span>,
    },
  ];

  const assetCols = [
    {
      key: 'type', label: 'Type', width: 110, noSort: true,
      render: row => {
        const color = TYPE_COLORS[row.type] || 'fg-2';
        const glyph = TYPE_GLYPHS[row.type] || '·';
        return <span style={{ color: `var(--${color})`, fontFamily: 'var(--font-mono)', fontSize: 11 }}>{glyph} {row.type}</span>;
      },
    },
    {
      key: 'value', label: 'Value', width: 340,
      render: row => <span style={{ color: 'var(--fg-0)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>{row.value}</span>,
    },
    {
      key: 'status', label: 'Status', width: 80, noSort: true,
      render: row => <span className={`badge ${(row.status || '').toLowerCase() === 'active' ? 'green' : 'dim'}`}>{row.status}</span>,
    },
    {
      key: 'worker', label: 'Worker', width: 130,
      render: row => <span style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>{row.worker || '—'}</span>,
    },
    {
      key: 'firstSeen', label: 'First Seen', width: 90,
      render: row => <span style={{ color: 'var(--fg-2)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>{fmtAgo(row.firstSeen)}</span>,
    },
  ];

  const assetRowStyle = row => ({
    background: flashIds.has(row.id) ? 'rgba(0, 255, 153, 0.10)' : 'transparent',
    transition: 'background 2s ease',
  });

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, overflow: 'hidden' }}>

      {/* Tab bar */}
      <div className="page-tabs">
        <div className="page-tab active"><span>Operations</span></div>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center', paddingRight: 12 }}>
          {polling && <span style={{ color: 'var(--green)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>● LIVE</span>}
          <span style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>{assets.length} assets</span>
          <button className="btn ghost tiny" onClick={() => selectedTarget && fetchAssets(selectedTarget)} title="Refresh">↻</button>
          <button
            className={`btn tiny ${polling ? 'danger' : 'ghost'}`}
            onClick={() => { const next = !polling; setPolling(next); pollingRef.current = next; }}
          >
            {polling ? '■ Stop' : '▶ Poll'}
          </button>
        </div>
      </div>

      {/* Body: grids left, activity right */}
      <div style={{ flex: 1, display: 'flex', minHeight: 0, overflow: 'hidden' }}>

        {/* Left: stacked grids */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, overflow: 'hidden' }}>

          {/* Targets grid — fixed height */}
          <div style={{ flex: '0 0 200px', display: 'flex', flexDirection: 'column', borderBottom: '1px solid var(--line-2)' }}>
            <div className="panel-header" style={{ padding: '0 12px' }}>
              <span className="mono-label">TARGETS</span>
              <span style={{ color: 'var(--fg-3)', fontSize: 10, marginLeft: 8 }}>{TARGETS.length} programs</span>
              <span style={{ color: 'var(--fg-3)', fontSize: 9, marginLeft: 'auto', fontFamily: 'var(--font-mono)' }}>right-click to run</span>
            </div>
            <div style={{ flex: 1, overflow: 'auto' }}>
              <ArgusDataGrid
                columns={targetCols}
                rows={TARGETS}
                rowKey="id"
                selectedId={selectedTarget?.id}
                onSelect={row => setSelectedTarget(row)}
                onContextMenu={(e, row) => openContextMenu(e, row, 'targets')}
                emptyMsg="// no targets"
              />
            </div>
          </div>

          {/* Assets grid — takes remaining space */}
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <div className="panel-header" style={{ padding: '0 12px' }}>
              <span className="mono-label">ASSETS</span>
              {selectedTarget && <span style={{ color: 'var(--fg-3)', fontSize: 10, marginLeft: 8 }}>{selectedTarget.name}</span>}
              {loadingAssets
                ? <span style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10, marginLeft: 8 }}>loading...</span>
                : <span style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10, marginLeft: 8 }}>{assets.length} assets</span>
              }
              <span style={{ color: 'var(--fg-3)', fontSize: 9, marginLeft: 'auto', fontFamily: 'var(--font-mono)' }}>right-click to enumerate / spider</span>
            </div>
            <div style={{ flex: 1, overflow: 'auto' }}>
              <ArgusDataGrid
                columns={assetCols}
                rows={sortedAssets}
                rowKey="id"
                selectedId={selectedAssetId}
                onSelect={row => setSelectedAssetId(row.id)}
                onContextMenu={(e, row) => openContextMenu(e, row, 'assets')}
                rowStyle={assetRowStyle}
                emptyMsg={loadingAssets ? '// loading…' : '// no assets — select a target or trigger a scan'}
                sortKey={sortKey}
                sortDir={sortDir}
                onSort={handleSort}
              />
            </div>
          </div>
        </div>

        {/* Right: activity feed */}
        <ResizablePanel
          id="ops-activity"
          side="right"
          defaultWidth={280}
          minWidth={160}
          maxWidth={480}
          label="Activity"
          pinnable={false}
        >
          <ActivityFeed log={actionLog} onClear={() => setActionLog([])} />
        </ResizablePanel>
      </div>

      <OpsContextMenu menu={contextMenu} onClose={() => setContextMenu(null)} />
    </div>
  );
}

Object.assign(window, { OpsPage });
