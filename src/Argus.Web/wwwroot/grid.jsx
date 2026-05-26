/* eslint-disable */
// ArgusDataGrid — shared resizable, sortable data grid control.

const { useState, useEffect, useCallback, useMemo, useRef } = React;

function ArgusDataGrid({
  columns = [],
  rows = [],
  rowKey = "id",
  selectedId,
  onSelect,
  multiSel,
  onMultiSel,
  sortKey,
  sortDir = "desc",
  onSort,
  emptyMsg = "// no data",
  className = "",
  searchable = false,
  filterable = false,
  onContextMenu,
}) {
  const [colWidths, setColWidths] = useState(() => {
    const widths = {};
    columns.forEach(c => { widths[c.key] = c.width || 80; });
    return widths;
  });
  const [contextMenu, setContextMenu] = useState(null);

  const resizeRef = useRef({ key: null, startX: 0, startWidth: 0 });
  const isResizing = useRef(false);

  const handleMouseDown = useCallback((e, key) => {
    e.preventDefault();
    e.stopPropagation();
    resizeRef.current = { key, startX: e.clientX, startWidth: colWidths[key] };
    isResizing.current = true;
  }, [colWidths]);

  useEffect(() => {
    const handleMouseMove = (e) => {
      if (!isResizing.current || !resizeRef.current.key) return;
      const delta = e.clientX - resizeRef.current.startX;
      setColWidths(prev => ({ ...prev, [resizeRef.current.key]: Math.max(30, resizeRef.current.startWidth + delta) }));
    };
    const handleMouseUp = () => {
      isResizing.current = false;
      resizeRef.current = { key: null, startX: 0, startWidth: 0 };
    };
    document.addEventListener("mousemove", handleMouseMove);
    document.addEventListener("mouseup", handleMouseUp);
    return () => {
      document.removeEventListener("mousemove", handleMouseMove);
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, []);

  useEffect(() => {
    const handleClick = () => setContextMenu(null);
    document.addEventListener("click", handleClick);
    return () => document.removeEventListener("click", handleClick);
  }, []);

  const sortArrow = (k) => sortKey === k ? (sortDir === "asc" ? "▲" : "▼") : "";

  const filteredRows = useMemo(() => {
    let r = rows;
    for (const [key, val] of Object.entries({})) {
      if (val) r = r.filter(row => String(row[key]) === val);
    }
    return r;
  }, [rows]);

  const handleRowContextMenu = (e, row) => {
    e.preventDefault();
    setContextMenu({ x: e.clientX, y: e.clientY, row });
    onContextMenu && onContextMenu(e, row);
  };

  const visibleColumns = columns.filter(c => !c.hidden);

  return (
    <div className={`argus-grid-container ${className}`} style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
      <div className="argus-grid-wrap" style={{ flex: 1, overflow: "auto" }}>
        <table className="argus-grid">
          <thead>
            <tr>
              {multiSel !== undefined && (
                <th style={{ width: 22, minWidth: 22 }} className="c-check"></th>
              )}
              {visibleColumns.map(c => (
                <th
                  key={c.key}
                  className={c.className || ""}
                  style={{ width: colWidths[c.key], minWidth: colWidths[c.key], position: "relative" }}
                  onClick={(e) => {
                    if (e.target.closest('.resize-handle')) return;
                    onSort && c.key && !c.noSort && onSort(c.key);
                  }}
                >
                  <span style={{ display: "flex", alignItems: "center", gap: 4, paddingRight: 8 }}>
                    {c.label}
                    {c.label && onSort && !c.noSort && <span style={{ fontSize: 9, opacity: 0.7 }}>{sortArrow(c.key)}</span>}
                  </span>
                  {c.label && (
                    <div
                      className="resize-handle"
                      onMouseDown={(e) => handleMouseDown(e, c.key)}
                      style={{
                        position: "absolute", right: 0, top: 0, bottom: 0, width: 8, cursor: "col-resize",
                        zIndex: 3, background: "transparent",
                      }}
                    />
                  )}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filteredRows.length === 0 ? (
              <tr>
                <td colSpan={visibleColumns.length + (multiSel !== undefined ? 1 : 0)} style={{ padding: 40, textAlign: "center", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
                  {emptyMsg}
                </td>
              </tr>
            ) : filteredRows.map(row => {
              const id = row[rowKey];
              const isSelected = selectedId === id;
              const isMulti = multiSel && multiSel.has(id);
              return (
                <tr
                  key={id}
                  className={`${isSelected ? "selected" : ""}`}
                  onClick={(e) => onSelect && onSelect(row, e)}
                  onContextMenu={(e) => handleRowContextMenu(e, row)}
                >
                  {multiSel !== undefined && (
                    <td className="c-check" style={{ width: 22, minWidth: 22 }}>
                      <span
                        className={"cell-checkbox" + (isMulti ? " on" : "")}
                        onClick={(e) => {
                          e.stopPropagation();
                          if (!onMultiSel) return;
                          const ns = new Set(multiSel);
                          if (ns.has(id)) ns.delete(id); else ns.add(id);
                          onMultiSel(ns);
                        }}
                      />
                    </td>
                  )}
                  {visibleColumns.map(c => (
                    <td
                      key={c.key}
                      className={c.className || ""}
                      style={{ width: colWidths[c.key], minWidth: colWidths[c.key] }}
                    >
                      {c.render ? c.render(row) : row[c.key]}
                    </td>
                  ))}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {contextMenu && (
        <div
          className="context-menu"
          style={{
            position: "fixed", left: contextMenu.x, top: contextMenu.y,
            background: "var(--bg-2)", border: "1px solid var(--line-2)",
            boxShadow: "0 8px 24px rgba(0,0,0,0.4)", zIndex: 1000,
            minWidth: 160, padding: "4px 0",
          }}
          onClick={(e) => e.stopPropagation()}
        >
          {[
            { label: contextMenu.row.enabled ? "Disable" : "Enable", action: "toggle" },
            { label: "Edit", action: "edit" },
            { label: "Delete", action: "delete" },
            { label: "Create a copy...", action: "copy" },
          ].map(item => (
            <div
              key={item.action}
              onClick={() => {
                setContextMenu(null);
                window.__argusContextAction && window.__argusContextAction(item.action, contextMenu.row);
              }}
              style={{
                padding: "6px 16px", fontFamily: "var(--font-mono)", fontSize: 11,
                color: item.action === "delete" ? "var(--red)" : "var(--fg-0)",
                cursor: "pointer",
              }}
              onMouseEnter={(e) => e.target.style.background = "var(--bg-3)"}
              onMouseLeave={(e) => e.target.style.background = "transparent"}
            >
              {item.label}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

Object.assign(window, { ArgusDataGrid });