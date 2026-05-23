/* eslint-disable */
// ResizablePanel — collapsible, pinnable, resizable panel shell.
// Usage:
//   <ResizablePanel id="facets" side="left" defaultWidth={220} minWidth={120} maxWidth={400} label="Filters">
//     ...content...
//   </ResizablePanel>
//
// Props:
//   id           — unique key for localStorage persistence
//   side         — "left" | "right" (which edge gets the resize handle)
//   defaultWidth — initial width in px
//   minWidth     — drag floor
//   maxWidth     — drag ceiling
//   label        — shown in collapsed strip
//   pinnable     — show pin/unpin button (default true)
//   className    — extra class on root

const PANEL_STORAGE_KEY = (id) => `argus_panel_${id}`;

function loadPanelState(id, defaults) {
  try {
    const s = localStorage.getItem(PANEL_STORAGE_KEY(id));
    if (s) return { ...defaults, ...JSON.parse(s) };
  } catch (e) {}
  return defaults;
}

function savePanelState(id, state) {
  try {
    localStorage.setItem(PANEL_STORAGE_KEY(id), JSON.stringify(state));
  } catch (e) {}
}

function ResizablePanel({
  id,
  side = "right",
  defaultWidth = 280,
  minWidth = 80,
  maxWidth = 700,
  label = "",
  pinnable = true,
  children,
  style = {},
  className = "",
}) {
  const defaults = { width: defaultWidth, collapsed: false, pinned: true };
  const [state, setState] = React.useState(() => loadPanelState(id, defaults));
  const dragRef = React.useRef(null);
  const startRef = React.useRef(null);

  const set = (patch) => {
    setState((s) => {
      const next = { ...s, ...patch };
      savePanelState(id, next);
      return next;
    });
  };

  const { width, collapsed, pinned } = state;

  // ── drag-to-resize ──────────────────────────────────────────────
  const onHandleDown = (e) => {
    e.preventDefault();
    startRef.current = { x: e.clientX, w: width };
    const onMove = (ev) => {
      const dx = side === "left"
        ? ev.clientX - startRef.current.x
        : startRef.current.x - ev.clientX;
      const next = Math.max(minWidth, Math.min(maxWidth, startRef.current.w + dx));
      setState((s) => ({ ...s, width: next }));
    };
    const onUp = () => {
      savePanelState(id, state);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  // ── collapsed strip ─────────────────────────────────────────────
  if (collapsed) {
    return (
      <div
        style={{
          width: 22,
          background: "var(--bg-1)",
          borderLeft: side === "right" ? "1px solid var(--line-1)" : "none",
          borderRight: side === "left" ? "1px solid var(--line-1)" : "none",
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          padding: "6px 0",
          gap: 6,
          flexShrink: 0,
          position: pinned ? "relative" : "absolute",
          ...(pinned ? {} : { [side]: 0, top: 0, bottom: 0, zIndex: 20 }),
        }}
      >
        <button
          title={`Expand ${label}`}
          onClick={() => set({ collapsed: false })}
          style={{
            width: 16, height: 16, display: "grid", placeItems: "center",
            color: "var(--fg-3)", background: "transparent", border: 0, cursor: "pointer",
          }}
        >
          {side === "right" ? "◁" : "▷"}
        </button>
        <span style={{
          writingMode: "vertical-rl",
          fontFamily: "var(--font-mono)", fontSize: 9, letterSpacing: "0.14em",
          textTransform: "uppercase", color: "var(--fg-3)", marginTop: 4,
          transform: side === "right" ? "none" : "rotate(180deg)",
        }}>{label}</span>
      </div>
    );
  }

  // ── resize handle ────────────────────────────────────────────────
  const handle = (
    <div
      onMouseDown={onHandleDown}
      style={{
        position: "absolute",
        [side === "left" ? "right" : "left"]: 0,
        top: 0, bottom: 0,
        width: 4,
        cursor: "col-resize",
        zIndex: 10,
        background: "transparent",
      }}
      onMouseEnter={(e) => { e.currentTarget.style.background = "var(--accent)"; e.currentTarget.style.opacity = "0.3"; }}
      onMouseLeave={(e) => { e.currentTarget.style.background = "transparent"; }}
    />
  );

  const panelButtons = (
    <div style={{ display: "flex", gap: 2, marginLeft: "auto" }}>
      {pinnable && (
        <button
          title={pinned ? "Unpin panel" : "Pin panel"}
          onClick={() => set({ pinned: !pinned })}
          style={{
            width: 18, height: 18, display: "grid", placeItems: "center",
            color: pinned ? "var(--accent)" : "var(--fg-3)",
            fontSize: 10, background: "transparent", border: 0, cursor: "pointer",
          }}
        >
          {pinned ? "⊕" : "⊙"}
        </button>
      )}
      <button
        title={`Collapse ${label}`}
        onClick={() => set({ collapsed: true })}
        style={{
          width: 18, height: 18, display: "grid", placeItems: "center",
          color: "var(--fg-3)", fontSize: 11,
          background: "transparent", border: 0, cursor: "pointer",
        }}
      >
        {side === "right" ? "▷" : "◁"}
      </button>
    </div>
  );

  return (
    <div
      style={{
        width: collapsed ? 22 : width,
        flexShrink: 0,
        position: pinned ? "relative" : "absolute",
        ...(pinned ? {} : { [side]: 0, top: 0, bottom: 0, zIndex: 20, boxShadow: side === "right" ? "-4px 0 24px rgba(0,0,0,0.4)" : "4px 0 24px rgba(0,0,0,0.4)" }),
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
        ...style,
      }}
      className={className}
    >
      {/* Resize handle */}
      {handle}

      {/* Panel header strip with collapse/pin buttons */}
      <div style={{
        height: 20,
        background: "var(--bg-2)",
        borderBottom: "1px solid var(--line-1)",
        borderLeft: side === "right" ? "1px solid var(--line-1)" : "none",
        borderRight: side === "left" ? "1px solid var(--line-1)" : "none",
        display: "flex",
        alignItems: "center",
        padding: "0 6px",
        gap: 4,
        flexShrink: 0,
      }}>
        <span style={{
          fontFamily: "var(--font-mono)", fontSize: 8.5, letterSpacing: "0.14em",
          textTransform: "uppercase", color: "var(--fg-3)",
        }}>{label}</span>
        {panelButtons}
      </div>

      {/* Content — takes remaining height */}
      <div style={{
        flex: 1, overflow: "hidden", display: "flex", flexDirection: "column",
        borderLeft: side === "right" ? "1px solid var(--line-1)" : "none",
        borderRight: side === "left" ? "1px solid var(--line-1)" : "none",
      }}>
        {children}
      </div>
    </div>
  );
}

// Sidebar-specific version — horizontal collapse, vertical label
function ResizableSidebar({ id = "sidebar", defaultWidth = 188, minWidth = 48, maxWidth = 340, children }) {
  const defaults = { width: defaultWidth, collapsed: false };
  const [state, setState] = React.useState(() => loadPanelState(id, defaults));
  const startRef = React.useRef(null);

  const set = (patch) => setState((s) => {
    const next = { ...s, ...patch };
    savePanelState(id, next);
    return next;
  });

  const onHandleDown = (e) => {
    e.preventDefault();
    startRef.current = { x: e.clientX, w: state.width };
    const onMove = (ev) => {
      const next = Math.max(minWidth, Math.min(maxWidth, startRef.current.w + ev.clientX - startRef.current.x));
      setState((s) => ({ ...s, width: next }));
    };
    const onUp = () => {
      savePanelState(id, state);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const { width, collapsed } = state;

  if (collapsed) {
    return (
      <div style={{
        gridArea: "side", width: 22, background: "var(--bg-1)",
        borderRight: "1px solid var(--line-1)", display: "flex",
        flexDirection: "column", alignItems: "center", padding: "8px 0", gap: 8,
      }}>
        <button onClick={() => set({ collapsed: false })}
          style={{ color: "var(--fg-3)", fontSize: 12, cursor: "pointer" }} title="Expand sidebar">
          ▷
        </button>
        <span style={{
          writingMode: "vertical-rl", transform: "rotate(180deg)",
          fontFamily: "var(--font-mono)", fontSize: 9, letterSpacing: "0.14em",
          textTransform: "uppercase", color: "var(--fg-3)",
        }}>ARGUS</span>
      </div>
    );
  }

  return (
    <div style={{
      gridArea: "side", width, background: "var(--bg-1)",
      borderRight: "1px solid var(--line-1)", display: "flex",
      flexDirection: "column", overflow: "hidden", position: "relative",
    }}>
      {/* Resize handle on right edge */}
      <div
        onMouseDown={onHandleDown}
        style={{
          position: "absolute", right: 0, top: 0, bottom: 0,
          width: 4, cursor: "col-resize", zIndex: 10,
        }}
        onMouseEnter={(e) => { e.currentTarget.style.background = "var(--accent)"; e.currentTarget.style.opacity = "0.3"; }}
        onMouseLeave={(e) => { e.currentTarget.style.background = "transparent"; }}
      />
      {/* Collapse trigger at the top of nav */}
      <div style={{
        display: "flex", alignItems: "center", justifyContent: "flex-end",
        padding: "4px 6px", borderBottom: "1px solid var(--line-0)",
      }}>
        <button onClick={() => set({ collapsed: true })}
          style={{ color: "var(--fg-3)", fontSize: 10, cursor: "pointer" }} title="Collapse sidebar">
          ◁
        </button>
      </div>
      {children}
    </div>
  );
}

Object.assign(window, { ResizablePanel, ResizableSidebar });
