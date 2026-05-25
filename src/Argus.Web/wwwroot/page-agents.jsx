/* eslint-disable */
// AGENTS — manage AI coding agents (claude/codex/opencode), recurring tasks.

function AgentsPage({ liveTick }) {
  const [selectedId, setSelectedId] = useState(AGENTS[0].id);
  const [showAdd, setShowAdd] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  const [filterRole, setFilterRole] = useState("all");
  const [selectedAgents, setSelectedAgents] = useState([]);

  const agent = AGENTS.find(a => a.id === selectedId) || AGENTS[0];

  const filtered = filterRole === "all" ? AGENTS : AGENTS.filter(a => a.role === filterRole);

  const toggleAgentSelection = (id, e) => {
    e.stopPropagation();
    setSelectedAgents(prev => 
      prev.includes(id) ? prev.filter(i => i !== id) : [...prev, id]
    );
  };

  const toggleAllSelection = () => {
    if (selectedAgents.length === filtered.length) {
      setSelectedAgents([]);
    } else {
      setSelectedAgents(filtered.map(a => a.id));
    }
  };

  const bulkEnable = () => {
    selectedAgents.forEach(id => {
      const a = AGENTS.find(a => a.id === id);
      if (a) a.enabled = true;
    });
    setSelectedAgents([]);
    liveTick && liveTick();
  };

  const bulkDisable = () => {
    selectedAgents.forEach(id => {
      const a = AGENTS.find(a => a.id === id);
      if (a) a.enabled = false;
    });
    setSelectedAgents([]);
    liveTick && liveTick();
  };

// Compute fleet stats
const stats = useMemo(() => ({
  working: AGENTS.filter(a => a.workStatus === "working").length,
  idle: AGENTS.filter(a => a.workStatus === "idle").length,
  stalled: AGENTS.filter(a => a.workStatus === "stalled").length,
  disabled: AGENTS.filter(a => !a.enabled).length,
  tokens: AGENTS.reduce((s, a) => s + a.tokensToday, 0),
  cost: AGENTS.reduce((s, a) => s + a.costToday, 0),
  quota: AGENTS.reduce((s, a) => s + (a.quota?.cost || 0), 0),
}), []);

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="page-tabs">
        {[
          ["all", "All Agents", AGENTS.length],
          ["development", "Development", AGENTS.filter(a => a.role === "development").length],
          ["devops", "DevOps", AGENTS.filter(a => a.role === "devops").length],
          ["reviewer", "Reviewer", AGENTS.filter(a => a.role === "reviewer").length],
        ].map(([id, label, n]) => (
          <div key={id} className={"page-tab " + (filterRole === id ? "active" : "")} onClick={() => setFilterRole(id)}>
            <span>{label}</span>
            <span className="tab-count">{n}</span>
          </div>
        ))}
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <button className="btn ghost tiny" onClick={() => setShowAdd(true)}>+ Spawn Agent</button>
          <button className="btn ghost tiny">Restart All</button>
          <button className="btn danger tiny">Pause Fleet</button>
        </div>
      </div>

      {selectedAgents.length > 0 && (
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 12px", background: "var(--bg-2)", borderBottom: "1px solid var(--line-1)", flexShrink: 0 }}>
          <span className="mono" style={{ fontSize: 11, color: "var(--fg-2)" }}>{selectedAgents.length} selected</span>
          <button className="btn ghost tiny" onClick={bulkEnable}>Enable</button>
          <button className="btn ghost tiny" onClick={bulkDisable}>Disable</button>
          <button className="btn ghost tiny" onClick={() => setSelectedAgents([])}>Clear</button>
        </div>
      )}

      {/* Top stats strip */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(8, 1fr)", background: "var(--line-1)", gap: 1, flexShrink: 0 }}>
        <FleetStat l="Total agents" v={AGENTS.length} sub={`${stats.disabled} disabled`} />
        <FleetStat l="Working" v={stats.working} tone="cyan" sub="active claims" />
        <FleetStat l="Idle" v={stats.idle} sub="awaiting tasks" />
        <FleetStat l="Stalled" v={stats.stalled} tone={stats.stalled ? "red" : ""} sub={stats.stalled ? "needs attention" : "ok"} />
        <FleetStat l="CLIs in use" v={new Set(AGENTS.map(a => a.cli)).size} sub={[...new Set(AGENTS.map(a => a.cli))].join(", ")} />
<FleetStat l="Tokens · 24h" v={fmtNum(stats.tokens)} tone="amber" sub="across fleet" />
<FleetStat 
  l="Spend · 24h"
  v={"$" + stats.cost.toFixed(2)}
  tone={stats.quota ? stats.cost / stats.quota > 0.9 ? "red" : stats.cost / stats.quota > 0.7 ? "amber" : "" : "amber"}
  sub={stats.quota ? `$${stats.cost.toFixed(2)} of $${stats.quota.toFixed(2)}` : "usage estimate"}
/>
<div style={{ gridColumn: "span 1", background: "var(--bg-1)", padding: "8px 12px" }}> 
  {stats.quota && (
    <div style={{ height: 4, background: "var(--bg-2)", marginTop: 2 }}> 
      <div 
        style={{ 
          height: "100%", 
          width: `${Math.min(100, (stats.cost / stats.quota) * 100)}%`,
          background: stats.cost / stats.quota > 0.9 ? "var(--red)" : stats.cost / stats.quota > 0.7 ? "var(--amber)" : "var(--accent)",
        }}
      />
    </div>
  )}
</div>
<FleetStat l="Tasks · in_progress" v={AGENT_TASKS.filter(t => t.status === "in_progress").length} tone="magenta" sub={`${AGENT_TASKS.filter(t => t.status === "pending").length} pending`} />
      </div>

      {/* Main: card grid + detail panel */}
      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        <div style={{ overflow: "auto", padding: 12, background: "var(--bg-0)", flex: 1, minWidth: 0 }}>
          <div style={{ display: "flex", alignItems: "center", marginBottom: 8 }}>
            <div className="mono-label">FLEET · {filtered.length} agents</div>
            <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 8 }}>
              <span className={"cell-checkbox" + (selectedAgents.length === filtered.length && filtered.length > 0 ? " on" : "")} onClick={toggleAllSelection} style={{ cursor: "pointer" }} />
              <span className="mono" style={{ fontSize: 10, color: "var(--fg-3)" }}>select all</span>
            </div>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(360px, 1fr))", gap: 1, background: "var(--line-1)" }}>
            {filtered.map(a => (
              <AgentCard key={a.id} agent={a} selected={selectedId === a.id} onClick={() => setSelectedId(a.id)} liveTick={liveTick} isSelected={selectedAgents.includes(a.id)} onSelect={(e) => toggleAgentSelection(a.id, e)} />
            ))}
          </div>
        </div>

        <ResizablePanel id="agents-detail" side="right" defaultWidth={460} minWidth={280} maxWidth={700} label="Agent Detail">
          <AgentDetail agent={agent} onEdit={() => setShowEdit(true)} />
        </ResizablePanel>
      </div>

      {showAdd && <AgentModal title="Spawn New Agent" onClose={() => setShowAdd(false)} />}
      {showEdit && <AgentModal title={"Edit · " + agent.name} agent={agent} onClose={() => setShowEdit(false)} />}
    </div>
  );
}

function FleetStat({ l, v, sub, tone }) {
  return (
    <div style={{ background: "var(--bg-1)", padding: "8px 12px" }}>
      <div className="mono" style={{ fontSize: 9, color: "var(--fg-3)", letterSpacing: "0.16em", textTransform: "uppercase" }}>{l}</div>
      <div className="mono tabular" style={{ fontSize: 18, color: tone === "red" ? "var(--red)" : tone === "cyan" ? "var(--cyan)" : tone === "amber" ? "var(--amber)" : tone === "magenta" ? "var(--magenta)" : "var(--fg-0)", marginTop: 2, lineHeight: 1 }}>{v}</div>
      <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", marginTop: 3 }}>{sub}</div>
    </div>
  );
}

function AgentCard({ agent, selected, onClick, liveTick, isSelected, onSelect }) {
  const cli = AGENT_CLIS.find(c => c.id === agent.cli);
  const role = AGENT_ROLES.find(r => r.id === agent.role);
  const statusColor =
    !agent.enabled ? "dim" :
    agent.workStatus === "working" ? "cyan" :
    agent.workStatus === "idle" ? "fg-2" :
    agent.workStatus === "stalled" ? "red" : "amber";

  const heartbeatStale = (Date.now() - agent.lastHeartbeat) > 30_000;

  // mini activity sparkline
  const hist = useMemo(() => Array.from({ length: 24 }, (_, i) => {
    const r = ((agent.id.charCodeAt(0) + i * 7) % 11) + 1;
    return r;
  }), [agent.id]);

  return (
    <div
      onClick={onClick}
      style={{
        background: selected ? "var(--bg-3)" : "var(--bg-1)",
        padding: "10px 12px",
        cursor: "pointer",
        borderLeft: `2px solid ${selected ? "var(--accent)" : `var(--${role.color})`}`,
        opacity: agent.enabled ? 1 : 0.55,
        position: "relative",
      }}
    >
      {/* Top row: checkbox + name + role + cli + status */}
      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
        <span className={"cell-checkbox" + (isSelected ? " on" : "")} onClick={onSelect} style={{ cursor: "pointer" }} />
        <span className={"dot " + (statusColor === "cyan" ? "cyan pulse" : statusColor === "red" ? "red pulse" : statusColor === "amber" ? "amber" : statusColor === "fg-2" ? "" : "")} />
        <span className="cond uppercase" style={{ fontSize: 13, color: "var(--fg-0)", fontWeight: 700, letterSpacing: "0.04em" }}>{agent.name}</span>
        <span style={{ fontFamily: "var(--font-mono)", fontSize: 10, color: `var(--${role.color})` }}>{role.glyph} {role.label}</span>
        <span style={{ marginLeft: "auto", display: "flex", gap: 4, alignItems: "center" }}>
          <Pill tone={statusColor === "fg-2" ? "dim" : statusColor}>{agent.enabled ? agent.workStatus.toUpperCase() : "DISABLED"}</Pill>
        </span>
      </div>

      {/* CLI + model */}
      <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 6, fontFamily: "var(--font-mono)", fontSize: 11 }}>
        <span style={{
          padding: "1px 5px",
          background: "var(--bg-3)",
          color: `var(--${cli.color})`,
          borderLeft: `2px solid var(--${cli.color})`,
          fontWeight: 600,
        }}>{cli.label}</span>
        <span style={{ color: "var(--fg-3)" }}>›</span>
        <span style={{ color: "var(--fg-1)" }}>{agent.model}</span>
        <span style={{ marginLeft: "auto", color: "var(--fg-3)", fontSize: 10 }}>
          {agent.pid ? `pid ${agent.pid}` : "no-pid"}
        </span>
      </div>

      {/* Current task */}
      <div style={{ marginTop: 8, background: "var(--bg-2)", padding: "6px 8px", borderLeft: `2px solid ${agent.currentTask ? `var(--${role.color})` : "var(--line-2)"}` }}>
        {agent.currentTask ? (
          <>
            <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.12em", textTransform: "uppercase" }}>
              CURRENT · <span style={{ color: "var(--accent)" }}>T-{agent.currentTask}</span>
              <span style={{ marginLeft: 8, color: "var(--fg-3)" }}>attempt {agent.attempts}</span>
            </div>
            <div className="mono" style={{ marginTop: 3, fontSize: 11, color: "var(--fg-1)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
              {agent.currentDescription}
            </div>
          </>
        ) : (
          <div className="mono" style={{ color: "var(--fg-3)", fontSize: 11 }}>// idle — no task claimed</div>
        )}
      </div>

      {/* Error */}
      {agent.lastError && (
        <div style={{ marginTop: 6, color: "var(--red)", fontFamily: "var(--font-mono)", fontSize: 10.5, padding: "3px 6px", background: "var(--red-bg)", borderLeft: "2px solid var(--red)" }}>
          ⚠ {agent.lastError}
        </div>
      )}

      {/* Telemetry strip */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 6, marginTop: 8, fontFamily: "var(--font-mono)" }}>
        <Mini l="runs/24h" v={agent.runs24h} />
        <Mini l="success" v={agent.successRate ? agent.successRate.toFixed(0) + "%" : "—"} tone={agent.successRate > 90 ? "green" : agent.successRate > 75 ? "amber" : agent.successRate ? "red" : ""} />
        <Mini l="tok/24h" v={fmtNum(agent.tokensToday)} />
        <Mini l="$/24h" v={"$" + agent.costToday.toFixed(2)} tone="amber" />
      </div>

      {/* Activity sparkline + heartbeat */}
      <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 8 }}>
        <Spark data={hist} tone={role.color} width={180} height={16} />
        <span className="mono" style={{ fontSize: 9.5, color: heartbeatStale ? "var(--red)" : "var(--fg-3)", marginLeft: "auto" }}>
          ♥ {fmtTime(agent.lastHeartbeat)}
        </span>
      </div>

      {/* Recurring tasks summary */}
      <div style={{ marginTop: 8, display: "flex", flexWrap: "wrap", gap: 4 }}>
        {agent.recurring.slice(0, 3).map(r => (
          <span key={r.id} className="mono" style={{
            fontSize: 9.5,
            color: r.enabled ? "var(--fg-2)" : "var(--fg-3)",
            background: "var(--bg-2)",
            border: "1px solid var(--line-1)",
            padding: "1px 5px",
            opacity: r.enabled ? 1 : 0.5,
          }}>
            <span style={{ color: r.enabled ? "var(--accent)" : "var(--fg-3)" }}>↻</span> {r.label} · <span style={{ color: "var(--fg-3)" }}>{r.cron}</span>
          </span>
        ))}
        {agent.recurring.length > 3 && (
          <span className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)" }}>+{agent.recurring.length - 3}</span>
        )}
      </div>
    </div>
  );
}

function Mini({ l, v, tone }) {
  const c = tone === "green" ? "var(--green)" : tone === "amber" ? "var(--amber)" : tone === "red" ? "var(--red)" : "var(--fg-0)";
  return (
    <div style={{ background: "var(--bg-2)", padding: "3px 6px" }}>
      <div className="mono" style={{ fontSize: 8.5, color: "var(--fg-3)", letterSpacing: "0.14em", textTransform: "uppercase" }}>{l}</div>
      <div className="mono tabular" style={{ fontSize: 11.5, color: c, marginTop: 1 }}>{v}</div>
    </div>
  );
}

// ============================================================ DETAIL

function AgentDetail({ agent, onEdit }) {
  const [tab, setTab] = useState("config");
  const cli = AGENT_CLIS.find(c => c.id === agent.cli);
  const role = AGENT_ROLES.find(r => r.id === agent.role);

  return (
    <div className="inspector" style={{ borderLeft: "1px solid var(--line-1)" }}>
      <div className="inspector-head">
        <div className="type-row">
          <span style={{ color: `var(--${role.color})` }} className="mono">{role.glyph}</span>
          <Pill tone={role.color}>{role.label}</Pill>
          <Pill tone={agent.enabled ? (agent.workStatus === "working" ? "cyan" : agent.workStatus === "stalled" ? "red" : "dim") : "dim"}>
            {agent.enabled ? agent.workStatus.toUpperCase() : "DISABLED"}
          </Pill>
          <span className="asset-id">{agent.id}</span>
        </div>
        <div className="value-big" style={{ fontFamily: "var(--font-sans)", fontWeight: 600, fontSize: 16 }}>{agent.name}</div>
        <div className="submeta" style={{ flexWrap: "wrap", gap: 12 }}>
          <span><span style={{ color: `var(--${cli.color})` }}>{cli.label}</span> · <b>{agent.model}</b></span>
          {agent.startedAt && <span>up <b>{fmtTime(agent.startedAt)}</b></span>}
          {agent.pid && <span>pid <b>{agent.pid}</b></span>}
        </div>
      </div>

      <div className="inspector-tabs">
        {["config", "recurring", "history", "logs"].map(t => (
          <button key={t} className={"insp-tab" + (tab === t ? " active" : "")} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>

      <div className="inspector-body">
        {tab === "config" && <ConfigTab agent={agent} onEdit={onEdit} />}
        {tab === "recurring" && <RecurringTab agent={agent} />}
        {tab === "history" && <HistoryTab agent={agent} />}
        {tab === "logs" && <LogsTab agent={agent} />}
      </div>
    </div>
  );
}

function ConfigTab({ agent, onEdit }) {
  return (
    <>
      <div className="section-label">RUNTIME</div>
      <table className="kv-table">
        <tbody>
          <tr><td>CLI</td><td><span style={{ color: `var(--${AGENT_CLIS.find(c => c.id === agent.cli).color})`, fontWeight: 600 }}>{agent.cli}</span></td></tr>
          <tr><td>Model</td><td>{agent.model}</td></tr>
          <tr><td>Command</td><td><span className="mono" style={{ color: "var(--amber)" }}>{AGENT_CLIS.find(c => c.id === agent.cli).command} {"<prompt>"}</span></td></tr>
          <tr><td>Working dir</td><td className="mono">{agent.cwd}</td></tr>
          <tr><td>Process</td><td>{agent.pid ? <span className="mono" style={{ color: "var(--green)" }}>pid {agent.pid}</span> : <span className="mono" style={{ color: "var(--fg-3)" }}>no process</span>}</td></tr>
          <tr><td>Started</td><td>{agent.startedAt ? fmtTime(agent.startedAt) + " ago" : "—"}</td></tr>
          <tr><td>Heartbeat</td><td>{fmtTime(agent.lastHeartbeat)} ago</td></tr>
        </tbody>
      </table>

      <div className="section-label">SYSTEM PROMPT · preview</div>
      <div style={{ padding: "0 12px 8px" }}>
        <div style={{ background: "var(--bg-0)", border: "1px solid var(--line-1)", padding: "8px 10px", fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--fg-1)", lineHeight: 1.5 }}>
          {agent.promptPreview}
          <span style={{ color: "var(--fg-3)" }}> …</span>
        </div>
      </div>

      <div className="section-label">CURRENT WORK</div>
      <div style={{ padding: "4px 12px 12px" }}>
        {agent.currentTask ? (
          <>
            <div className="mono" style={{ fontSize: 11, color: "var(--fg-3)" }}>Task <span style={{ color: "var(--accent)" }}>T-{agent.currentTask}</span> · attempt {agent.attempts}</div>
            <div className="mono" style={{ marginTop: 4, fontSize: 11.5, color: "var(--fg-1)", lineHeight: 1.5 }}>{agent.currentDescription}</div>
          </>
        ) : (
          <div className="mono" style={{ color: "var(--fg-3)", fontSize: 11 }}>// agent is idle</div>
        )}
        {agent.lastError && (
          <div style={{ marginTop: 8, color: "var(--red)", background: "var(--red-bg)", padding: "6px 8px", borderLeft: "2px solid var(--red)", fontSize: 11 }} className="mono">
            ⚠ {agent.lastError}
          </div>
        )}
      </div>

      <div className="section-label">CONTROLS</div>
      <div style={{ padding: "4px 12px 14px", display: "flex", flexWrap: "wrap", gap: 4 }}>
        <button className="btn primary tiny">{agent.enabled ? "Disable" : "Enable"}</button>
        <button className="btn ghost tiny" onClick={onEdit}>Edit Config</button>
        <button className="btn ghost tiny">Edit Prompt</button>
        <button className="btn ghost tiny">Restart</button>
        <button className="btn ghost tiny">Send Signal</button>
        <button className="btn ghost tiny">Reassign Task</button>
        <button className="btn danger tiny">Kill</button>
        <button className="btn danger tiny">Remove</button>
      </div>
    </>
  );
}

function RecurringTab({ agent }) {
  return (
    <>
      <div className="section-label">RECURRING TASKS · {agent.recurring.length}</div>
      {agent.recurring.map(r => (
        <div key={r.id} style={{
          margin: "0 12px 8px",
          padding: "8px 10px",
          background: "var(--bg-0)",
          border: "1px solid var(--line-1)",
          borderLeft: `2px solid ${r.enabled ? "var(--accent)" : "var(--line-2)"}`,
          opacity: r.enabled ? 1 : 0.5,
        }}>
          <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
            <span className={"cell-checkbox" + (r.enabled ? " on" : "")} />
            <span className="mono" style={{ color: "var(--fg-0)", fontSize: 11.5, fontWeight: 500 }}>{r.label}</span>
            <span style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
              <button className="btn ghost tiny">Edit</button>
              <button className="btn ghost tiny">Run Now</button>
              <button className="btn danger tiny">×</button>
            </span>
          </div>
          <div style={{ display: "flex", gap: 16, marginTop: 6, fontFamily: "var(--font-mono)", fontSize: 10 }}>
            <span><span style={{ color: "var(--fg-3)" }}>schedule</span> <span style={{ color: "var(--amber)" }}>{r.cron}</span></span>
            <span><span style={{ color: "var(--fg-3)" }}>last</span> <span style={{ color: "var(--fg-1)" }}>{r.lastRun ? fmtTime(r.lastRun) + " ago" : "never"}</span></span>
            <span><span style={{ color: "var(--fg-3)" }}>next</span> <span style={{ color: "var(--cyan)" }}>{r.enabled ? "in ~" + (Math.floor(Math.random() * 20) + 1) + "m" : "—"}</span></span>
          </div>
        </div>
      ))}

      <div style={{ padding: "0 12px 14px" }}>
        <button className="btn ghost" style={{ width: "100%", height: 28, justifyContent: "center" }}>+ Add Recurring Task</button>
      </div>

      <div className="section-label">PROMPT TEMPLATES · agent-local</div>
      <div style={{ padding: "4px 12px 14px", display: "flex", flexDirection: "column", gap: 4 }}>
        {["pick-next-task.md", "review-uncommitted.md", "investigate-bug.md", "run-build.sh"].map(t => (
          <div key={t} className="mono" style={{ fontSize: 10.5, color: "var(--fg-2)", display: "flex", gap: 6, padding: "2px 6px", background: "var(--bg-2)" }}>
            <span style={{ color: "var(--fg-3)" }}>▷</span>
            <span style={{ flex: 1 }}>{t}</span>
            <span style={{ color: "var(--fg-3)" }}>edit</span>
          </div>
        ))}
      </div>
    </>
  );
}

function HistoryTab({ agent }) {
  return (
    <>
      <div className="section-label">RECENT RUNS · {agent.history.length}</div>
      <ArgusDataGrid
        columns={[
          { key: "startedAt", label: "Started", className: "c-seen", width: 80, render: (h) => <span style={{ color: "var(--fg-2)" }}>{fmtTime(h.startedAt)} ago</span> },
          { key: "taskId", label: "Task", className: "c-type", width: 60, render: (h) => <span style={{ color: "var(--accent)" }}>T-{h.taskId}</span> },
          { key: "duration", label: "Duration", className: "c-int", width: 80, render: (h) => <span className="tabular" style={{ color: "var(--fg-1)" }}>{fmtDur(h.duration)}</span> },
          { key: "tokensIn", label: "Tok In", className: "c-worker", width: 70, render: (h) => <span className="tabular" style={{ color: "var(--fg-2)" }}>{fmtNum(h.tokensIn)}</span> },
          { key: "tokensOut", label: "Tok Out", className: "c-worker", width: 70, render: (h) => <span className="tabular" style={{ color: "var(--fg-1)" }}>{fmtNum(h.tokensOut)}</span> },
          { key: "cost", label: "Cost", className: "c-risk", width: 70, render: (h) => <span className="tabular" style={{ color: "var(--amber)" }}>${h.cost.toFixed(3)}</span> },
          { key: "status", label: "Status", className: "c-status", width: 80, render: (h) => <Pill tone={h.status === "completed" ? "green" : h.status === "failed" ? "red" : "amber"}>{h.status}</Pill> },
        ]}
        rows={agent.history}
        rowKey="taskId"
      />
    </>
  );
}

function LogsTab({ agent }) {
  const lines = [
    [Date.now() - 3000,  "INFO",  "claiming task 052 from queue (priority=high)"],
    [Date.now() - 2200,  "DEBUG", "fetched scope rules · 8 rules cached"],
    [Date.now() - 1900,  "INFO",  "spawning claude --print --model claude-sonnet-4-5"],
    [Date.now() - 1800,  "DEBUG", "claude pid=14283 attached"],
    [Date.now() - 800,   "INFO",  "model response · 7.4kb · 412 input + 1812 output tokens"],
    [Date.now() - 600,   "DEBUG", "editing src/Services/Argus.ProgramScopeService/Endpoints.cs"],
    [Date.now() - 300,   "INFO",  "running dotnet build · target net8.0"],
    [Date.now() - 200,   "WARN",  "build emitted 1 warning · CS8602 possible null reference"],
    [Date.now() - 100,   "INFO",  "task 052 → checkpoint v3 written · cursor at endpoint:export"],
    [Date.now() - 30,    "DEBUG", "heartbeat sent"],
  ].reverse();
  return (
    <>
      <div className="section-label">LIVE LOG · stdout/stderr · last 60s</div>
      <div style={{ padding: "0 8px 12px", fontFamily: "var(--font-mono)", fontSize: 10.5 }}>
        {lines.map((l, i) => {
          const [t, lvl, msg] = l;
          const c = lvl === "WARN" ? "var(--amber)" : lvl === "ERROR" ? "var(--red)" : lvl === "INFO" ? "var(--cyan)" : "var(--fg-3)";
          return (
            <div key={i} style={{ display: "grid", gridTemplateColumns: "60px 56px 1fr", gap: 6, padding: "1px 6px" }}>
              <span style={{ color: "var(--fg-3)", fontSize: 9.5 }}>{fmtClock(t)}</span>
              <span style={{ color: c, fontSize: 9.5 }}>{lvl}</span>
              <span style={{ color: "var(--fg-1)" }}>{msg}</span>
            </div>
          );
        })}
      </div>
      <div style={{ padding: "0 12px 14px", display: "flex", gap: 4 }}>
        <button className="btn ghost tiny">Tail Full Log</button>
        <button className="btn ghost tiny">Export</button>
      </div>
    </>
  );
}

// ============================================================ MODAL

function AgentModal({ title, agent, onClose }) {
  const [cli, setCli] = useState(agent?.cli || "claude");
  const [model, setModel] = useState(agent?.model || AGENT_MODELS.claude[0]);
  const [role, setRole] = useState(agent?.role || "development");
  const [name, setName] = useState(agent?.name || `Agent ${AGENTS.length + 1}`);
  const [prompt, setPrompt] = useState(agent?.promptPreview || "");

  return (
    <div style={{
      position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)",
      backdropFilter: "blur(2px)", display: "grid", placeItems: "center", zIndex: 100,
    }} onClick={onClose}>
      <div style={{
        background: "var(--bg-1)", border: "1px solid var(--line-3)", width: 560,
        boxShadow: "0 24px 80px rgba(0,0,0,0.6)",
      }} onClick={(e) => e.stopPropagation()}>
        <div style={{
          height: 32, padding: "0 14px", borderBottom: "1px solid var(--line-2)",
          background: "var(--bg-2)", display: "flex", alignItems: "center", gap: 8,
          fontFamily: "var(--font-cond)", fontWeight: 700, letterSpacing: "0.14em", textTransform: "uppercase", fontSize: 11,
        }}>
          <span className="pre-tick" style={{ width: 6, height: 6, background: "var(--accent)" }} />
          {title}
          <button className="icon-btn" style={{ marginLeft: "auto" }} onClick={onClose}>×</button>
        </div>
        <div style={{ padding: 16, display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12, fontFamily: "var(--font-mono)", fontSize: 11.5 }}>
          <FormField label="Name">
            <input
              value={name} onChange={(e) => setName(e.target.value)}
              style={{ background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "5px 8px", color: "var(--fg-0)", fontFamily: "inherit", fontSize: 11.5, outline: "none", width: "100%" }}
            />
          </FormField>
          <FormField label="Role">
            <select value={role} onChange={(e) => setRole(e.target.value)} style={selStyle}>
              {AGENT_ROLES.map(r => <option key={r.id} value={r.id}>{r.label}</option>)}
            </select>
          </FormField>

          <FormField label="CLI">
            <div style={{ display: "flex", border: "1px solid var(--line-2)" }}>
              {AGENT_CLIS.map(c => (
                <button key={c.id} onClick={() => { setCli(c.id); setModel(AGENT_MODELS[c.id][0]); }}
                  style={{
                    flex: 1, padding: "5px 0", fontFamily: "var(--font-cond)", fontWeight: 600, fontSize: 11,
                    textTransform: "uppercase", letterSpacing: "0.1em",
                    background: cli === c.id ? "var(--bg-3)" : "transparent",
                    color: cli === c.id ? `var(--${c.color})` : "var(--fg-2)",
                    borderRight: c.id !== "opencode" ? "1px solid var(--line-2)" : "0",
                  }}>{c.label}</button>
              ))}
            </div>
          </FormField>
          <FormField label="Model">
            <select value={model} onChange={(e) => setModel(e.target.value)} style={selStyle}>
              {AGENT_MODELS[cli].map(m => <option key={m} value={m}>{m}</option>)}
            </select>
          </FormField>

          <FormField label="Spawn command" full>
            <div style={{ background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "6px 8px", color: "var(--amber)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
              {AGENT_CLIS.find(c => c.id === cli).command} --model {model} ...
            </div>
          </FormField>

          <FormField label="System prompt" full>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              rows={5}
              placeholder="You are a senior engineer working on the ArgusEngine codebase…"
              style={{
                background: "var(--bg-0)", border: "1px solid var(--line-2)",
                padding: "6px 8px", color: "var(--fg-1)", fontFamily: "var(--font-mono)",
                fontSize: 11, outline: "none", width: "100%", resize: "vertical",
              }}
            />
          </FormField>

          <FormField label="Concurrency limit"><input defaultValue="1" style={inStyle} /></FormField>
          <FormField label="Per-run timeout (sec)"><input defaultValue="900" style={inStyle} /></FormField>
        </div>

        <div style={{
          padding: 12, borderTop: "1px solid var(--line-2)", background: "var(--bg-2)",
          display: "flex", gap: 6, justifyContent: "flex-end",
        }}>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={onClose}>{agent ? "Save" : "Spawn"}</button>
        </div>
      </div>
    </div>
  );
}

const selStyle = {
  background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "5px 8px",
  color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontSize: 11.5, outline: "none", width: "100%",
};
const inStyle = selStyle;

function FormField({ label, children, full }) {
  return (
    <div style={{ gridColumn: full ? "1 / 3" : "auto" }}>
      <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.16em", textTransform: "uppercase", marginBottom: 4 }}>{label}</div>
      {children}
    </div>
  );
}

Object.assign(window, { AgentsPage });
