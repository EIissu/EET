import { useId, useMemo, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent } from 'react'
import type { TrendSeries } from '../../types'
import {
  clamp,
  formatSlopePerWeek,
  formatTrendTick,
  formatTrendValue,
  formatUtcDay,
  formatUtcDayFull,
  isFiniteNumber,
  niceTicks,
  parseUtc,
  pluralMatches,
  safe,
  tickDecimals,
} from './format'

/**
 * One trend chart: the raw daily points, sized by how many matches produced each one, and
 * the server's smoothed line over the top. Hand-written SVG -- no charting library.
 *
 * The sample weighting is the point of the whole view. A day made of two matches and a day
 * made of forty are not the same evidence, and a tracker that draws them as identical dots
 * invites the reader to read noise as a trend. Here the dot's area grows with the sample
 * count (radius on a square root, so area stays proportional) and any day thinner than
 * three matches is drawn hollow as well as small -- two channels, so the distinction
 * survives a greyscale print and a colour-blind reader.
 *
 * `direction` is rendered exactly as the server sent it. It has already been
 * significance-tested there: a slope that has not cleared two standard errors comes back
 * "steady" no matter how convincing the line looks, and nothing in this file recomputes,
 * upgrades or softens that word.
 *
 * One y-axis. Always one. Two measures of different scale get two charts.
 */

const VIEW_W = 620
const VIEW_H = 232
const M = { top: 12, right: 58, bottom: 30, left: 44 }
const PLOT_W = VIEW_W - M.left - M.right
const PLOT_H = VIEW_H - M.top - M.bottom
const AXIS_Y = M.top + PLOT_H
const R_MIN = 4
const R_MAX = 8
/** Below this many matches a day is thin evidence, and is drawn hollow to say so. */
const LOW_SAMPLES = 3

export interface TrendMark {
  date: string
  value: number
  samples: number
  smoothed: number | null
  x: number
  y: number
  smoothedY: number | null
  r: number
  low: boolean
}

export interface AxisTick {
  value: number
  y: number
  label: string
}

export interface DateTick {
  x: number
  label: string
  anchor: 'start' | 'middle' | 'end'
}

export interface TrendModel {
  marks: TrendMark[]
  yTicks: AxisTick[]
  xTicks: DateTick[]
  linePath: string
  end: TrendMark | null
  minSamples: number
  maxSamples: number
}

const EMPTY_MODEL: TrendModel = {
  marks: [],
  yTicks: [],
  xTicks: [],
  linePath: '',
  end: null,
  minSamples: 0,
  maxSamples: 0,
}

/**
 * Everything geometric happens here, so it can be tested without a DOM. Every number that
 * reaches an attribute is finite and inside the viewBox: values that cannot be parsed are
 * dropped rather than drawn at NaN, and a flat or single-point series still produces a
 * usable scale instead of a division by zero.
 */
export function buildTrendModel(series: TrendSeries): TrendModel {
  const rawPoints = Array.isArray(series?.points) ? series.points : []
  const smoothing = Array.isArray(series?.smoothed) ? series.smoothed : []
  const unit = typeof series?.unit === 'string' ? series.unit : ''

  const usable = rawPoints
    .map((point, index) => {
      const t = parseUtc(point?.date)
      const smoothed = smoothing[index]
      return {
        date: typeof point?.date === 'string' ? point.date : '',
        t,
        value: point?.value,
        samples: point?.samples,
        smoothed: isFiniteNumber(smoothed) ? smoothed : null,
      }
    })
    .filter((entry) => entry.t !== null && isFiniteNumber(entry.value))
    .sort((a, b) => (a.t ?? 0) - (b.t ?? 0))

  if (usable.length === 0) return EMPTY_MODEL

  const values: number[] = []
  for (const entry of usable) {
    if (isFiniteNumber(entry.value)) values.push(entry.value)
    if (entry.smoothed !== null) values.push(entry.smoothed)
  }
  const rawLo = Math.min(...values)
  const rawHi = Math.max(...values)
  const span = rawHi - rawLo
  const pad = span > 0 ? span * 0.08 : Math.max(Math.abs(rawHi) * 0.1, 0.5)
  let yLo = rawLo - pad
  let yHi = rawHi + pad
  if (!(yHi > yLo)) {
    yLo = rawLo - 0.5
    yHi = rawHi + 0.5
  }

  const sampleCounts = usable.map((entry) => (isFiniteNumber(entry.samples) ? entry.samples : 0))
  const minSamples = sampleCounts.length > 0 ? Math.min(...sampleCounts) : 0
  const maxSamples = sampleCounts.length > 0 ? Math.max(...sampleCounts) : 0

  const t0 = usable[0]?.t ?? 0
  const t1 = usable[usable.length - 1]?.t ?? t0
  const tSpan = t1 - t0

  const xOf = (t: number): number =>
    tSpan > 0 ? M.left + ((t - t0) / tSpan) * PLOT_W : M.left + PLOT_W / 2
  const yOf = (value: number): number =>
    clamp(M.top + (1 - (value - yLo) / (yHi - yLo)) * PLOT_H, M.top, AXIS_Y)

  const marks: TrendMark[] = usable.map((entry, index) => {
    const samples = sampleCounts[index] ?? 0
    // Radius on a square root so the DOT'S AREA, not its width, tracks the match count.
    const norm =
      maxSamples > minSamples ? (samples - minSamples) / (maxSamples - minSamples) : 0.5
    const r = R_MIN + (R_MAX - R_MIN) * Math.sqrt(clamp(norm, 0, 1))
    const value = isFiniteNumber(entry.value) ? entry.value : yLo
    return {
      date: entry.date,
      value,
      samples,
      smoothed: entry.smoothed,
      x: safe(xOf(entry.t ?? t0), M.left),
      y: safe(yOf(value), AXIS_Y),
      smoothedY: entry.smoothed === null ? null : safe(yOf(entry.smoothed), AXIS_Y),
      r: safe(r, R_MIN),
      low: samples > 0 && samples < LOW_SAMPLES,
    }
  })

  let linePath = ''
  for (const mark of marks) {
    if (mark.smoothedY === null) continue
    linePath += `${linePath === '' ? 'M' : 'L'}${mark.x} ${mark.smoothedY}`
  }

  let end: TrendMark | null = null
  for (let i = marks.length - 1; i >= 0; i -= 1) {
    const mark = marks[i]
    if (mark && mark.smoothedY !== null) {
      end = mark
      break
    }
  }

  const tickValues = niceTicks(yLo, yHi, 4)
  const first = tickValues[0]
  const second = tickValues[1]
  const step = first !== undefined && second !== undefined ? second - first : yHi - yLo
  const decimals = tickDecimals(unit, step)
  const yTicks: AxisTick[] = tickValues.map((value) => ({
    value,
    y: safe(yOf(value), AXIS_Y),
    label: formatTrendTick(unit, value, decimals),
  }))

  const wanted = Math.min(4, marks.length)
  const xTicks: DateTick[] = []
  const seen = new Set<string>()
  for (let k = 0; k < wanted; k += 1) {
    const index = wanted === 1 ? 0 : Math.round((k * (marks.length - 1)) / (wanted - 1))
    const mark = marks[index]
    if (!mark) continue
    const label = formatUtcDay(mark.date)
    if (label === null || seen.has(label)) continue
    seen.add(label)
    xTicks.push({
      x: mark.x,
      label,
      anchor: k === 0 ? 'start' : k === wanted - 1 ? 'end' : 'middle',
    })
  }

  return { marks, yTicks, xTicks, linePath, end, minSamples, maxSamples }
}

const DIRECTION_MARKS: Record<string, string> = {
  improving: '▲',
  rising: '▲',
  declining: '▼',
  falling: '▼',
  steady: '▬',
}

/** The direction word, exactly as the server sent it, with a shape as well as a colour. */
export function DirectionChip({ direction }: { direction: string }) {
  const word = typeof direction === 'string' && direction.length > 0 ? direction : 'unknown'
  const mark = DIRECTION_MARKS[word] ?? '·'
  return (
    <span className="cv-dir" data-direction={word}>
      <span className="cv-dir__mark" aria-hidden="true">
        {mark}
      </span>
      <span className="cv-sr">Trend direction, as tested on the server:&nbsp;</span>
      {word}
    </span>
  )
}

export function TrendChart({ series }: { series: TrendSeries }) {
  const model = useMemo(() => buildTrendModel(series), [series])
  const [active, setActive] = useState<number | null>(null)
  const tipId = useId()

  const unit = typeof series.unit === 'string' ? series.unit : ''
  const label = typeof series.label === 'string' && series.label.length > 0 ? series.label : series.key
  const direction = typeof series.direction === 'string' ? series.direction : ''
  const slope = formatSlopePerWeek(unit, series.slopePerWeek)
  const marks = model.marks

  if (marks.length === 0) return null

  const activeMark = active === null ? null : (marks[active] ?? null)
  const endLabel = model.end ? formatTrendValue(unit, model.end.smoothed) : null

  function moveTo(index: number) {
    setActive(clamp(index, 0, marks.length - 1))
  }

  function onPointerMove(event: ReactPointerEvent<SVGSVGElement>) {
    const rect = event.currentTarget.getBoundingClientRect()
    if (!(rect.width > 0)) return
    const vx = ((event.clientX - rect.left) / rect.width) * VIEW_W
    let best: number | null = null
    let bestDistance = Number.POSITIVE_INFINITY
    for (let i = 0; i < marks.length; i += 1) {
      const mark = marks[i]
      if (!mark) continue
      const distance = Math.abs(mark.x - vx)
      if (distance < bestDistance) {
        bestDistance = distance
        best = i
      }
    }
    setActive(best)
  }

  function onKeyDown(event: ReactKeyboardEvent<SVGSVGElement>) {
    const current = active ?? marks.length - 1
    if (event.key === 'ArrowRight') {
      event.preventDefault()
      moveTo(current + 1)
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault()
      moveTo(current - 1)
    } else if (event.key === 'Home') {
      event.preventDefault()
      moveTo(0)
    } else if (event.key === 'End') {
      event.preventDefault()
      moveTo(marks.length - 1)
    } else if (event.key === 'Escape') {
      setActive(null)
    }
  }

  return (
    <figure className="cv-card" style={{ margin: 0 }}>
      <figcaption className="cv-chart__head">
        <div className="cv-chart__title">
          <h3>{label}</h3>
          <div className="cv-chart__sub">
            Daily value, sized by matches played &middot; smoothed average
          </div>
        </div>
        <DirectionChip direction={direction} />
      </figcaption>

      <div className="cv-plot">
        <svg
          viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
          preserveAspectRatio="xMidYMid meet"
          role="img"
          tabIndex={0}
          aria-label={`${label}: ${marks.length} days of play, direction ${direction || 'unknown'}. Arrow keys read each day; the table below has every value.`}
          aria-describedby={activeMark ? tipId : undefined}
          onPointerMove={onPointerMove}
          onPointerLeave={() => setActive(null)}
          onFocus={() => setActive((current) => current ?? marks.length - 1)}
          onBlur={() => setActive(null)}
          onKeyDown={onKeyDown}
        >
          {/* grid: hairline, solid, one step off the surface */}
          {model.yTicks.map((tick) => (
            <g key={`grid-${tick.value}`}>
              <line
                x1={M.left}
                x2={M.left + PLOT_W}
                y1={tick.y}
                y2={tick.y}
                stroke="var(--c-grid)"
                strokeWidth={1}
                shapeRendering="crispEdges"
              />
              <text
                x={M.left - 8}
                y={tick.y + 4}
                textAnchor="end"
                fill="var(--c-ink-muted)"
                fontSize={12.5}
                style={{ fontVariantNumeric: 'tabular-nums' }}
              >
                {tick.label}
              </text>
            </g>
          ))}

          <line
            x1={M.left}
            x2={M.left + PLOT_W}
            y1={AXIS_Y}
            y2={AXIS_Y}
            stroke="var(--c-axis)"
            strokeWidth={1}
            shapeRendering="crispEdges"
          />
          {model.xTicks.map((tick) => (
            <text
              key={`x-${tick.label}`}
              x={tick.x}
              y={AXIS_Y + 17}
              textAnchor={tick.anchor}
              fill="var(--c-ink-muted)"
              fontSize={12.5}
            >
              {tick.label}
            </text>
          ))}

          {/* crosshair first, so it sits under the data it is pointing at */}
          {activeMark ? (
            <line
              className="cv-crosshair"
              x1={activeMark.x}
              x2={activeMark.x}
              y1={M.top}
              y2={AXIS_Y}
              stroke="var(--c-axis)"
              strokeWidth={1}
              shapeRendering="crispEdges"
            />
          ) : null}

          {/* raw daily points: size carries the sample count, hollow carries thin evidence */}
          <g>
            {marks.map((mark, index) => (
              <circle
                key={`${mark.date}-${index}`}
                data-testid="trend-point"
                data-samples={mark.samples}
                data-date={mark.date}
                cx={mark.x}
                cy={mark.y}
                r={mark.r}
                fill={mark.low ? 'var(--c-surface-1)' : 'var(--c-series)'}
                fillOpacity={mark.low ? 1 : 0.55 + 0.35 * fillRamp(mark, model)}
                stroke={mark.low ? 'var(--c-series)' : 'var(--c-surface-1)'}
                strokeWidth={mark.low ? 1.5 : 2}
              />
            ))}
          </g>

          {model.linePath ? (
            <path
              data-testid="trend-smoothed"
              d={model.linePath}
              fill="none"
              stroke="var(--c-series)"
              strokeWidth={2}
              strokeLinejoin="round"
              strokeLinecap="round"
            />
          ) : null}

          {/* one direct label, on the end of the smoothed line */}
          {model.end && model.end.smoothedY !== null && endLabel ? (
            <g>
              <circle
                cx={model.end.x}
                cy={model.end.smoothedY}
                r={4.5}
                fill="var(--c-series)"
                stroke="var(--c-surface-1)"
                strokeWidth={2}
              />
              <text
                x={Math.min(model.end.x + 10, VIEW_W - 50)}
                y={clamp(model.end.smoothedY + 4, M.top + 6, AXIS_Y)}
                fill="var(--c-ink-1)"
                fontSize={13}
                fontWeight={620}
                style={{ fontVariantNumeric: 'tabular-nums' }}
              >
                {endLabel}
              </text>
            </g>
          ) : null}

          {activeMark ? (
            <circle
              cx={activeMark.x}
              cy={activeMark.y}
              r={activeMark.r + 3}
              fill="none"
              stroke="var(--c-series)"
              strokeWidth={2}
            />
          ) : null}
        </svg>

        {activeMark ? (
          <div
            className="cv-tip"
            id={tipId}
            role="status"
            data-side={tipSide(activeMark.x)}
            data-vside={activeMark.y < M.top + PLOT_H * 0.42 ? 'below' : 'above'}
            style={{
              left: `${(activeMark.x / VIEW_W) * 100}%`,
              top: `${(Math.min(activeMark.y, activeMark.smoothedY ?? activeMark.y) / VIEW_H) * 100}%`,
            }}
          >
            <div className="cv-tip__head">{formatUtcDayFull(activeMark.date) ?? activeMark.date} UTC</div>
            <div className="cv-tip__row">
              <span className="cv-tip__key cv-tip__key--dot" aria-hidden="true" />
              <span className="cv-tip__val">{formatTrendValue(unit, activeMark.value) ?? '—'}</span>
              <span className="cv-tip__lab">that day</span>
            </div>
            {activeMark.smoothed !== null ? (
              <div className="cv-tip__row">
                <span className="cv-tip__key" aria-hidden="true" />
                <span className="cv-tip__val">{formatTrendValue(unit, activeMark.smoothed) ?? '—'}</span>
                <span className="cv-tip__lab">smoothed</span>
              </div>
            ) : null}
            <div className="cv-tip__foot">
              {pluralMatches(activeMark.samples) ?? 'no matches'}
              {activeMark.low ? ' — thin evidence' : ''}
            </div>
          </div>
        ) : null}
      </div>

      <div className="cv-legend">
        <span className="cv-legend__item">
          <span className="cv-legend__dot" aria-hidden="true" />
          Daily value &mdash; dot area = matches that day
        </span>
        <span className="cv-legend__item">
          <span className="cv-legend__dot cv-legend__dot--hollow" aria-hidden="true" />
          Fewer than {LOW_SAMPLES} matches
        </span>
        <span className="cv-legend__item">
          <span className="cv-legend__line" aria-hidden="true" />
          Smoothed average
        </span>
      </div>

      <TrendTable series={series} model={model} slope={slope} />
    </figure>
  )
}

function fillRamp(mark: TrendMark, model: TrendModel): number {
  if (model.maxSamples <= model.minSamples) return 0.5
  return clamp((mark.samples - model.minSamples) / (model.maxSamples - model.minSamples), 0, 1)
}

function tipSide(x: number): 'left' | 'center' | 'right' {
  if (x > VIEW_W * 0.72) return 'right'
  if (x < VIEW_W * 0.28) return 'left'
  return 'center'
}

function TrendTable({
  series,
  model,
  slope,
}: {
  series: TrendSeries
  model: TrendModel
  slope: string | null
}) {
  const unit = typeof series.unit === 'string' ? series.unit : ''
  const direction = typeof series.direction === 'string' ? series.direction : 'unknown'
  return (
    <details className="cv-tableview">
      <summary>Table view &mdash; {model.marks.length} days</summary>
      <div className="cv-scroll">
        <table className="cv-table">
          <caption>
            {series.label}. Direction: {direction}
            {slope ? ` (slope ${slope})` : ''}.{' '}
            {direction === 'steady'
              ? 'The server significance-tests the slope; this one has not cleared two standard errors, so it counts as steady however the line looks.'
              : 'Dates are UTC.'}
          </caption>
          <thead>
            <tr>
              <th scope="col">Day (UTC)</th>
              <th scope="col" className="cv-num">
                Value
              </th>
              <th scope="col" className="cv-num">
                Matches
              </th>
              <th scope="col" className="cv-num">
                Smoothed
              </th>
            </tr>
          </thead>
          <tbody>
            {model.marks.map((mark, index) => (
              <tr key={`${mark.date}-${index}`}>
                <th scope="row" style={{ fontWeight: 500 }}>
                  {formatUtcDayFull(mark.date) ?? mark.date}
                </th>
                <td className="cv-num">{formatTrendValue(unit, mark.value) ?? ''}</td>
                <td className="cv-num">{mark.samples}</td>
                <td className="cv-num">{formatTrendValue(unit, mark.smoothed) ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </details>
  )
}
