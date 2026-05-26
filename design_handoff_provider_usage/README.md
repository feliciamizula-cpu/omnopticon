# Handoff: Provider Usage Redesign

## Overview

Redesign of the `provider-usage` tab in **Argus.Web** (the Blazor UI at `src/Argus.Web`).

The existing view shows provider health badges and mixed usage data in a 2-column card grid. This redesign focuses purely on **usage data**: how much capacity remains in each window, when it resets, and a per-provider enable/disable control. CLI/tool installation status is removed from this view entirely.

---

## About the Design Files

`Provider Usage Prototype.html` in this folder is a **design reference built in HTML/React**. It is not production code. Your task is to recreate these designs in Blazor using the existing Argus.Web component patterns and CSS conventions.

The prototype includes a tab switcher at the top to preview all three variants (V1, V2, V3). All interactive behaviors (toggle, alert config) are wired up in the prototype so you can see exactly how they should work before building.

**Fidelity: High-fidelity.** The prototype uses the exact color values, font, spacing, and interaction model intended for production. Recreate pixel-faithfully; do not substitute the design system's existing colors if they differ.

---

## Variants

Three visual variants were designed. The team should pick one before implementation. They share identical layout logic and data model — only the window cell rendering differs.

| Variant | Description | Best for |
|---------|-------------|----------|
| **V1 — Status Grid** | Window cells have a color-tinted background (green/amber/red dim fill). Heatmap-like — critical cells visually pop without needing to read numbers. | Dense monitoring views |
| **V2 — Bold Stack** | No background tint on cells. Instead the card gets a 3px left accent border colored by the *worst* window. Triggered alert cells get a subtle amber wash. | Cleaner, less colorful UI |
| **V3 — Gauge Panel** | SVG arc gauge per window. Most visual. | Dashboard/overview contexts |

---

## Page Structure

```
┌─ Breadcrumb bar ───────────────────────────────────────────────┐
│  Provider-usage · Usage windows, remaining capacity...         │
├─ Summary bar (4 counters) ─────────────────────────────────────┤
│  ACTIVE  │  CRITICAL  │  WARNING  │  ALERTS                    │
├─ Column header ────────────────────────────────────────────────┤
│  PROVIDER ↓ avg remaining  │  SHORT WINDOW  │  WEEKLY          │
├─ Provider rows (sorted) ───────────────────────────────────────┤
│  [Provider 1]                                                  │
│  [Provider 2]                                                  │
│  ...                                                           │
│  [Disabled providers at bottom, 60% opacity]                   │
└────────────────────────────────────────────────────────────────┘
```

---

## Layout System

All rows (column header + every provider card) use the **same 3-column CSS grid**:

```css
grid-template-columns: 220px 1fr 1fr;
/* [provider name col] [short window col] [weekly col] */
```

This ensures window cells align vertically across all providers, enabling at-a-glance column scanning.

---

## Data Model

### Provider shapes

**Flat-window provider** (Claude, OpenAI, Opencode):
```json
{
  "id": "claude",
  "name": "Claude",
  "subtitle": "Haiku / Sonnet",
  "windows": [
    {
      "period": "5H",
      "label": "5 HOUR",
      "remaining": 73,
      "resetAt": 1748470022000
    },
    {
      "period": "WEEK",
      "label": "WEEKLY",
      "remaining": 6,
      "resetAt": 1748477222000
    }
  ]
}
```

**Model-based provider** (Gemini only):
```json
{
  "id": "gemini",
  "name": "Gemini",
  "subtitle": "Flash / Pro",
  "models": [
    {
      "name": "gemini-2.0-flash",
      "windows": [
        { "period": "DAY",  "label": "DAILY",  "remaining": 92, "resetAt": 1748484422000 },
        { "period": "WEEK", "label": "WEEKLY", "remaining": 80, "resetAt": 1748642822000 }
      ]
    },
    {
      "name": "gemini-1.5-pro",
      "windows": [
        { "period": "DAY",  "label": "DAILY",  "remaining": 12, "resetAt": 1748484422000 },
        { "period": "WEEK", "label": "WEEKLY", "remaining": 45, "resetAt": 1748642822000 }
      ]
    }
  ]
}
```

### Field notes

| Field | Type | Notes |
|-------|------|-------|
| `remaining` | `number \| null` | **Always normalized to % remaining (0–100).** Claude's API returns "used" — invert it: `remaining = 100 - usedPct`. OpenAI/Opencode return remaining directly. `null` means no data yet — render as `—`. |
| `resetAt` | `number \| null` | Unix millisecond timestamp of next window reset. `null` when unavailable. |
| `period` | `'5H' \| 'DAY' \| 'WEEK' \| 'MONTH'` | Used to route windows to the correct display column. |

### Window-to-column mapping

| Column | Periods shown |
|--------|---------------|
| SHORT WINDOW | `5H` (Claude, OpenAI, Opencode) or `DAY` (Gemini models) |
| WEEKLY | `WEEK` (all providers) |

MONTHLY windows exist in the API for OpenAI and Opencode — store them but do not display in the current layout. They may be used for alert rule evaluation or future expansion.

---

## Provider Sort Order

Providers are sorted on every render (reactive to enable/disable toggles):

1. **Enabled providers first**, disabled providers at the bottom
2. Within each group, sort by **average remaining %** descending (highest first)
3. Providers with all-null windows sort below providers with data

```
score = avg(remaining) for all non-null windows
      = -1 if all windows are null
```

Example order with mock data: OpenAI (66%) → Gemini (57%) → Claude (39%) → Opencode (no data)

---

## Enable / Disable

Each provider card has a toggle switch (top-right of name column).

- **Enabled** → full opacity, all controls active
- **Disabled** → `opacity: 0.6` on the whole card, alert buttons and progress bars hidden, window cells show only the % number (greyed), provider drops to bottom of the sorted list

The enabled/disabled state should persist (user preference — localStorage or user settings API).

---

## Color Coding

Applied to: progress bar fill, `%` number, cell background tint (V1), gauge ring (V3), card accent border (V2).

| Threshold | Color | Hex | Use |
|-----------|-------|-----|-----|
| `> 50%` remaining | Green | `#00c9a7` | Healthy |
| `20–50%` remaining | Amber | `#f0a030` | Warning |
| `< 20%` remaining | Red | `#ff4545` | Critical |
| `null` | Dim grey | `#3d4e5c` | No data |

Dim/mid variants for backgrounds and borders:
```css
--green-dim: rgba(0,201,167,.12);   /* cell bg tint      */
--green-mid: rgba(0,201,167,.32);   /* border / accent   */
--amber-dim: rgba(240,160,48,.14);
--amber-mid: rgba(240,160,48,.32);
--red-dim:   rgba(255,69,69,.12);
--red-mid:   rgba(255,69,69,.32);
```

---

## Reset Time Display

- **Default**: relative time string shown inline — `↺ 22m`, `↺ 2h 14m`, `↺ 5d`
- **On hover**: absolute timestamp shown via `title` attribute — `May 28, 11:45`

```
fmtRel logic:
  < 60s   → "{n}s"
  < 60m   → "{n}m"
  < 24h   → "{h}h {m}m" (omit minutes if 0)
  ≥ 24h   → "{d}d {h}h" (omit hours if 0)
```

---

## Alert Configuration

Each window cell has a `+ ALERT` button. Clicking it expands an inline panel with two configurable rules:

| Rule | Field | Behavior |
|------|-------|----------|
| Remaining below | `remBelow: number \| null` | Fire alert when `remaining <= remBelow` |
| Resets within | `rstWithin: number \| null` | Fire alert when `(resetAt - now) / 60000 <= rstWithin` |

Alert rule shape per window key:
```json
{ "remBelow": 20, "rstWithin": 30 }
```

Window key format:
- Regular provider: `"{providerId}|{period}"` e.g. `"claude|5H"`
- Gemini model: `"gemini|{modelName}|{period}"` e.g. `"gemini|gemini-2.0-flash|DAY"`

### Alert button states

| State | Condition | Label | Style |
|-------|-----------|-------|-------|
| No rule | `rule == null` | `+ ALERT` | Dim border, dim text |
| Rule saved, not firing | rule set, threshold not crossed | `● ALERT` | Green border |
| Rule firing | threshold crossed | `⚡ ALERT` | Amber border + amber bg |

**Persist alert rules** to user preferences or a backend settings API. The prototype uses in-memory state only.

---

## Summary Bar

Four live counters at the top of the page:

| Counter | Calculation |
|---------|-------------|
| ACTIVE | Count of providers where `enabled == true` |
| CRITICAL | Count of individual windows where `remaining < 20` |
| WARNING | Count of individual windows where `remaining >= 20 && remaining <= 50` |
| ALERTS | Count of saved alert rules across all windows |

CRITICAL and WARNING count individual windows, not providers. A provider with two critical windows contributes 2 to CRITICAL.

CRITICAL counter text turns red (`#ff4545`) when > 0. WARNING counter turns amber when > 0. ALERTS counter turns green when > 0.

---

## Design Tokens

All values needed to match the existing Argus dark theme:

### Colors
```css
--bg:        #0c1014;   /* page background              */
--surface:   #141a22;   /* card background              */
--surface-2: #1c2530;   /* alert panel, model sub-rows  */
--border:    #252e3a;   /* all dividers and borders     */
--text:      #cdd8e3;   /* primary text                 */
--text-2:    #7d8e9e;   /* subtitle / secondary text    */
--text-dim:  #3d4e5c;   /* labels, placeholders         */
--green:     #00c9a7;
--amber:     #f0a030;
--red:       #ff4545;
```

### Typography
- **Font**: `'JetBrains Mono'` (monospace) — weight 300, 400, 500, 600, 700
- **Provider name**: 14px / weight 600
- **Subtitle**: 10px / weight 400 / `--text-2`
- **Window label**: 8px / weight 400 / `--text-dim` / letter-spacing 0.1em / UPPERCASE
- **Remaining %**: 22px / weight 700
- **"rem." label**: 8px / `--text-dim`
- **Reset time**: 9px / `--text-2`
- **Alert button**: 9px / letter-spacing 0.04em
- **Summary bar label**: 8px / `--text-dim` / letter-spacing 0.1em / UPPERCASE
- **Summary bar value**: 22px / weight 600
- **Column header**: 8px / `--text-dim` / letter-spacing 0.1em / UPPERCASE

### Spacing
- Page horizontal padding: 20px
- Card gap (between provider rows): 8px
- Cell padding: 12px 14px (V1/V2), 10px 14px (V3)
- Name column padding: 12px 16px
- Summary bar cell padding: 12px 20px
- Name column width: 220px (fixed)

### Borders & Radius
- Card border: `1px solid var(--border)`, `border-radius: 6px`
- V2 accent border: `3px solid {colMid(worstPct)}` (left side only)
- Cell dividers: `1px solid var(--border)` (left border on each window cell)
- Progress bar: `border-radius: 2px`, track background `var(--border)`

### Toggle Switch
- Size: 32×18px
- Knob: 12×12px, `border-radius: 50%`
- Track: `border-radius: 9px`
- Enabled: track `#00c9a7`, knob `#0c1014`
- Disabled: track `var(--border)`, knob `var(--text-dim)`
- Transition: `background 0.18s`, `left 0.18s`

### Progress Bar (V1/V2)
- Height: 3px (V1), 4px (V2)
- Fill direction: left → right = most remaining → least remaining
- Fill color: matches health threshold color

### Arc Gauge (V3 only)
- Size: 60×60px SVG
- Track stroke: 5px, `var(--border)`
- Fill stroke: 5px, health color, `stroke-linecap: round`
- Start angle: −90° (12 o'clock)
- Value text: 11px, weight 600, centered

---

## Gemini Special Handling

Gemini is the only provider with per-model data. Its card renders differently:

1. **Provider header row**: Full-width row with name/subtitle/toggle in the name column, and `MODEL BREAKDOWN` label spanning the two window columns
2. **Model rows**: One row per model. Name column shows the model name (indented 28px). Window columns show that model's data.
3. **Model row background**: `rgba(28,37,48,.35)` (slightly different from card surface)
4. **Sort score**: Calculated from the average of all model windows combined

---

## Disabled Provider Behavior

When a provider is toggled off:
- Card `opacity: 0.6`
- Progress bars hidden
- Reset times hidden
- Alert buttons hidden
- Window cell shows only the `%` number in grey (`var(--text-dim)`)
- Provider moves to bottom of the sorted list
- ENABLED/DISABLED label (8px, letter-spacing 0.08em) updates accordingly

---

## Files in This Package

| File | Description |
|------|-------------|
| `Provider Usage Prototype.html` | Interactive design reference. Open in a browser. Use the variant tabs to switch between V1/V2/V3. All interactions (toggle, alert config) are working. |
| `README.md` | This document. |

---

## Implementation Notes

- The column header row and each provider row must use the **same `grid-template-columns` value** so cells align. A CSS custom property or shared class is the cleanest way to do this in Blazor.
- Alert rule state should be persisted. Suggest a user-settings endpoint or `localStorage` keyed by `"argus:alert-rules"`.
- The enabled/disabled state per provider should also persist — suggest the same mechanism or a separate provider-settings endpoint.
- The prototype uses client-side timestamp arithmetic for reset times. In production, `resetAt` comes from the API and is recalculated on each render or on a polling interval.
- Suggest polling or SSE for live usage updates — the existing `Argus.RealtimeService` (SSE) may already support this.
