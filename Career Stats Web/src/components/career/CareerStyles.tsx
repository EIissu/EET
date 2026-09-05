/**
 * The career view's own stylesheet, scoped to `.cv` and shipped inside the component tree.
 *
 * It lives here rather than in `src/styles.css` because that file belongs to another part
 * of the app; everything under `components/career/` has to be self-contained. Every class
 * is prefixed `cv-` for the same reason -- nothing here can collide with a shell class.
 *
 * COLOUR: not one hex below is chosen here. Each role reads a project custom property and
 * falls back to the value the shared dashboard already validated for that role
 * (`Career Stats Shared/web/css/dashboard.css`), so the view is correct whether or not the
 * shell has defined the tokens yet, in light mode and in dark. The chart hue is the
 * palette's slot 1, which passes the dataviz validator on both surfaces:
 *   light #2a78d6 on #f9f9f7 -- lightness, chroma and contrast all PASS
 *   dark  #3987e5 on #0d0d0d -- lightness, chroma and contrast all PASS
 * Dark is declared twice on purpose: once for the OS setting, once for an explicit
 * `data-theme`, so a stamped theme wins in both directions.
 */
export function CareerStyles() {
  return <style>{CSS}</style>
}

const CSS = `
.cv {
  --c-series: var(--series-1, #2a78d6);
  --c-surface-1: var(--surface-1, #fcfcfb);
  --c-surface-2: var(--surface-2, #f4f3f0);
  --c-ink-1: var(--ink-1, #0b0b0b);
  --c-ink-2: var(--ink-2, #52514e);
  --c-ink-muted: var(--ink-muted, #898781);
  --c-grid: var(--grid, #e1e0d9);
  --c-axis: var(--axis, #c3c2b7);
  --c-border: var(--border, rgba(11, 11, 11, 0.10));
  --c-hover-wash: var(--hover-wash, rgba(11, 11, 11, 0.04));
  --c-focus: var(--focus-ring, #2a78d6);
  --c-good: var(--good, #0ca30c);
  --c-good-ink: var(--good-ink, #006300);
  --c-critical: var(--critical, #d03b3b);
  --c-good-wash: var(--good-wash, rgba(12, 163, 12, 0.12));
  --c-critical-wash: var(--critical-wash, rgba(208, 59, 59, 0.12));
  --c-neutral-wash: var(--neutral-wash, rgba(11, 11, 11, 0.05));
  --c-warn-ink: var(--warn-ink, #6b4700);
  --c-warn-wash: var(--warn-wash, #fff4d6);
  --c-warn-edge: var(--warn-edge, #d9a441);
  --c-shadow: var(--shadow, 0 1px 2px rgba(11, 11, 11, 0.04));

  color: var(--c-ink-1);
  font-size: 15px;
  line-height: 1.45;
  max-width: 100%;
  overflow-x: hidden;
}

@media (prefers-color-scheme: dark) {
  :root:where(:not([data-theme="light"])) .cv {
    --c-series: var(--series-1, #3987e5);
    --c-surface-1: var(--surface-1, #1a1a19);
    --c-surface-2: var(--surface-2, #232322);
    --c-ink-1: var(--ink-1, #ffffff);
    --c-ink-2: var(--ink-2, #c3c2b7);
    --c-ink-muted: var(--ink-muted, #898781);
    --c-grid: var(--grid, #2c2c2a);
    --c-axis: var(--axis, #383835);
    --c-border: var(--border, rgba(255, 255, 255, 0.10));
    --c-hover-wash: var(--hover-wash, rgba(255, 255, 255, 0.06));
    --c-focus: var(--focus-ring, #3987e5);
    --c-good-ink: var(--good-ink, #0ca30c);
    --c-good-wash: var(--good-wash, rgba(12, 163, 12, 0.18));
    --c-critical-wash: var(--critical-wash, rgba(208, 59, 59, 0.20));
    --c-neutral-wash: var(--neutral-wash, rgba(255, 255, 255, 0.07));
    --c-warn-ink: var(--warn-ink, #ffd479);
    --c-warn-wash: var(--warn-wash, #3a2d0b);
    --c-warn-edge: var(--warn-edge, #8a6a1f);
    --c-shadow: var(--shadow, none);
  }
}

:root[data-theme="dark"] .cv {
  --c-series: var(--series-1, #3987e5);
  --c-surface-1: var(--surface-1, #1a1a19);
  --c-surface-2: var(--surface-2, #232322);
  --c-ink-1: var(--ink-1, #ffffff);
  --c-ink-2: var(--ink-2, #c3c2b7);
  --c-ink-muted: var(--ink-muted, #898781);
  --c-grid: var(--grid, #2c2c2a);
  --c-axis: var(--axis, #383835);
  --c-border: var(--border, rgba(255, 255, 255, 0.10));
  --c-hover-wash: var(--hover-wash, rgba(255, 255, 255, 0.06));
  --c-focus: var(--focus-ring, #3987e5);
  --c-good-ink: var(--good-ink, #0ca30c);
  --c-good-wash: var(--good-wash, rgba(12, 163, 12, 0.18));
  --c-critical-wash: var(--critical-wash, rgba(208, 59, 59, 0.20));
  --c-neutral-wash: var(--neutral-wash, rgba(255, 255, 255, 0.07));
  --c-warn-ink: var(--warn-ink, #ffd479);
  --c-warn-wash: var(--warn-wash, #3a2d0b);
  --c-warn-edge: var(--warn-edge, #8a6a1f);
  --c-shadow: var(--shadow, none);
}

.cv *, .cv *::before, .cv *::after { box-sizing: border-box; }
.cv h2, .cv h3 { margin: 0; font-weight: 620; letter-spacing: -0.01em; }
.cv h2 { font-size: 16px; }
.cv h3 { font-size: 14.5px; }
.cv p { margin: 0; }

.cv-sr {
  position: absolute;
  width: 1px; height: 1px;
  padding: 0; margin: -1px;
  overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0;
}

.cv-card {
  background: var(--c-surface-1);
  border: 1px solid var(--c-border);
  border-radius: 12px;
  box-shadow: var(--c-shadow);
  padding: 16px;
}

.cv-section { margin-top: 20px; }
.cv-section__head {
  display: flex; flex-wrap: wrap; align-items: baseline; gap: 10px;
  margin-bottom: 10px;
}
.cv-section__note { font-size: 13px; color: var(--c-ink-2); }

/* ---------------------------------------------------------- fixture notice */

.cv-fixture {
  display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap;
  border: 2px solid var(--c-warn-edge);
  border-radius: 12px;
  background: var(--c-warn-wash);
  color: var(--c-warn-ink);
  padding: 10px 14px;
  margin-bottom: 14px;
}
.cv-fixture__mark { font-size: 15px; line-height: 1.2; flex: none; }
.cv-fixture__title { font-weight: 700; font-size: 14px; }
.cv-fixture__body { font-size: 13.5px; }
.cv-fixture__source {
  font-size: 12px; opacity: 0.85; margin-left: auto;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

.cv-warnings { margin: 12px 0 0; padding-left: 20px; font-size: 13px; color: var(--c-ink-2); }
.cv-warnings li + li { margin-top: 4px; }

/* ------------------------------------------------------------- identity */

.cv-identity { display: flex; flex-wrap: wrap; gap: 16px; align-items: flex-start; }
.cv-identity__who { display: flex; gap: 14px; align-items: flex-start; min-width: 0; }
.cv-avatar {
  width: 56px; height: 56px; flex: none; border-radius: 10px; overflow: hidden;
  background: var(--c-surface-2); border: 1px solid var(--c-border);
}
.cv-avatar img { width: 100%; height: 100%; object-fit: cover; display: block; }
.cv-handle {
  font-size: 24px; font-weight: 660; letter-spacing: -0.02em;
  overflow-wrap: anywhere; margin: 0;
}
.cv-meta {
  display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: center;
  font-size: 13px; color: var(--c-ink-2); margin-top: 4px;
}
.cv-meta code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px; color: var(--c-ink-muted);
}
.cv-badge {
  display: inline-flex; align-items: center; gap: 6px;
  border: 1px solid var(--c-border); border-radius: 999px;
  padding: 2px 9px; font-size: 12px; font-weight: 600; color: var(--c-ink-1);
  background: var(--c-neutral-wash);
}
.cv-badge--sample {
  background: var(--c-warn-wash); color: var(--c-warn-ink);
  border-color: var(--c-warn-edge);
}

.cv-totals {
  display: grid; grid-template-columns: repeat(auto-fit, minmax(96px, 1fr));
  gap: 10px 18px; margin-left: auto; min-width: min(100%, 280px);
}
.cv-total__label { font-size: 12px; color: var(--c-ink-2); }
.cv-total__value { font-size: 17px; font-weight: 640; }

/* ------------------------------------------------------------------ kpis */

.cv-kpis {
  display: grid; gap: 10px;
  grid-template-columns: repeat(auto-fit, minmax(158px, 1fr));
  /* Tiles size to their own note. Stretching them all to the longest note in the row
     leaves half a card of empty space under the short ones. */
  align-items: start;
  list-style: none; margin: 0; padding: 0;
}
.cv-kpi {
  background: var(--c-surface-1); border: 1px solid var(--c-border);
  border-radius: 12px; padding: 12px 13px; box-shadow: var(--c-shadow);
}
.cv-kpi__label { font-size: 13px; color: var(--c-ink-2); }
.cv-kpi__value { font-size: 26px; font-weight: 650; letter-spacing: -0.02em; margin-top: 2px; }
.cv-kpi__note { font-size: 11.5px; color: var(--c-ink-muted); margin-top: 6px; }

.cv-delta {
  display: inline-flex; align-items: center; gap: 6px; margin-top: 6px;
  border-radius: 999px; padding: 2px 9px 2px 7px; font-size: 12.5px;
  background: var(--c-neutral-wash); color: var(--c-ink-1);
}
.cv-delta__arrow { font-size: 12px; line-height: 1; color: var(--c-ink-2); }
.cv-delta__num { font-variant-numeric: tabular-nums; font-weight: 600; }
.cv-delta__word { color: var(--c-ink-2); }
.cv-delta[data-delta="good"] { background: var(--c-good-wash); }
.cv-delta[data-delta="good"] .cv-delta__arrow { color: var(--c-good-ink); }
.cv-delta[data-delta="bad"] { background: var(--c-critical-wash); }
.cv-delta[data-delta="bad"] .cv-delta__arrow { color: var(--c-critical); }
.cv-delta[data-delta="zero"] { background: var(--c-neutral-wash); }
.cv-delta[data-delta="none"] {
  background: transparent; padding-left: 0; color: var(--c-ink-muted);
  border: 1px dashed var(--c-border); padding: 2px 9px;
}
.cv-delta[data-delta="none"] .cv-delta__word { color: var(--c-ink-muted); font-style: italic; }

/* ---------------------------------------------------------------- charts */

.cv-charts { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); }
@media (max-width: 720px) { .cv-charts { grid-template-columns: 1fr; } }

.cv-chart__head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.cv-chart__title { margin-right: auto; }
.cv-chart__sub { font-size: 12px; color: var(--c-ink-2); margin-top: 2px; }

.cv-dir {
  display: inline-flex; align-items: center; gap: 6px;
  border-radius: 999px; padding: 2px 10px; font-size: 12.5px; font-weight: 600;
  background: var(--c-neutral-wash); color: var(--c-ink-1);
}
.cv-dir__mark { color: var(--c-ink-2); font-size: 11px; line-height: 1; }
.cv-dir[data-direction="improving"] { background: var(--c-good-wash); }
.cv-dir[data-direction="improving"] .cv-dir__mark { color: var(--c-good-ink); }
.cv-dir[data-direction="declining"] { background: var(--c-critical-wash); }
.cv-dir[data-direction="declining"] .cv-dir__mark { color: var(--c-critical); }

.cv-plot { position: relative; margin-top: 8px; }
.cv-plot svg { display: block; width: 100%; height: auto; touch-action: pan-y; }
.cv-plot svg:focus-visible { outline: 2px solid var(--c-focus); outline-offset: 2px; border-radius: 6px; }

.cv-legend {
  display: flex; flex-wrap: wrap; gap: 6px 16px; margin-top: 8px;
  font-size: 12px; color: var(--c-ink-2);
}
.cv-legend__item { display: inline-flex; align-items: center; gap: 7px; }
.cv-legend__dot {
  width: 11px; height: 11px; border-radius: 50%;
  background: var(--c-series); flex: none;
}
.cv-legend__dot--hollow { background: var(--c-surface-1); border: 2px solid var(--c-series); }
.cv-legend__line { width: 18px; height: 2px; border-radius: 2px; background: var(--c-series); flex: none; }

.cv-tip {
  position: absolute; z-index: 3; pointer-events: none;
  background: var(--c-surface-1); color: var(--c-ink-1);
  border: 1px solid var(--c-border); border-radius: 8px;
  box-shadow: 0 4px 14px rgba(11, 11, 11, 0.14);
  padding: 8px 11px; font-size: 12.5px; min-width: 150px; max-width: 260px;
  transform: translate(-50%, calc(-100% - 14px));
}
.cv-tip[data-side="right"] { transform: translate(-100%, calc(-100% - 14px)); }
.cv-tip[data-side="left"] { transform: translate(0, calc(-100% - 14px)); }
.cv-tip[data-vside="below"] { transform: translate(-50%, 16px); }
.cv-tip[data-vside="below"][data-side="right"] { transform: translate(-100%, 16px); }
.cv-tip[data-vside="below"][data-side="left"] { transform: translate(0, 16px); }
.cv-tip__head { font-size: 12px; color: var(--c-ink-2); margin-bottom: 4px; }
.cv-tip__row { display: flex; align-items: baseline; gap: 7px; white-space: nowrap; }
.cv-tip__row + .cv-tip__row { margin-top: 2px; }
.cv-tip__key { width: 14px; height: 2px; border-radius: 2px; background: var(--c-series); flex: none; }
.cv-tip__key--dot { width: 9px; height: 9px; border-radius: 50%; }
.cv-tip__val { font-weight: 660; font-variant-numeric: tabular-nums; }
.cv-tip__lab { color: var(--c-ink-2); }
.cv-tip__foot {
  margin-top: 5px; padding-top: 5px; font-size: 12px; color: var(--c-ink-2);
  border-top: 1px solid var(--c-border);
}

/* ------------------------------------------------------------ table view */

.cv-tableview { margin-top: 10px; }
.cv-tableview > summary {
  cursor: pointer; font-size: 12.5px; color: var(--c-ink-2);
  padding: 3px 2px; border-radius: 6px; width: fit-content;
}
.cv-tableview > summary:hover { color: var(--c-ink-1); background: var(--c-hover-wash); }
.cv-tableview > summary:focus-visible { outline: 2px solid var(--c-focus); outline-offset: 2px; }

.cv-scroll { overflow-x: auto; max-width: 100%; -webkit-overflow-scrolling: touch; }

.cv-table { border-collapse: collapse; width: 100%; font-size: 13px; }
.cv-table caption {
  caption-side: top; text-align: left; font-size: 12px; color: var(--c-ink-2);
  padding: 6px 0 8px;
}
.cv-table th, .cv-table td {
  padding: 7px 10px; text-align: left; white-space: nowrap;
  border-bottom: 1px solid var(--c-border);
}
.cv-table th { font-weight: 600; color: var(--c-ink-2); font-size: 12px; }
.cv-table thead th { position: sticky; top: 0; background: var(--c-surface-1); z-index: 1; }
.cv-table td.cv-num, .cv-table th.cv-num {
  text-align: right; font-variant-numeric: tabular-nums;
}
.cv-table tbody tr:hover { background: var(--c-hover-wash); }
.cv-table tbody tr:last-child td { border-bottom: 0; }
.cv-empty { color: var(--c-ink-muted); }

/* ------------------------------------------------------------ breakdowns */

.cv-breakdowns { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); }
@media (max-width: 720px) { .cv-breakdowns { grid-template-columns: 1fr; } }
.cv-bars svg { display: block; width: 100%; height: auto; }
.cv-bars__hit { fill: transparent; }
.cv-bars__hit:focus-visible { outline: 2px solid var(--c-focus); outline-offset: -2px; }

/* --------------------------------------------------------------- matches */

.cv-result {
  display: inline-flex; align-items: center; gap: 6px;
  font-weight: 600; font-size: 12.5px;
}
.cv-result__chip {
  display: inline-flex; align-items: center; justify-content: center;
  width: 19px; height: 19px; border-radius: 5px; flex: none;
  font-size: 11px; font-weight: 700; border: 1px solid var(--c-border);
  background: var(--c-neutral-wash); color: var(--c-ink-1);
}
.cv-result[data-result="win"] .cv-result__chip {
  background: var(--c-good-wash); border-color: transparent; color: var(--c-good-ink);
}
.cv-result[data-result="loss"] .cv-result__chip {
  background: var(--c-critical-wash); border-color: transparent; color: var(--c-critical);
}

@media (max-width: 560px) {
  .cv-handle { font-size: 20px; }
  .cv-kpi__value { font-size: 22px; }
  .cv-totals { margin-left: 0; }
}
`
