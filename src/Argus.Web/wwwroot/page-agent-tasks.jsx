/* eslint-disable */
// AGENT TASKS — editable queue for the AI agents.

function AgentTasksPage() {
  const [tasks, setTasks] = useState(AGENT_TASKS);
  const [selected, setSelected] = useState(AGENT_TASKS.find(t => t.status === "in_progress") || AGENT_TASKS[0]);
  const [filter, setFilter] = useState("queue");
  const [filterAgent, setFilterAgent] = useState(null);
  const [editing, setEditing] = useState(false);
  const [showAdd, setShowAdd] = useState(false);

  const filterFns = {
    queue:    (t) => t.status === "pending" || t.status === "claimed" || t.status === "in_progress" || t.status === "blocked",
    pending:  (t) => t.status === "pending",
    active:   (t) => t.status === "claimed" || t.status === "in_progress",
    done:     (t) => t.status === "completed",
    failed:   (t) => t.status === "failed",
    all:      (t) => true,
  };

  const rows = tasks.filter(filterFns[filter]).filter(t => !filterAgent || t.assignedTo === filterAgent);

  const counts = {
    queue: tasks.filter(filterFns.queue).length,
    pending: tasks.filter(filterFns.pending).length,
    active: tasks.filter(filterFns.active).length,
    done: tasks.filter(filterFns.done).length,
    failed: tasks.filter(filterFns.failed).length,
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="page-tabs">
        {[
          ["queue", "Queue", counts.queue],
          ["pending", "Pending", counts.pending],
          ["active", "Active", counts.active],
          ["done", "Completed", counts.done],
          ["failed", "Failed", counts.failed],
          ["all", "All", tasks.length],
        ].map(([id, label, n]) => (
          <div key={id} className={"page-tab " + (filter === id ? "active" : "")} onClick={() => setFilter(id)}>
            <span>{label}</span>
            <span className="tab-count">{n}</span>
          </div>
        ))}
        <div className="page-tab-spacer" />
        <div className="page-tab-actions">
          <button className="btn primary tiny" onClick={() => setShowAdd(true)}>+ New Task</button>
          <button className="btn ghost tiny">Import from GitHub</button>
          <button className="btn ghost tiny">Pause Intake</button>
        </div>
      </div>

      <div style={{ flex: 1, display: "flex", minHeight: 0, position: "relative" }}>
        {/* Left rail: filter by agent */}
        <ResizablePanel id="agtasks-facets" side="left" defaultWidth={180} minWidth={120} maxWidth={320} label="Filters">
        <div className="facets" style={{ flex: 1 }}>
          <div className="facet-head" style={{ borderBottom: "1px solid var(--line-1)" }}>
            <span>Assigned</span>
            <span className="count">{AGENTS.length + 1}</span>
          </div>
          <div className={"facet-row" + (!filterAgent ? " active" : "")} onClick={() => setFilterAgent(null)}>
            <div className="check" />
            <span className="label">All agents</span>
            <span className="n">{tasks.length}</span>
          </div>
          <div className={"facet-row" + (filterAgent === "_unassigned" ? " active" : "")} onClick={() => setFilterAgent("_unassigned")}>
            <div className="check" />
            <span className="label" style={{ color: "var(--fg-3)" }}><i>unassigned</i></span>
            <span className="n">{tasks.filter(t => !t.assignedTo).length}</span>
          </div>
          {AGENTS.map(a => (
            <div key={a.id} className={"facet-row" + (filterAgent === a.id ? " active" : "")} onClick={() => setFilterAgent(a.id)}>
              <div className="check" />
              <span className="label" style={{ fontSize: 10.5 }}>
                <span className={"dot " + (a.workStatus === "working" ? "cyan" : a.workStatus === "stalled" ? "red" : "")} style={{ marginRight: 5 }} />
                {a.name}
              </span>
              <span className="n">{tasks.filter(t => t.assignedTo === a.id).length}</span>
            </div>
          ))}

          <div className="facet-head" style={{ marginTop: 6 }}>
            <span>Priority</span>
          </div>
          {["critical", "high", "medium", "low"].map(p => (
            <div key={p} className="facet-row">
              <div className="check" />
              <span className="label">
                <PrioPill p={p} />
              </span>
              <span className="n">{tasks.filter(t => t.priority === p).length}</span>
            </div>
          ))}

          <div className="facet-head" style={{ marginTop: 6 }}>
            <span>Kind</span>
          </div>
          {["feature", "bug", "refactor", "docs", "ops", "review", "security", "chore"].map(k => (
            <div key={k} className="facet-row">
              <div className="check" />
              <span className="label" style={{ fontSize: 10.5 }}>{k}</span>
              <span className="n">{tasks.filter(t => t.kind === k).length}</span>
            </div>
          ))}
        </div>
        </ResizablePanel>

        {/* Center: task table */}
        <div style={{ overflow: "auto", background: "var(--bg-0)", flex: 1, minWidth: 0 }}>
          <table className="asset-grid">
            <thead>
              <tr>
                <th className="c-check"></th>
                <th style={{ width: 80 }}>Task</th>
                <th style={{ width: 84 }}>Priority</th>
                <th style={{ width: 96 }}>Status</th>
                <th style={{ width: 70 }}>Kind</th>
                <th>Description</th>
                <th style={{ width: 130 }}>Assigned</th>
                <th style={{ width: 40 }}>Att.</th>
                <th style={{ width: 70 }}>Created</th>
                <th style={{ width: 22 }}></th>
              </tr>
            </thead>
            <tbody>
              {rows.map(t => (
                <tr key={t.id} className={selected?.id === t.id ? "selected" : ""} onClick={() => setSelected(t)}>
                  <td className="c-check"><span className="cell-checkbox" onClick={(e) => e.stopPropagation()} /></td>
                  <td style={{ color: "var(--accent)", fontWeight: 600 }}>T-{t.id}</td>
                  <td><PrioPill p={t.priority} /></td>
                  <td><TaskStatusPill s={t.status} /></td>
                  <td><span style={{ color: "var(--fg-2)", fontSize: 10.5 }}>{t.kind}</span></td>
                  <td className="c-value" style={{ color: "var(--fg-0)" }}>
                    {t.description}
                    {t.blockedReason && <span style={{ color: "var(--amber)", marginLeft: 6, fontSize: 10 }}>· {t.blockedReason}</span>}
                  </td>
                  <td>
                    {t.assignedTo ? (
                      <AgentChip id={t.assignedTo} />
                    ) : (
                      <span style={{ color: "var(--fg-3)", fontSize: 10.5 }} className="mono">—</span>
                    )}
                  </td>
                  <td className="tabular" style={{ color: t.attempts > 3 ? "var(--red)" : "var(--fg-2)" }}>{t.attempts}</td>
                  <td className="tabular" style={{ color: "var(--fg-3)" }}>{fmtTime(t.createdAt)}</td>
                  <td><span style={{ color: "var(--fg-3)", cursor: "pointer" }}>⋮</span></td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr><td colSpan={10} style={{ padding: 30, textAlign: "center", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
                  // queue is empty
                </td></tr>
              )}
            </tbody>
          </table>
        </div>

        {/* Right: detail / edit */}
        <ResizablePanel id="agtasks-detail" side="right" defaultWidth={420} minWidth={260} maxWidth={640} label="Task Detail">
        <TaskDetail
          task={selected}
          editing={editing}
          setEditing={setEditing}
          onSave={(t) => {
            setTasks(tasks.map(x => x.id === t.id ? t : x));
            setSelected(t);
            setEditing(false);
          }}
          onDelete={(t) => {
            const next = tasks.filter(x => x.id !== t.id);
            setTasks(next);
            setSelected(next[0]);
          }}
        />
        </ResizablePanel>
      </div>

      {showAdd && <TaskModal onClose={() => setShowAdd(false)} />}
    </div>
  );
}

function PrioPill({ p }) {
  const tone = p === "critical" ? "red" : p === "high" ? "amber" : p === "medium" ? "cyan" : "dim";
  return <Pill tone={tone}>{p}</Pill>;
}

function TaskStatusPill({ s }) {
  const tone =
    s === "pending" ? "dim" :
    s === "claimed" ? "cyan" :
    s === "in_progress" ? "cyan" :
    s === "blocked" ? "amber" :
    s === "completed" ? "green" :
    s === "failed" ? "red" : "dim";
  return <Pill tone={tone}>{s.replace("_", " ")}</Pill>;
}

function AgentChip({ id }) {
  const a = AGENTS.find(x => x.id === id);
  if (!a) return <span className="mono" style={{ fontSize: 10.5 }}>{id}</span>;
  const role = AGENT_ROLES.find(r => r.id === a.role);
  const cli = AGENT_CLIS.find(c => c.id === a.cli);
  return (
    <span className="mono" style={{ fontSize: 10.5, display: "inline-flex", alignItems: "center", gap: 4 }}>
      <span className={"dot " + (a.workStatus === "working" ? "cyan pulse" : a.workStatus === "stalled" ? "red" : "")} />
      <span style={{ color: `var(--${role.color})`, fontWeight: 600 }}>{a.name}</span>
      <span style={{ color: "var(--fg-3)", fontSize: 9 }}>{cli.label}</span>
    </span>
  );
}

// ============================================================ TASK DETAIL

function TaskDetail({ task, editing, setEditing, onSave, onDelete }) {
  const [draft, setDraft] = useState(task);
  useEffect(() => { setDraft(task); setEditing(false); }, [task?.id]);

  if (!task) return <div className="inspector" />;
  const t = editing ? draft : task;

  return (
    <div className="inspector" style={{ borderLeft: "1px solid var(--line-1)" }}>
      <div className="inspector-head">
        <div className="type-row">
          <Pill tone="dim">Task</Pill>
          <PrioPill p={t.priority} />
          <TaskStatusPill s={t.status} />
          <span className="asset-id">T-{t.id}</span>
        </div>
        {editing ? (
          <textarea
            value={draft.description}
            onChange={(e) => setDraft({ ...draft, description: e.target.value })}
            rows={3}
            style={{
              width: "100%", background: "var(--bg-0)", border: "1px solid var(--line-2)",
              padding: "6px 8px", color: "var(--fg-0)", fontFamily: "var(--font-mono)",
              fontSize: 12, outline: "none", marginTop: 6, resize: "vertical",
            }}
          />
        ) : (
          <div className="value-big" style={{ fontFamily: "var(--font-sans)", fontWeight: 500, fontSize: 13.5, lineHeight: 1.4 }}>
            {t.description}
          </div>
        )}
      </div>

      <div className="inspector-body">
        <div className="section-label">METADATA</div>
        <table className="kv-table">
          <tbody>
            <tr><td>Task ID</td><td className="mono">T-{t.id}</td></tr>
            <tr>
              <td>Priority</td>
              <td>{editing
                ? <select value={draft.priority} onChange={(e) => setDraft({ ...draft, priority: e.target.value })} style={selStyle2}>
                    {["critical", "high", "medium", "low"].map(p => <option key={p} value={p}>{p}</option>)}
                  </select>
                : <PrioPill p={t.priority} />
              }</td>
            </tr>
            <tr>
              <td>Kind</td>
              <td>{editing
                ? <select value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value })} style={selStyle2}>
                    {["feature", "bug", "refactor", "docs", "ops", "review", "security", "chore"].map(k => <option key={k} value={k}>{k}</option>)}
                  </select>
                : <span className="mono">{t.kind}</span>
              }</td>
            </tr>
            <tr>
              <td>Status</td>
              <td>{editing
                ? <select value={draft.status} onChange={(e) => setDraft({ ...draft, status: e.target.value })} style={selStyle2}>
                    {AGENT_TASK_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
                  </select>
                : <TaskStatusPill s={t.status} />
              }</td>
            </tr>
            <tr>
              <td>Assigned to</td>
              <td>{editing
                ? <select value={draft.assignedTo || ""} onChange={(e) => setDraft({ ...draft, assignedTo: e.target.value || null })} style={selStyle2}>
                    <option value="">— unassigned —</option>
                    {AGENTS.map(a => <option key={a.id} value={a.id}>{a.name} ({a.cli}/{a.role})</option>)}
                  </select>
                : (t.assignedTo ? <AgentChip id={t.assignedTo} /> : <span className="mono" style={{ color: "var(--fg-3)" }}>—</span>)
              }</td>
            </tr>
            <tr><td>Attempts</td><td className="tabular">{t.attempts} / 5</td></tr>
            <tr><td>Created</td><td className="tabular">{fmtTime(t.createdAt)} ago</td></tr>
            {t.claimedAt && <tr><td>Claimed</td><td className="tabular">{fmtTime(t.claimedAt)} ago</td></tr>}
            {t.completedAt && <tr><td>Completed</td><td className="tabular">{fmtTime(t.completedAt)} ago</td></tr>}
          </tbody>
        </table>

        {t.blockedReason && !editing && (
          <>
            <div className="section-label">BLOCKED</div>
            <div style={{ margin: "0 12px 12px", padding: "8px 10px", background: "var(--amber-bg)", borderLeft: "2px solid var(--amber)", color: "var(--amber)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
              {t.blockedReason}
            </div>
          </>
        )}

        <div className="section-label">AGENT PROMPT</div>
        {editing ? (
          <div style={{ padding: "0 12px 8px" }}>
            <textarea
              value={draft.prompt}
              onChange={(e) => setDraft({ ...draft, prompt: e.target.value })}
              rows={6}
              style={{
                width: "100%", background: "var(--bg-0)", border: "1px solid var(--line-2)",
                padding: "8px 10px", color: "var(--fg-1)", fontFamily: "var(--font-mono)",
                fontSize: 11, outline: "none", resize: "vertical", lineHeight: 1.5,
              }}
            />
          </div>
        ) : (
          <div style={{ padding: "0 12px 8px" }}>
            <div style={{ background: "var(--bg-0)", border: "1px solid var(--line-1)", padding: "8px 10px", fontFamily: "var(--font-mono)", fontSize: 10.5, color: "var(--fg-1)", lineHeight: 1.5, whiteSpace: "pre-wrap" }}>
              {t.prompt}
            </div>
          </div>
        )}

        {!editing && (
          <>
            <div className="section-label">EVENT TRAIL</div>
            <div style={{ padding: "0 0 8px" }}>
              {[
                { type: "TaskCreated",    color: "cyan",   t: t.createdAt,    msg: "task added to queue" },
                t.claimedAt && { type: "TaskClaimed",  color: "amber",  t: t.claimedAt,    msg: <>by <span className="worker" style={{ color: "var(--magenta)" }}>{t.assignedTo}</span></> },
                t.attempts > 1 && { type: "TaskRetried",  color: "amber",  t: t.claimedAt + 12000, msg: `attempt ${t.attempts}` },
                t.status === "completed" && { type: "TaskCompleted", color: "green", t: t.completedAt, msg: "diff merged · build green" },
                t.status === "failed" && { type: "TaskFailed", color: "red", t: t.completedAt, msg: "exceeded max attempts" },
                t.status === "blocked" && { type: "TaskBlocked",  color: "amber",  t: Date.now() - 10*60_000, msg: t.blockedReason },
              ].filter(Boolean).reverse().map((e, i) => (
                <div key={i} className="event-row">
                  <span className="t">{fmtTime(e.t)}</span>
                  <span className={"k " + e.color}>{e.type.replace(/Task/, "")}</span>
                  <span className="body">{e.msg}</span>
                </div>
              ))}
            </div>
          </>
        )}

        <div className="section-label">ACTIONS</div>
        <div style={{ padding: "4px 12px 14px", display: "flex", flexWrap: "wrap", gap: 4 }}>
          {editing ? (
            <>
              <button className="btn primary tiny" onClick={() => onSave(draft)}>Save</button>
              <button className="btn ghost tiny" onClick={() => setEditing(false)}>Cancel</button>
            </>
          ) : (
            <>
              <button className="btn primary tiny" onClick={() => setEditing(true)}>Edit</button>
              <button className="btn ghost tiny">Reassign</button>
              <button className="btn ghost tiny">Move to Top</button>
              <button className="btn ghost tiny">Requeue</button>
              <button className="btn ghost tiny">Duplicate</button>
              <button className="btn ghost tiny">Pause</button>
              <button className="btn danger tiny" onClick={() => onDelete(t)}>Delete</button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

const selStyle2 = {
  background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "2px 4px",
  color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontSize: 11, outline: "none",
};

// ============================================================ ADD MODAL

function TaskModal({ onClose }) {
  return (
    <div style={{
      position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)",
      backdropFilter: "blur(2px)", display: "grid", placeItems: "center", zIndex: 100,
    }} onClick={onClose}>
      <div style={{ background: "var(--bg-1)", border: "1px solid var(--line-3)", width: 580 }} onClick={(e) => e.stopPropagation()}>
        <div style={{
          height: 32, padding: "0 14px", borderBottom: "1px solid var(--line-2)",
          background: "var(--bg-2)", display: "flex", alignItems: "center", gap: 8,
          fontFamily: "var(--font-cond)", fontWeight: 700, letterSpacing: "0.14em", textTransform: "uppercase", fontSize: 11,
        }}>
          <span style={{ width: 6, height: 6, background: "var(--accent)" }} />
          New Agent Task
          <button className="icon-btn" style={{ marginLeft: "auto" }} onClick={onClose}>×</button>
        </div>
        <div style={{ padding: 16, display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
          <FormField label="Short description" full>
            <input style={inStyle2} placeholder="Bug #47: HttpProbe ignores robots.txt for in-scope hosts" />
          </FormField>
          <FormField label="Priority">
            <select style={inStyle2} defaultValue="medium">
              {["critical", "high", "medium", "low"].map(p => <option key={p}>{p}</option>)}
            </select>
          </FormField>
          <FormField label="Kind">
            <select style={inStyle2}>
              {["feature", "bug", "refactor", "docs", "ops", "review", "security", "chore"].map(k => <option key={k}>{k}</option>)}
            </select>
          </FormField>
          <FormField label="Assign to (optional)" full>
            <select style={inStyle2} defaultValue="">
              <option value="">— pool · any matching agent picks up —</option>
              {AGENTS.map(a => <option key={a.id} value={a.id}>{a.name} · {a.cli}/{a.role}</option>)}
            </select>
          </FormField>
          <FormField label="Prompt / context for the agent" full>
            <textarea
              rows={7}
              placeholder="Investigate why HttpProbe fetches paths even when robots.txt disallows.
Reproduce on a test target. Patch with a robots.txt check before fetch.
Add a unit test."
              style={{ ...inStyle2, resize: "vertical", lineHeight: 1.5, fontSize: 11 }}
            />
          </FormField>
          <FormField label="Max attempts"><input style={inStyle2} defaultValue="3" /></FormField>
          <FormField label="Timeout (sec)"><input style={inStyle2} defaultValue="900" /></FormField>
        </div>
        <div style={{
          padding: 12, borderTop: "1px solid var(--line-2)", background: "var(--bg-2)",
          display: "flex", gap: 6, justifyContent: "flex-end",
        }}>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={onClose}>Add Task</button>
        </div>
      </div>
    </div>
  );
}

const inStyle2 = {
  background: "var(--bg-0)", border: "1px solid var(--line-2)", padding: "5px 8px",
  color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontSize: 11.5, outline: "none", width: "100%",
};

Object.assign(window, { AgentTasksPage });
