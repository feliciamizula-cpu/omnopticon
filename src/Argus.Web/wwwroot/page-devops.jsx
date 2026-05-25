// DEVOPS PAGE - GKE Deployment & GitHub Workflows Monitor

function DevopsPage() {
  const [workflows, setWorkflows] = useState([]);
  const [gkeStatus, setGkeStatus] = useState(null);
  const [deployLoading, setDeployLoading] = useState(false);
  const [deployMsg, setDeployMsg] = useState("");
  const [scaleMsg, setScaleMsg] = useState("");
  const [refreshInterval, setRefreshInterval] = useState(30);

  useEffect(() => {
    loadData();
    const id = setInterval(loadData, refreshInterval * 1000);
    return () => clearInterval(id);
  }, [refreshInterval]);

  async function loadData() {
    await Promise.all([
      fetchWorkflows(),
      fetchGKEStatus(),
    ]);
  }

  async function fetchWorkflows() {
    try {
      const resp = await fetch('https://api.github.com/repos/feliciamizula-cpu/omnopticon/actions/runs?per_page=10', {
        headers: { 'Accept': 'application/vnd.github.v3+json' }
      });
      if (resp.ok) {
        const data = await resp.json();
        setWorkflows(data.workflow_runs || []);
      }
    } catch (e) {
      console.warn("Could not fetch workflows:", e);
    }
  }

  async function fetchGKEStatus() {
    try {
      const { execSync } = window.__argus_shell || {};
      if (execSync) {
        const result = execSync('gcloud container clusters list --region us-central1 --format="value(NAME,STATUS,LOCATION,NUM_NODES,MACHINE_TYPE)"');
        const lines = result.trim().split('\n').filter(l => l);
        if (lines.length > 0) {
          const [name, status, location, nodes, machine] = lines[0].split(/\s+/);
          setGkeStatus({ name, status, location, nodes, machine, raw: result });
        }
      }
    } catch (e) {
      setGkeStatus({ error: e.message });
    }
  }

  async function triggerDeployment() {
    setDeployLoading(true);
    setDeployMsg("");
    try {
      const resp = await fetch('https://api.github.com/repos/feliciamizula-cpu/omnopticon/actions/workflows/cd-gcp.yml/dispatch', {
        method: 'POST',
        headers: {
          'Accept': 'application/vnd.github.v3+json',
          'Content-Type': 'application/vnd.github.v3+json',
        },
        body: JSON.stringify({ ref: 'main' })
      });
      if (resp.ok) {
        setDeployMsg("Deployment workflow triggered successfully!");
        setTimeout(fetchWorkflows, 3000);
      } else {
        const err = await resp.json();
        setDeployMsg("Failed: " + (err.message || resp.statusText));
      }
    } catch (e) {
      setDeployMsg("Error: " + e.message);
    }
    setDeployLoading(false);
  }

  function getWorkflowStatusColor(status) {
    return { completed: 'green', in_progress: 'cyan', queued: 'amber', requested: 'dim', waiting: 'dim' }[status] || 'dim';
  }

  function getWorkflowDuration(run) {
    if (!run.updated_at) return '-';
    const start = new Date(run.created_at);
    const end = new Date(run.updated_at);
    const secs = Math.round((end - start) / 1000);
    if (secs < 60) return `${secs}s`;
    if (secs < 3600) return `${Math.floor(secs / 60)}m ${secs % 60}s`;
    return `${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`;
  }

  function formatTimeAgo(dateStr) {
    if (!dateStr) return '-';
    const diff = Date.now() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    return `${Math.floor(hrs / 24)}d ago`;
  }

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, overflow: 'hidden' }}>
      <div className="page-tabs">
        <div className="page-tab active"><span>DevOps · GKE & GitHub</span></div>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center', paddingRight: 12 }}>
          <span style={{ color: 'var(--fg-3)', fontSize: 11 }}>Auto-refresh:</span>
          <select value={refreshInterval} onChange={e => setRefreshInterval(Number(e.target.value))} style={{ background: 'var(--bg-2)', border: '1px solid var(--line-2)', color: 'var(--fg-1)', fontSize: 11, padding: '2px 6px' }}>
            <option value={10}>10s</option>
            <option value={30}>30s</option>
            <option value={60}>1m</option>
            <option value={0}>Off</option>
          </select>
        </div>
      </div>

      <div style={{ flex: 1, overflow: 'auto', padding: 16, display: 'flex', flexDirection: 'column', gap: 16 }}>
        
        {/* GKE Cluster Status */}
        <div className="panel">
          <div className="panel-header">
            <span className="mono-label">GKE CLUSTER STATUS</span>
            <div style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
              <button className="btn ghost" onClick={fetchGKEStatus} title="Refresh">↻</button>
            </div>
          </div>
          <div style={{ padding: 12 }}>
            {gkeStatus?.error ? (
              <div style={{ color: 'var(--red)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                Error: {gkeStatus.error}
              </div>
            ) : gkeStatus ? (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: 12 }}>
                <div className="stat-card">
                  <div className="stat-label">Cluster Name</div>
                  <div className="stat-value" style={{ color: 'var(--cyan)' }}>{gkeStatus.name}</div>
                </div>
                <div className="stat-card">
                  <div className="stat-label">Status</div>
                  <div className="stat-value">
                    <span className={`badge ${gkeStatus.status === 'RUNNING' ? 'green' : 'amber'}`}>
                      {gkeStatus.status || 'PROVISIONING'}
                    </span>
                  </div>
                </div>
                <div className="stat-card">
                  <div className="stat-label">Location</div>
                  <div className="stat-value" style={{ color: 'var(--fg-1)' }}>{gkeStatus.location}</div>
                </div>
                <div className="stat-card">
                  <div className="stat-label">Nodes</div>
                  <div className="stat-value">{gkeStatus.nodes || '-'}</div>
                </div>
                <div className="stat-card">
                  <div className="stat-label">Machine Type</div>
                  <div className="stat-value">{gkeStatus.machine || '-'}</div>
                </div>
              </div>
            ) : (
              <div style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                Loading cluster status...
              </div>
            )}
          </div>
        </div>

        {/* GitHub Workflows */}
        <div className="panel">
          <div className="panel-header">
            <span className="mono-label">GITHUB WORKFLOWS</span>
            <button 
              className="btn primary" 
              onClick={triggerDeployment}
              disabled={deployLoading}
              style={{ marginLeft: 'auto' }}
            >
              {deployLoading ? 'Triggering...' : '▶ Run Deployment'}
            </button>
          </div>
          {deployMsg && (
            <div style={{ 
              padding: '8px 12px', 
              background: deployMsg.includes('success') ? 'var(--bg-2)' : 'var(--red)',
              color: deployMsg.includes('success') ? 'var(--green)' : 'var(--bg-0)',
              fontFamily: 'var(--font-mono)', 
              fontSize: 11 
            }}>
              {deployMsg}
            </div>
          )}
          <div style={{ padding: 12 }}>
            {workflows.length === 0 ? (
              <div style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                Loading workflows...
              </div>
            ) : (
              <table className="asset-grid" style={{ width: '100%' }}>
                <thead>
                  <tr>
                    <th>Status</th>
                    <th>Workflow</th>
                    <th>Branch</th>
                    <th>Triggered</th>
                    <th>Duration</th>
                    <th>Commit</th>
                    <th>Actor</th>
                    <th>Action</th>
                  </tr>
                </thead>
                <tbody>
                  {workflows.map(run => (
                    <tr key={run.id}>
                      <td>
                        <Pill tone={getWorkflowStatusColor(run.status)}>
                          {run.status === 'completed' ? run.conclusion?.toUpperCase() || 'OK' : run.status.toUpperCase()}
                        </Pill>
                      </td>
                      <td style={{ color: 'var(--fg-0)', fontFamily: 'var(--font-mono)', fontSize: 12 }}>{run.name}</td>
                      <td style={{ color: 'var(--cyan)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>{run.head_branch}</td>
                      <td style={{ color: 'var(--fg-2)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>{formatTimeAgo(run.created_at)}</td>
                      <td className="tabular" style={{ color: 'var(--fg-2)' }}>{getWorkflowDuration(run)}</td>
                      <td style={{ color: 'var(--amber)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>{run.head_sha?.slice(0, 7)}</td>
                      <td style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>{run.actor?.login}</td>
                      <td>
                        <a href={run.html_url} target="_blank" rel="noopener" className="btn ghost tiny">View ↗</a>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </div>

        {/* GKE Node Pool & Scaling */}
        <div className="panel">
          <div className="panel-header">
            <span className="mono-label">NODE POOL & SCALING</span>
          </div>
          <div style={{ padding: 12, display: 'flex', gap: 12, flexWrap: 'wrap' }}>
            <div style={{ flex: 1, minWidth: 200 }}>
              <div style={{ color: 'var(--fg-3)', fontSize: 10, marginBottom: 4 }}>SCALE UP</div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="btn primary" disabled={!gkeStatus || gkeStatus.status !== 'RUNNING'}>
                  Scale to 3 Nodes
                </button>
              </div>
            </div>
            <div style={{ flex: 1, minWidth: 200 }}>
              <div style={{ color: 'var(--fg-3)', fontSize: 10, marginBottom: 4 }}>SCALE DOWN</div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="btn danger" disabled={!gkeStatus || gkeStatus.status !== 'RUNNING'}>
                  Scale to 1 Node
                </button>
              </div>
            </div>
            <div style={{ flex: 1, minWidth: 200 }}>
              <div style={{ color: 'var(--fg-3)', fontSize: 10, marginBottom: 4 }}>AUTOSCALING</div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="btn ghost" disabled={!gkeStatus || gkeStatus.status !== 'RUNNING'}>
                  Enable
                </button>
                <button className="btn ghost" disabled={!gkeStatus || gkeStatus.status !== 'RUNNING'}>
                  Disable
                </button>
              </div>
            </div>
          </div>
          {scaleMsg && (
            <div style={{ padding: '8px 12px', background: 'var(--bg-2)', color: 'var(--green)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
              {scaleMsg}
            </div>
          )}
        </div>

        {/* Quick Actions */}
        <div className="panel">
          <div className="panel-header">
            <span className="mono-label">QUICK ACTIONS</span>
          </div>
          <div style={{ padding: 12, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <button className="btn ghost" onClick={() => window.open('https://console.cloud.google.com/kubernetes/clusters', '_blank')}>
              GCP Console ↗
            </button>
            <button className="btn ghost" onClick={() => window.open('https://github.com/feliciamizula-cpu/omnopticon/actions', '_blank')}>
              GitHub Actions ↗
            </button>
            <button className="btn ghost" onClick={() => window.open('https://console.cloud.google.com/logs', '_blank')}>
              Cloud Logging ↗
            </button>
            <button className="btn ghost" onClick={loadData}>
              ↻ Refresh All
            </button>
          </div>
        </div>

        {/* GKE Connection Info */}
        <div className="panel">
          <div className="panel-header">
            <span className="mono-label">GKE CONNECTION INFO</span>
          </div>
          <div style={{ padding: 12, fontFamily: 'var(--font-mono)', fontSize: 11 }}>
            <div style={{ display: 'grid', gap: 8 }}>
              <div><span style={{ color: 'var(--fg-3)' }}>PROJECT:</span> <span style={{ color: 'var(--cyan)' }}>project-30b3b95e-ed2b-4573-98a</span></div>
              <div><span style={{ color: 'var(--fg-3)' }}>CLUSTER:</span> <span style={{ color: 'var(--cyan)' }}>argus-cluster</span></div>
              <div><span style={{ color: 'var(--fg-3)' }}>REGION:</span> <span style={{ color: 'var(--cyan)' }}>us-central1</span></div>
              <div><span style={{ color: 'var(--fg-3)' }}>MASTER IP:</span> <span style={{ color: 'var(--amber)' }}>35.253.156.103</span></div>
            </div>
          </div>
        </div>

      </div>
    </div>
  );
}