/* eslint-disable */
// Shared atomic components.

const { useState, useEffect, useRef, useMemo, useCallback } = React;

// --- formatters ---
function fmtNum(n) {
  if (n == null) return "—";
  if (n >= 1e6) return (n / 1e6).toFixed(1) + "M";
  if (n >= 1e3) return (n / 1e3).toFixed(1) + "k";
  return String(n);
}
function fmtTime(ts) {
  if (!ts) return "—";
  const d = (Date.now() - ts) / 1000;
  if (d < 60) return Math.floor(d) + "s";
  if (d < 3600) return Math.floor(d / 60) + "m";
  if (d < 86400) return Math.floor(d / 3600) + "h";
  return Math.floor(d / 86400) + "d";
}
function fmtClock(ts) {
  const d = new Date(ts);
  const pad = (n) => String(n).padStart(2, "0");
  return pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds());
}
function fmtDur(ms) {
  if (ms == null) return "—";
  if (ms < 1000) return ms + "ms";
  if (ms < 60000) return (ms / 1000).toFixed(1) + "s";
  return Math.floor(ms / 60000) + "m" + Math.floor((ms % 60000) / 1000) + "s";
}
function fmtBytes(b) {
  if (b == null) return "—";
  if (b < 1024) return b + "B";
  if (b < 1024 * 1024) return (b / 1024).toFixed(1) + "K";
  return (b / 1024 / 1024).toFixed(1) + "M";
}

// --- Pill ---
function Pill({ tone, children, solid, dim, className = "" }) {
  return (
    <span className={`pill ${tone || ""} ${solid ? "solid" : ""} ${dim ? "dim" : ""} ${className}`}>
      {children}
    </span>
  );
}

// --- StatusPill ---
function StatusPill({ status }) {
  const map = {
    InScope: "green",
    Active: "cyan",
    New: "amber",
    OutOfScope: "dim",
    Archived: "dim",
    Error: "red",
  };
  return <Pill tone={map[status] || "dim"}>{status}</Pill>;
}

// --- Bar gauge ---
function Bar({ value, tone, width = 56 }) {
  const v = Math.max(0, Math.min(100, value));
  return (
    <span className={`bar ${tone || ""}`} style={{ width }}>
      <span className="fill" style={{ width: v + "%" }} />
    </span>
  );
}

// --- LED meter (1–10 cells) ---
function LED({ value, max = 10, tone }) {
  const v = Math.round((value / 100) * max);
  return (
    <span className={`led ${tone || ""}`}>
      {Array.from({ length: max }, (_, i) => (
        <span key={i} className={"cell" + (i < v ? " on" : "")} />
      ))}
    </span>
  );
}

// --- ASCII sparkline ---
function Spark({ data, tone, width = 60, height = 14 }) {
  const max = Math.max(...data, 1);
  const min = Math.min(...data);
  const w = width / Math.max(1, data.length - 1);
  const pts = data.map((d, i) => [i * w, height - ((d - min) / (max - min || 1)) * (height - 2) - 1]);
  const path = pts.map((p, i) => (i === 0 ? "M" : "L") + p[0].toFixed(1) + "," + p[1].toFixed(1)).join(" ");
  const colorMap = {
    accent: "var(--accent)", amber: "var(--amber)", cyan: "var(--cyan)",
    green: "var(--green)", red: "var(--red)", magenta: "var(--magenta)",
  };
  const c = colorMap[tone] || "var(--accent)";
  return (
    <svg width={width} height={height} style={{ display: "block" }}>
      <path d={path} stroke={c} strokeWidth="1" fill="none" />
      <path d={path + ` L${width},${height} L0,${height} Z`} fill={c} opacity="0.12" />
    </svg>
  );
}

// --- Icons (12px monospaced glyphs) ---
const ICONS = {
  command: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <rect x="2" y="2" width="4" height="4" /><rect x="8" y="2" width="4" height="4" />
      <rect x="2" y="8" width="4" height="4" /><rect x="8" y="8" width="4" height="4" />
    </svg>
  ),
  assets: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M2 3.5h10M2 7h10M2 10.5h10" /><circle cx="4" cy="3.5" r="0.7" fill="currentColor" />
      <circle cx="6.5" cy="7" r="0.7" fill="currentColor" /><circle cx="3" cy="10.5" r="0.7" fill="currentColor" />
    </svg>
  ),
  targets: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <circle cx="7" cy="7" r="5" /><circle cx="7" cy="7" r="2.5" /><circle cx="7" cy="7" r="0.6" fill="currentColor" stroke="none" />
    </svg>
  ),
  workers: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <circle cx="7" cy="7" r="5" /><path d="M7 2v2M7 10v2M2 7h2M10 7h2M3.5 3.5l1.4 1.4M9.1 9.1l1.4 1.4M9.1 4.9l1.4-1.4M3.5 10.5l1.4-1.4" />
    </svg>
  ),
  workerTypes: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M3 2h8v4H3zM3 8h8v4H3z" /><path d="M5 4h4M5 10h4" />
    </svg>
  ),
  subs: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M2 3h6l2 2h2v6H2z" /><path d="M5 7h4" />
    </svg>
  ),
  tasks: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <rect x="2" y="3" width="10" height="2" /><rect x="2" y="6.5" width="7" height="2" /><rect x="2" y="10" width="4" height="2" />
    </svg>
  ),
  findings: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M3 2v10l4-2 4 2V2z" />
    </svg>
  ),
  events: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M2 7h2l1.5-4 3 8 1.5-4h2.5" />
    </svg>
  ),
  contexts: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M2 4l5-2 5 2v6l-5 2-5-2z" /><path d="M2 4l5 2 5-2M7 6v6" />
    </svg>
  ),
  settings: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <circle cx="7" cy="7" r="2" /><path d="M7 1v2M7 11v2M1 7h2M11 7h2M2.5 2.5l1.4 1.4M10.1 10.1l1.4 1.4M10.1 3.9l1.4-1.4M2.5 11.5l1.4-1.4" />
    </svg>
  ),
  "agents-ai": (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <rect x="3" y="3" width="8" height="6" /><path d="M7 3V1M5 11l-1 2M9 11l1 2M5 5.5h1M8 5.5h1M5 7.5h4" />
    </svg>
  ),
  search: (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.3">
      <circle cx="5" cy="5" r="3.5" /><path d="M7.5 7.5L10 10" />
    </svg>
  ),
  bell: (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M3 10V7a4 4 0 018 0v3l1 1H2z" /><path d="M5.5 12.5a1.5 1.5 0 003 0" />
    </svg>
  ),
  caretDown: (
    <svg width="8" height="8" viewBox="0 0 8 8" fill="currentColor"><path d="M0 2h8L4 7z" /></svg>
  ),
  caretRight: (
    <svg width="8" height="8" viewBox="0 0 8 8" fill="currentColor"><path d="M2 0v8l5-4z" /></svg>
  ),
  filter: (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M1 2h10l-4 5v4l-2-1V7z" />
    </svg>
  ),
  download: (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.2">
      <path d="M6 1v8M3 6l3 3 3-3M2 11h8" />
    </svg>
  ),
  star: (
    <svg width="11" height="11" viewBox="0 0 11 11" fill="currentColor"><path d="M5.5 0.5L7 4l3.5.5-2.5 2.5.6 3.5L5.5 8.8 2.4 10.5 3 7 0.5 4.5 4 4z"/></svg>
  ),
};

// --- Card ---
function Card({ title, tick, corner, children, className = "" }) {
  return (
    <div className={`card ${className}`}>
      {title && (
        <div className="card-header">
          {tick !== false && <span className="pre-tick" />}
          <span>{title}</span>
          {corner && <span className="corner">{corner}</span>}
        </div>
      )}
      <div className="card-body">{children}</div>
    </div>
  );
}

// --- Type glyph (used in asset rows) ---
function TypeGlyph({ type }) {
  const t = ASSET_TYPES.find((x) => x.id === type);
  if (!t) return <span className="mono text-fg-3">·</span>;
  return <span className={`mono text-${t.color}`}>{t.glyph}</span>;
}

// --- URL value renderer (color-codes parts) ---
function ValueCell({ asset }) {
  if (asset.type === "FindingCandidate") {
    return (
      <span className="value-cell">
        <span className="star" style={{ color: "var(--magenta)" }}>★</span>
        <span style={{ color: "var(--fg-0)" }}>{asset.value}</span>
      </span>
    );
  }
  if (asset.urlParts) {
    const { scheme, host, path } = asset.urlParts;
    const [pathPart, qs] = path.split("?");
    return (
      <span className="value-cell">
        <span className="scheme">{scheme}://</span>
        <span className="host">{host}</span>
        <span className="path">{pathPart}</span>
        {qs && <span className="qs">?{qs}</span>}
      </span>
    );
  }
  return <span className="value-cell"><span className="host">{asset.value}</span></span>;
}

// --- Tag chip ---
function TagChip({ tag }) {
  const tone = tag === "hot" ? "hot" :
               tag === "api" || tag === "graphql" || tag === "swagger" ? "api" :
               tag === "admin" || tag === "auth" ? "adm" : "";
  return <span className={`tag-chip ${tone}`}>{tag}</span>;
}

// expose
Object.assign(window, {
  fmtNum, fmtTime, fmtClock, fmtDur, fmtBytes,
  Pill, StatusPill, Bar, LED, Spark, Card, TypeGlyph, ValueCell, TagChip, ICONS
});
