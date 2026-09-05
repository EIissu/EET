import { useId, useState } from 'react'
import type { Breakdown, BreakdownRow } from '../../types'
import { clamp, formatPercent, isFiniteNumber, pluralMatches, safe } from './format'

/**
 * A breakdown as ranked bars, with the same numbers available as a table.
 *
 * These are nominal categories -- maps, modes -- so every bar takes the same hue. Shading
 * them by value would spend the identity channel re-encoding what the bar length already
 * shows, and reads as a ranking the data does not contain.
 *
 * The row label sits above its bar rather than in a left gutter: map and playlist names
 * are long and arrive from the API, so a gutter would either truncate them or push the
 * plot off a phone screen. Names come straight from the API and are rendered as text
 * nodes, never as markup.
 */

const VIEW_W = 520
const ROW_H = 44
const PAD_TOP = 4
const PAD_BOTTOM = 8
const BAR_X = 2
const BAR_H = 14
/** Room at the right for the value that sits at the bar's tip. */
const VALUE_GUTTER = 74
const BAR_MAX = VIEW_W - BAR_X - VALUE_GUTTER
const LOW_SAMPLES = 3

interface Geometry {
  row: BreakdownRow
  index: number
  y: number
  width: number
  low: boolean
}

export function buildBreakdownGeometry(rows: BreakdownRow[]): Geometry[] {
  const usable = Array.isArray(rows) ? rows.filter((row) => row && typeof row.name === 'string') : []
  const values = usable.map((row) => (isFiniteNumber(row.value) ? row.value : 0))
  const max = values.length > 0 ? Math.max(...values, 0) : 0
  return usable.map((row, index) => {
    const value = values[index] ?? 0
    const width = max > 0 ? clamp((value / max) * BAR_MAX, 0, BAR_MAX) : 0
    return {
      row,
      index,
      y: PAD_TOP + index * ROW_H,
      width: safe(width, 0),
      low: isFiniteNumber(row.samples) && row.samples > 0 && row.samples < LOW_SAMPLES,
    }
  })
}

/** Square at the baseline, 4px rounded at the data end. */
export function barPath(x: number, y: number, w: number, h: number, r = 4): string {
  const width = Math.max(0, safe(w, 0))
  const radius = Math.max(0, Math.min(r, width, h / 2))
  const right = safe(x + width, x)
  const top = safe(y, 0)
  const bottom = safe(y + h, h)
  if (width <= 0) return ''
  return [
    `M${safe(x, 0)} ${top}`,
    `H${safe(right - radius, right)}`,
    `Q${right} ${top} ${right} ${safe(top + radius, top)}`,
    `V${safe(bottom - radius, bottom)}`,
    `Q${right} ${bottom} ${safe(right - radius, right)} ${bottom}`,
    `H${safe(x, 0)}`,
    'Z',
  ].join('')
}

export function BreakdownPanel({ breakdown }: { breakdown: Breakdown }) {
  const geometry = buildBreakdownGeometry(breakdown.rows)
  const [active, setActive] = useState<number | null>(null)
  const tipId = useId()

  if (geometry.length === 0) return null

  const height = PAD_TOP + geometry.length * ROW_H + PAD_BOTTOM
  const activeBar = active === null ? null : (geometry[active] ?? null)
  const valueLabel =
    typeof breakdown.valueLabel === 'string' && breakdown.valueLabel.length > 0
      ? breakdown.valueLabel
      : 'Value'

  return (
    <figure className="cv-card" style={{ margin: 0 }}>
      <figcaption className="cv-chart__head">
        <div className="cv-chart__title">
          <h3>{breakdown.label}</h3>
          <div className="cv-chart__sub">Ranked by {valueLabel}</div>
        </div>
      </figcaption>

      <div className="cv-plot cv-bars">
        <svg
          viewBox={`0 0 ${VIEW_W} ${height}`}
          preserveAspectRatio="xMidYMid meet"
          role="img"
          aria-label={`${breakdown.label}: ${geometry.length} rows ranked by ${valueLabel}. Every value is in the table below.`}
        >
          <line
            x1={BAR_X}
            x2={BAR_X}
            y1={PAD_TOP}
            y2={height - PAD_BOTTOM}
            stroke="var(--c-axis)"
            strokeWidth={1}
            shapeRendering="crispEdges"
          />
          {geometry.map((bar) => {
            const name = bar.row.name
            const samples = pluralMatches(bar.row.samples)
            const tipX = safe(BAR_X + bar.width + 8, BAR_X + 8)
            return (
              <g key={`${name}-${bar.index}`}>
                <text
                  x={BAR_X}
                  y={safe(bar.y + 13, bar.y)}
                  fill="var(--c-ink-1)"
                  fontSize={13}
                  fontWeight={560}
                >
                  {name}
                  {samples ? (
                    <tspan fill="var(--c-ink-2)" fontWeight={400}>
                      {' · '}
                      {samples}
                      {bar.low ? ' · thin evidence' : ''}
                    </tspan>
                  ) : null}
                </text>
                {bar.width > 0 ? (
                  <path
                    data-testid="breakdown-bar"
                    d={barPath(BAR_X, bar.y + 20, bar.width, BAR_H)}
                    fill="var(--c-series)"
                    fillOpacity={active === bar.index ? 1 : 0.9}
                  />
                ) : null}
                <text
                  x={tipX}
                  y={safe(bar.y + 20 + BAR_H - 3, bar.y)}
                  fill="var(--c-ink-1)"
                  fontSize={13}
                  fontWeight={620}
                  style={{ fontVariantNumeric: 'tabular-nums' }}
                >
                  {bar.row.formatted}
                </text>
                {/* the hit target is the whole row, not the 14px bar */}
                <rect
                  className="cv-bars__hit"
                  x={0}
                  y={bar.y}
                  width={VIEW_W}
                  height={ROW_H}
                  tabIndex={0}
                  role="button"
                  aria-label={`${name}: ${bar.row.formatted}${samples ? `, ${samples}` : ''}`}
                  aria-describedby={active === bar.index ? tipId : undefined}
                  onPointerEnter={() => setActive(bar.index)}
                  onPointerLeave={() => setActive(null)}
                  onFocus={() => setActive(bar.index)}
                  onBlur={() => setActive(null)}
                />
              </g>
            )
          })}
        </svg>

        {activeBar ? (
          <div
            className="cv-tip"
            id={tipId}
            role="status"
            data-side={activeBar.width > VIEW_W * 0.62 ? 'right' : 'left'}
            data-vside={activeBar.y < ROW_H * 1.5 ? 'below' : 'above'}
            style={{
              left: `${((BAR_X + activeBar.width) / VIEW_W) * 100}%`,
              top: `${((activeBar.y + 20) / height) * 100}%`,
            }}
          >
            <div className="cv-tip__head">{activeBar.row.name}</div>
            <div className="cv-tip__row">
              <span className="cv-tip__key" aria-hidden="true" />
              <span className="cv-tip__val">{activeBar.row.formatted}</span>
              <span className="cv-tip__lab">{valueLabel}</span>
            </div>
            <div className="cv-tip__row">
              <span className="cv-tip__val">{pluralMatches(activeBar.row.samples) ?? '—'}</span>
              <span className="cv-tip__lab">{activeBar.low ? 'thin evidence' : 'played'}</span>
            </div>
            {formatPercent(activeBar.row.share) ? (
              <div className="cv-tip__row">
                <span className="cv-tip__val">{formatPercent(activeBar.row.share)}</span>
                <span className="cv-tip__lab">of matches</span>
              </div>
            ) : null}
          </div>
        ) : null}
      </div>

      <details className="cv-tableview">
        <summary>Table view &mdash; {geometry.length} rows</summary>
        <div className="cv-scroll">
          <table className="cv-table">
            <caption>{breakdown.label}</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col" className="cv-num">
                  {valueLabel}
                </th>
                <th scope="col" className="cv-num">
                  Matches
                </th>
                <th scope="col" className="cv-num">
                  Share
                </th>
              </tr>
            </thead>
            <tbody>
              {geometry.map((bar) => (
                <tr key={`row-${bar.row.name}-${bar.index}`}>
                  <th scope="row" style={{ fontWeight: 500 }}>
                    {bar.row.name}
                  </th>
                  <td className="cv-num">{bar.row.formatted}</td>
                  <td className="cv-num">{isFiniteNumber(bar.row.samples) ? bar.row.samples : ''}</td>
                  <td className="cv-num">{formatPercent(bar.row.share) ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </figure>
  )
}
