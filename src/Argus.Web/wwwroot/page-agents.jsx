/* eslint-disable */
// AGENTS — manage AI coding agent configurations.

function AgentsPage({ liveTick }) {
  const [agentConfigs, setAgentConfigs] = useState(AGENTS.map(a => ({ ...a })));
  const [selectedId, setSelectedId] = useState(AGENTS[0]?.id);
  const [multiSel, setMultiSel] = useState(new Set());
  const [sortKey, setSortKey] = useState("name");
  const [sortDir, setSortDir] = useState("asc");
  const [searchTerm, setSearchTerm] = useState("");
  const [showAddModal, setShowAddModal] = useState(false);
  const [showEditModal, setShowEditModal] = useState(false);
  const [editingConfig, setEditingConfig] = useState(null);
  const [showPromptModal, setShowPromptModal] = useState(false);
  const [promptTarget, setPromptTarget] = useState(null);

  const selectedConfig = agentConfigs.find(a => a.id === selectedId);

  const handleSort = (key) => {
    if (sortKey === key) {
      setSortDir(d => d === "asc" ? "desc" : "asc");
    } else {
      setSortKey(key);
      setSortDir("asc");
    }
  };

  const filteredConfigs = useMemo(() => {
    let r = agentConfigs;
    if (searchTerm) {
      const term = searchTerm.toLowerCase();
      r = r.filter(a =>
        a.name.toLowerCase().includes(term) ||
        a.role.toLowerCase().includes(term) ||
        a.cli.toLowerCase().includes(term) ||
        a.model.toLowerCase().includes(term)
      );
    }
    return [...r].sort((a, b) => {
      const dir = sortDir === "asc" ? 1 : -1;
      const av = a[sortKey], bv = b[sortKey];
      if (typeof av === "number") return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  }, [agentConfigs, searchTerm, sortKey, sortDir]);

  const handleContextAction = (action, config) => {
    switch (action) {
      case "toggle":
        setAgentConfigs(prev => prev.map(a => a.id === config.id ? { ...a, enabled: !a.enabled } : a));
        break;
      case "edit":
        setEditingConfig(config);
        setShowEditModal(true);
        break;
      case "delete":
        setAgentConfigs(prev => prev.filter(a => a.id !== config.id));
        if (selectedId === config.id) setSelectedId(prev[0]?.id);
        break;
      case "copy":
        const newConfig = {
          ...config,
          id: config.id + "-copy-" + Date.now(),
          name: config.name + " (copy)",
          enabled: false,
        };
        setAgentConfigs(prev => [...prev, newConfig]);
        break;
    }
  };

  window.__argusContextAction = handleContextAction;

  const columns = [
    { key: "_sel", label: "", width: 22, noSort: true },
    { key: "enabled", label: "Enabled", width: 60, noSort: true, filterable: true },
    { key: "name", label: "Agent Name", width: 140 },
    { key: "role", label: "Role", width: 90, filterable: true },
    { key: "cli", label: "CLI Tool", width: 90, filterable: true, render: (a) => {
      const cli = AGENT_CLIS.find(c => c.id === a.cli);
      return <span style={{ color: `var(--${cli?.color || "fg-2"})`, fontWeight: 600 }}>{cli?.label || a.cli}</span>;
    }},
    { key: "provider", label: "Provider", width: 100, render: (a) => {
      const cli = AGENT_CLIS.find(c => c.id === a.cli);
      return <span style={{ color: `var(--${cli?.color || "fg-2"})` }}>{cli?.label?.toUpperCase() || a.cli}</span>;
    }},
    { key: "model", label: "Model", width: 150 },
    { key: "priority", label: "Priority", width: 70, render: (a) => (
      <span className="mono" style={{ color: a.priority > 5 ? "var(--amber)" : "var(--fg-2)", fontSize: 11 }}>
        {a.priority || 1}
      </span>
    )},
    { key: "activeInstances", label: "Active", width: 60, render: (a) => (
      <span className="mono tabular" style={{ color: a.workStatus === "working" ? "var(--cyan)" : "var(--fg-3)" }}>
        {a.workStatus === "working" ? "1" : "0"}
      </span>
    )},
    { key: "prompt", label: "Prompt", width: 200, noSort: true, render: (a) => (
      a.promptPreview ? (
        <span
          onClick={(e) => { e.stopPropagation(); setPromptTarget(a); setShowPromptModal(true); }}
          style={{
            color: "var(--cyan)", cursor: "pointer", fontFamily: "var(--font-mono)", fontSize: 10,
            overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", display: "block",
          }}
          title="Click to view/edit prompt"
        >
          {a.promptPreview.slice(0, 40)}…
        </span>
      ) : (
        <span style={{ color: "var(--fg-3)", fontSize: 10, cursor: "pointer" }}
          onClick={(e) => { e.stopPropagation(); setPromptTarget(a); setShowPromptModal(true); }}>
          + add prompt
        </span>
      )
    )},
  ];

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="page-tabs">
        <div className="page-tab active"><span>Agent Configurations</span></div>
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <button className="btn ghost tiny" onClick={() => { setEditingConfig(null); setShowAddModal(true); }}>+ New Configuration</button>
        </div>
      </div>

      {/* Main: grid + detail panel */}
      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0 }}>
          {/* Search bar */}
          <div style={{ display: "flex", gap: 8, padding: "6px 12px", background: "var(--bg-2)", borderBottom: "1px solid var(--line-1)" }}>
            <div style={{ position: "relative", flex: 1, maxWidth: 320 }}>
              <span style={{ position: "absolute", left: 8, top: "50%", transform: "translateY(-50%)", color: "var(--fg-3)", fontSize: 11 }}>⌕</span>
              <input
                type="text"
                placeholder="Search agents..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                style={{
                  width: "100%", padding: "5px 8px 5px 28px", background: "var(--bg-0)",
                  border: "1px solid var(--line-2)", color: "var(--fg-0)",
                  fontFamily: "var(--font-mono)", fontSize: 11, outline: "none",
                }}
              />
            </div>
            <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span className="mono" style={{ fontSize: 10, color: "var(--fg-3)" }}>{filteredConfigs.length} configs</span>
              {multiSel.size > 0 && (
                <>
                  <span style={{ width: 1, height: 16, background: "var(--line-2)" }} />
                  <span className="mono" style={{ fontSize: 11, color: "var(--accent)" }}>{multiSel.size} selected</span>
                  <button className="btn ghost tiny" onClick={() => {
                    multiSel.forEach(id => {
                      const a = agentConfigs.find(x => x.id === id);
                      if (a) a.enabled = !a.enabled;
                    });
                    setAgentConfigs([...agentConfigs]);
                    setMultiSel(new Set());
                  }}>Toggle</button>
                  <button className="btn danger tiny" onClick={() => {
                    setAgentConfigs(prev => prev.filter(a => !multiSel.has(a.id)));
                    setMultiSel(new Set());
                  }}>Delete</button>
                </>
              )}
            </div>
          </div>

          {/* Grid */}
          <ArgusDataGrid
            columns={columns}
            rows={filteredConfigs}
            rowKey="id"
            selectedId={selectedId}
            onSelect={(a) => setSelectedId(a.id)}
            multiSel={multiSel}
            onMultiSel={setMultiSel}
            sortKey={sortKey}
            sortDir={sortDir}
            onSort={handleSort}
            searchable={false}
            filterable={false}
            onContextMenu={(e, row) => handleContextAction && handleContextAction(null, row)}
          />
        </div>

        {/* Detail Panel */}
        {selectedConfig && (
          <ResizablePanel id="agent-config-detail" side="right" defaultWidth={480} minWidth={300} maxWidth={720} label="Configuration Detail">
            <ConfigDetailPanel
              config={selectedConfig}
              onEdit={() => { setEditingConfig(selectedConfig); setShowEditModal(true); }}
              onPromptEdit={() => { setPromptTarget(selectedConfig); setShowPromptModal(true); }}
            />
          </ResizablePanel>
        )}
      </div>

      {showAddModal && (
        <ConfigModal
          title="New Agent Configuration"
          onClose={() => setShowAddModal(false)}
          onSave={(config) => {
            setAgentConfigs(prev => [...prev, { ...config, id: "agent-" + Date.now() }]);
            setShowAddModal(false);
          }}
        />
      )}
      {showEditModal && editingConfig && (
        <ConfigModal
          title={"Edit · " + editingConfig.name}
          config={editingConfig}
          onClose={() => setShowEditModal(false)}
          onSave={(config) => {
            setAgentConfigs(prev => prev.map(a => a.id === config.id ? config : a));
            setShowEditModal(false);
          }}
        />
      )}
      {showPromptModal && promptTarget && (
        <PromptEditorModal
          config={promptTarget}
          onClose={() => setShowPromptModal(false)}
          onSave={(prompt) => {
            setAgentConfigs(prev => prev.map(a => a.id === promptTarget.id ? { ...a, promptPreview: prompt, prompt } : a));
            setShowPromptModal(false);
          }}
        />
      )}
    </div>
  );
}

// ============================================================ DETAIL PANEL

function ConfigDetailPanel({ config, onEdit, onPromptEdit }) {
  const cli = AGENT_CLIS.find(c => c.id === config.cli);
  const role = AGENT_ROLES.find(r => r.id === config.role);
  const command = cli?.command + " --model " + config.model;

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, overflow: "auto" }}>
      <div style={{ padding: "12px 14px", borderBottom: "1px solid var(--line-1)", background: "var(--bg-2)" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 8 }}>
          <span style={{ color: `var(--${role?.color || "fg-2"})` }}>{role?.glyph}</span>
          <Pill tone={config.enabled ? "green" : "dim"}>{config.enabled ? "ENABLED" : "DISABLED"}</Pill>
        </div>
        <div style={{ fontFamily: "var(--font-mono)", fontSize: 16, color: "var(--fg-0)", fontWeight: 600 }}>{config.name}</div>
      </div>

      <div style={{ flex: 1, overflow: "auto", padding: "0 0 20px" }}>
        <div className="section-label" style={{ marginTop: 12 }}>EXECUTION COMMAND</div>
        <div style={{ padding: "0 12px 12px" }}>
          <div style={{
            background: "var(--bg-0)", border: "1px solid var(--line-1)", padding: "8px 10px",
            fontFamily: "var(--font-mono)", fontSize: 11, color: "var(--amber)",
            wordBreak: "break-all",
          }}>
            {command} {"<prompt>"}
          </div>
        </div>

        <div className="section-label">CONFIGURATION</div>
        <table className="kv-table">
          <tbody>
            <tr><td>CLI</td><td><span style={{ color: `var(--${cli?.color})`, fontWeight: 600 }}>{cli?.label}</span></td></tr>
            <tr><td>Provider</td><td>{cli?.label?.toUpperCase()}</td></tr>
            <tr><td>Model</td><td>{config.model}</td></tr>
            <tr><td>Role</td><td><span style={{ color: `var(--${role?.color})` }}>{role?.glyph} {role?.label}</span></td></tr>
            <tr><td>Priority</td><td>{config.priority || 1}</td></tr>
            <tr><td>Working Dir</td><td className="mono">{config.cwd || "/srv/argus"}</td></tr>
          </tbody>
        </table>

        <div className="section-label">PROMPT</div>
        <div style={{ padding: "0 12px 12px" }}>
          {config.promptPreview ? (
            <>
              <div style={{
                background: "var(--bg-0)", border: "1px solid var(--line-1)", padding: "8px 10px",
                fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--fg-1)", maxHeight: 120, overflow: "hidden",
                position: "relative",
              }}>
                {config.promptPreview}
                <span style={{ position: "absolute", bottom: 0, left: 0, right: 0, height: 30, background: "linear-gradient(transparent, var(--bg-0))" }} />
              </div>
              <button className="btn ghost tiny" style={{ marginTop: 6 }} onClick={onPromptEdit}>Edit Prompt</button>
            </>
          ) : (
            <div style={{ textAlign: "center", padding: "16px", background: "var(--bg-0)", border: "1px dashed var(--line-2)" }}>
              <div className="mono" style={{ fontSize: 11, color: "var(--fg-3)", marginBottom: 8 }}>No custom prompt</div>
              <button className="btn ghost tiny" onClick={onPromptEdit}>+ Add Prompt</button>
            </div>
          )}
        </div>

        <div className="section-label">ACTIONS</div>
        <div style={{ padding: "4px 12px 14px", display: "flex", flexWrap: "wrap", gap: 4 }}>
          <button className="btn primary tiny" onClick={onEdit}>Edit Configuration</button>
          <button className="btn ghost tiny">Duplicate</button>
          <button className="btn danger tiny">Delete</button>
        </div>
      </div>
    </div>
  );
}

// ============================================================ CONFIG MODAL

function ConfigModal({ title, config, onClose, onSave }) {
  const [name, setName] = useState(config?.name || "");
  const [cli, setCli] = useState(config?.cli || "claude");
  const [model, setModel] = useState(config?.model || "");
  const [role, setRole] = useState(config?.role || "development");
  const [priority, setPriority] = useState(config?.priority || 1);
  const [prompt, setPrompt] = useState(config?.promptPreview || "");

  useEffect(() => {
    if (!model && AGENT_MODELS[cli]) setModel(AGENT_MODELS[cli][0]);
  }, [cli]);

  return (
    <div style={{
      position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)",
      backdropFilter: "blur(2px)", display: "grid", placeItems: "center", zIndex: 100,
    }} onClick={onClose}>
      <div style={{
        background: "var(--bg-1)", border: "1px solid var(--line-3)", width: 600,
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
          <FormField label="Name" full>
            <input value={name} onChange={(e) => setName(e.target.value)} style={inStyle} />
          </FormField>
          <FormField label="Role">
            <select value={role} onChange={(e) => setRole(e.target.value)} style={inStyle}>
              {AGENT_ROLES.map(r => <option key={r.id} value={r.id}>{r.glyph} {r.label}</option>)}
            </select>
          </FormField>

          <FormField label="CLI Tool">
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
            <select value={model} onChange={(e) => setModel(e.target.value)} style={inStyle}>
              {AGENT_MODELS[cli]?.map(m => <option key={m} value={m}>{m}</option>)}
            </select>
          </FormField>

          <FormField label="Priority">
            <input type="number" min="1" max="10" value={priority} onChange={(e) => setPriority(+e.target.value)} style={inStyle} />
          </FormField>

          <FormField label="Generated Command" full>
            <div style={{ background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "6px 8px", color: "var(--amber)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
              {AGENT_CLIS.find(c => c.id === cli)?.command || cli} --model {model} {"<prompt>"}
            </div>
          </FormField>

          <FormField label="Custom Prompt (optional)" full>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              rows={4}
              placeholder="Optional: Add a custom system prompt that will be prepended to agent commands..."
              style={{ ...inStyle, resize: "vertical" }}
            />
          </FormField>
        </div>

        <div style={{
          padding: 12, borderTop: "1px solid var(--line-2)", background: "var(--bg-2)",
          display: "flex", gap: 6, justifyContent: "flex-end",
        }}>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={() => {
            onSave({
              ...config,
              name, cli, model, role, priority,
              promptPreview: prompt,
              cwd: config?.cwd || "/srv/argus",
              enabled: config?.enabled ?? true,
            });
          }}>Save</button>
        </div>
      </div>
    </div>
  );
}

// ============================================================ PROMPT EDITOR MODAL

function PromptEditorModal({ config, onClose, onSave }) {
  const [prompt, setPrompt] = useState(config?.promptPreview || "");

  return (
    <div style={{
      position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)",
      backdropFilter: "blur(2px)", display: "grid", placeItems: "center", zIndex: 100,
    }} onClick={onClose}>
      <div style={{
        background: "var(--bg-1)", border: "1px solid var(--line-3)", width: 700,
        maxHeight: "80vh", display: "flex", flexDirection: "column",
        boxShadow: "0 24px 80px rgba(0,0,0,0.6)",
      }} onClick={(e) => e.stopPropagation()}>
        <div style={{
          height: 32, padding: "0 14px", borderBottom: "1px solid var(--line-2)",
          background: "var(--bg-2)", display: "flex", alignItems: "center", gap: 8,
          fontFamily: "var(--font-cond)", fontWeight: 700, letterSpacing: "0.14em", textTransform: "uppercase", fontSize: 11,
          flexShrink: 0,
        }}>
          <span className="pre-tick" style={{ width: 6, height: 6, background: "var(--cyan)" }} />
          Prompt · {config?.name}
          <button className="icon-btn" style={{ marginLeft: "auto" }} onClick={onClose}>×</button>
        </div>
        <div style={{ flex: 1, overflow: "auto", padding: 16 }}>
          <div style={{ marginBottom: 8 }}>
            <span className="mono" style={{ fontSize: 10, color: "var(--fg-3)", letterSpacing: "0.12em", textTransform: "uppercase" }}>
              Custom Prompt
            </span>
            <span style={{ marginLeft: 8, fontSize: 10, color: "var(--fg-3)" }}>
              (prepended to default system prompt)
            </span>
          </div>
          <textarea
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            rows={16}
            placeholder="You are a senior C# engineer working on the ArgusEngine codebase. Implement assigned tasks following existing patterns, write tests, and update documentation as needed..."
            style={{
              width: "100%", padding: "10px 12px", background: "var(--bg-0)",
              border: "1px solid var(--line-2)", color: "var(--fg-0)",
              fontFamily: "var(--font-mono)", fontSize: 11, outline: "none",
              resize: "vertical", minHeight: 200,
            }}
          />
          <div style={{ marginTop: 8, fontFamily: "var(--font-mono)", fontSize: 10, color: "var(--fg-3)" }}>
            This prompt will be included with the command: <span style={{ color: "var(--amber)" }}>
              {AGENT_CLIS.find(c => c.id === config?.cli)?.command || config?.cli} --model {config?.model} [this prompt]
            </span>
          </div>
        </div>
        <div style={{
          padding: 12, borderTop: "1px solid var(--line-2)", background: "var(--bg-2)",
          display: "flex", gap: 6, justifyContent: "flex-end",
        }}>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={() => onSave(prompt)}>Save Prompt</button>
        </div>
      </div>
    </div>
  );
}

function FormField({ label, children, full }) {
  return (
    <div style={{ gridColumn: full ? "1 / 3" : "auto" }}>
      <div className="mono" style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.16em", textTransform: "uppercase", marginBottom: 4 }}>{label}</div>
      {children}
    </div>
  );
}

const inStyle = {
  background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "5px 8px",
  color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontSize: 11.5, outline: "none", width: "100%",
};

Object.assign(window, { AgentsPage });