/* eslint-disable */
// ArgusDataGrid — shared resizable, sortable data grid control.

const { useState, useEffect, useCallback, useMemo } = React;

function ArgusDataGrid({
  columns = [],     // [{ key, label, className, render, width }]
  rows = [],        // data rows
  rowKey = "id",    // property name for row key
  selectedId,       // currently selected row id
  onSelect,         // (row) => void
  multiSel,         // Set of selected row ids
  onMultiSel,       // (set) => void
  sortKey,
  sortDir = "desc",
  onSort,
  emptyMsg = "// no data",
  className = "",
}) {
  const [colWidths, setColWidths] = useState(() => {
    const widths = {};
    columns.forEach(c => { widths[c.key] = c.width || 80; });
    return widths;
  });
  const [resizing, setResizing] = useState(null);

  const handleMouseDown = useCallback((e, key) => {
    e.preventDefault();
    e.stopPropagation();
    setResizing({ key, startX: e.clientX, startWidth: colWidths[key] });
  }, [colWidths]);

  useEffect(() => {
    if (!resizing) return;
    const handleMouseMove = (e) => {
      const delta = e.clientX - resizing.startX;
      setColWidths(prev => ({ ...prev, [resizing.key]: Math.max(30, resizing.startWidth + delta) }));
    };
    const handleMouseUp = () => setResizing(null);
    document.addEventListener("mousemove", handleMouseMove);
    document.addEventListener("mouseup", handleMouseUp);
    return () => {
      document.removeEventListener("mousemove", handleMouseMove);
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, [resizing]);

  const sortArrow = (k) => sortKey === k ? (sortDir === "asc" ? "▲" : "▼") : "";

  return (
    <div className={`argus-grid-wrap ${className}`}>
      <table className="argus-grid">
        <thead>
          <tr>
            {multiSel !== undefined && (
              <th style={{ width: 22, minWidth: 22 }} className="c-check"></th>
            )}
            {columns.map(c => (
              <th
                key={c.key}
                className={c.className || ""}
                style={{ width: colWidths[c.key], minWidth: colWidths[c.key], position: "relative" }}
                onClick={() => onSort && c.key && onSort(c.key)}
              >
                <span style={{ display: "flex", alignItems: "center", gap: 4 }}>
                  {c.label}
                  {c.label && onSort && <span style={{ fontSize: 9, opacity: 0.7 }}>{sortArrow(c.key)}</span>}
                </span>
                <div
                  onMouseDown={(e) => c.label && handleMouseDown(e, c.key)}
                  style={{
                    position: "absolute", right: 0, top: 0, bottom: 0, width: 6, cursor: "col-resize",
                    zIndex: 3,
                  }}
                />
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length + (multiSel !== undefined ? 1 : 0)} style={{ padding: 40, textAlign: "center", color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 11 }}>
                {emptyMsg}
              </td>
            </tr>
          ) : rows.map(row => {
            const id = row[rowKey];
            const isSelected = selectedId === id;
            const isMulti = multiSel && multiSel.has(id);
            return (
              <tr
                key={id}
                className={`${isSelected ? "selected" : ""}`}
                onClick={(e) => onSelect && onSelect(row, e)}
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
                {columns.map(c => (
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
  );
}

Object.assign(window, { ArgusDataGrid });