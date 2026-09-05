/**
 * Formatting and small numeric helpers for the career view.
 *
 * Two rules govern everything in here.
 *
 * 1. If the API sent a `formatted` string, that string is what gets rendered. Nothing in
 *    this file re-formats a KPI value, a breakdown row's headline number, or a delta --
 *    those all arrive pre-formatted and culture-invariant. What is left over is the
 *    handful of fields the API sends as bare numbers with no companion string:
 *    `CareerTotals`, `MatchSummary`, trend axis ticks and trend tooltips. Those have to
 *    become text somewhere, so it happens here, once, and without `toLocaleString` so the
 *    output cannot drift with the reader's locale.
 * 2. Nothing is invented. A missing number formats to `null` and the caller renders an
 *    empty cell, never a zero.
 */

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

export function isFiniteNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/**
 * Every coordinate that reaches an SVG attribute goes through this. A NaN inside a path's
 * `d` silently drops the whole path rather than erroring, so non-finite becomes the
 * fallback instead, and the rounding keeps the markup readable.
 */
export function safe(value: number, fallback = 0): number {
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : fallback
}

export function clamp(value: number, lo: number, hi: number): number {
  if (!Number.isFinite(value)) return lo
  if (value < lo) return lo
  if (value > hi) return hi
  return value
}

/**
 * Durations are documented as seconds, and Halo sends seconds. Destiny's service hands
 * back a .NET `TimeSpan`, which serialises as "19:27:56" or "1.19:27:56", so the runtime
 * value does not always match the declared `number`. Rather than render "NaN" at the one
 * place a reader would notice, both spellings are accepted and anything else is refused.
 */
export function asSeconds(value: number | null | undefined): number | null {
  if (isFiniteNumber(value)) return value
  const raw: unknown = value
  if (typeof raw !== 'string') return null
  const withDays = /^(\d+)\.(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(raw)
  const clock = /^(\d{1,4}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(raw)
  const parts = withDays
    ? [withDays[1], withDays[2], withDays[3], withDays[4]]
    : clock
      ? ['0', clock[1], clock[2], clock[3]]
      : null
  if (!parts) return null
  const scale = [86400, 3600, 60, 1]
  let total = 0
  for (let i = 0; i < 4; i += 1) {
    const piece = parts[i]
    const factor = scale[i]
    if (piece === undefined || factor === undefined) return null
    const n = Number(piece)
    if (!Number.isFinite(n)) return null
    total += n * factor
  }
  return total
}

/** "23h 38m", "13m 15s", "45s". Null when there is no duration to show. */
export function formatDuration(value: number | null | undefined): string | null {
  const seconds = asSeconds(value)
  if (seconds === null || seconds < 0) return null
  const whole = Math.round(seconds)
  const hours = Math.floor(whole / 3600)
  const minutes = Math.floor((whole % 3600) / 60)
  const rest = whole % 60
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return rest > 0 ? `${minutes}m ${rest}s` : `${minutes}m`
  return `${rest}s`
}

/** Grouped integer, invariant: 1577 -> "1,577". */
export function formatInt(value: number | null | undefined): string | null {
  if (!isFiniteNumber(value)) return null
  const rounded = Math.round(value)
  const sign = rounded < 0 ? '-' : ''
  const digits = String(Math.abs(rounded))
  return sign + digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',')
}

export function formatFixed(value: number | null | undefined, places: number): string | null {
  if (!isFiniteNumber(value)) return null
  return value.toFixed(places)
}

/** Accuracy and win rate arrive as fractions in [0,1]; they are shown as percentages. */
export function formatPercent(value: number | null | undefined, places = 1): string | null {
  if (!isFiniteNumber(value)) return null
  return `${(value * 100).toFixed(places)}%`
}

interface UtcParts {
  year: number
  month: number
  day: number
  hour: number
  minute: number
}

function utcParts(iso: string | null | undefined): UtcParts | null {
  if (typeof iso !== 'string' || iso.length === 0) return null
  const ms = Date.parse(iso)
  if (!Number.isFinite(ms)) return null
  const d = new Date(ms)
  return {
    year: d.getUTCFullYear(),
    month: d.getUTCMonth(),
    day: d.getUTCDate(),
    hour: d.getUTCHours(),
    minute: d.getUTCMinutes(),
  }
}

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n)
}

/** "2 Sep 2026 20:10" -- always UTC, and every caller labels the column as such. */
export function formatUtcDateTime(iso: string | null | undefined): string | null {
  const p = utcParts(iso)
  if (!p) return null
  const month = MONTHS[p.month] ?? '?'
  return `${p.day} ${month} ${p.year} ${pad2(p.hour)}:${pad2(p.minute)}`
}

/** "6 Jun" -- axis ticks. */
export function formatUtcDay(iso: string | null | undefined): string | null {
  const p = utcParts(iso)
  if (!p) return null
  const month = MONTHS[p.month] ?? '?'
  return `${p.day} ${month}`
}

/** "6 Jun 2026" -- tooltips and the trend table, where the year is worth having. */
export function formatUtcDayFull(iso: string | null | undefined): string | null {
  const p = utcParts(iso)
  if (!p) return null
  const month = MONTHS[p.month] ?? '?'
  return `${p.day} ${month} ${p.year}`
}

/** Midnight UTC for a plain "2026-06-06", tolerant of a full timestamp. */
export function parseUtc(iso: string | null | undefined): number | null {
  if (typeof iso !== 'string' || iso.length === 0) return null
  let ms = Date.parse(iso)
  if (!Number.isFinite(ms) && /^\d{4}-\d{2}-\d{2}$/.test(iso)) ms = Date.parse(`${iso}T00:00:00Z`)
  return Number.isFinite(ms) ? ms : null
}

/**
 * Trend point values have no `formatted` companion -- the series carries a `unit` instead,
 * and this is the one place that turns the two into text.
 */
export function formatTrendValue(unit: string, value: number | null | undefined): string | null {
  if (!isFiniteNumber(value)) return null
  if (unit === '%') return `${(value * 100).toFixed(1)}%`
  if (unit === 'ratio') return value.toFixed(2)
  return Math.abs(value) >= 100 ? value.toFixed(0) : value.toFixed(2)
}

/**
 * Axis ticks take their precision from the gap between them, not from a fixed number of
 * places. A series that moves between 0.36 and 0.50 has a 0.02 tick step, and rounding
 * that to one decimal prints "0.4, 0.4, 0.5, 0.5" -- an axis that lies about being an axis.
 */
export function tickDecimals(unit: string, step: number): number {
  const gap = Math.abs(step) * (unit === '%' ? 100 : 1)
  if (!(gap > 0)) return unit === '%' ? 0 : 2
  return clamp(Math.ceil(-Math.log10(gap)), 0, 4)
}

export function formatTrendTick(unit: string, value: number, decimals: number): string {
  if (!Number.isFinite(value)) return ''
  if (unit === '%') return `${(value * 100).toFixed(decimals)}%`
  return value.toFixed(decimals)
}

/** Slope is a rate of change, so it is signed and carries its period in the label. */
export function formatSlopePerWeek(unit: string, value: number | null | undefined): string | null {
  if (!isFiniteNumber(value)) return null
  const sign = value > 0 ? '+' : value < 0 ? '-' : ''
  const size = Math.abs(value)
  if (unit === '%') return `${sign}${(size * 100).toFixed(2)} pp / week`
  if (unit === 'ratio') return `${sign}${size.toFixed(3)} / week`
  return `${sign}${size.toFixed(2)} / week`
}

export function pluralMatches(samples: number | null | undefined): string | null {
  if (!isFiniteNumber(samples)) return null
  const n = Math.round(samples)
  return `${formatInt(n) ?? String(n)} ${n === 1 ? 'match' : 'matches'}`
}

/** Gridline values: 1 / 2 / 5 x 10^k, so the axis reads in round numbers. */
export function niceTicks(lo: number, hi: number, target = 4): number[] {
  if (!Number.isFinite(lo) || !Number.isFinite(hi) || hi <= lo) return []
  const rough = (hi - lo) / Math.max(1, target)
  if (!Number.isFinite(rough) || rough <= 0) return []
  const magnitude = Math.pow(10, Math.floor(Math.log10(rough)))
  const normalised = rough / magnitude
  const step = (normalised >= 5 ? 5 : normalised >= 2 ? 2 : 1) * magnitude
  if (!Number.isFinite(step) || step <= 0) return []
  const places = Math.max(0, Math.min(8, -Math.floor(Math.log10(step)) + 1))
  const out: number[] = []
  const first = Math.ceil(lo / step) * step
  for (let tick = first; tick <= hi + step * 1e-9 && out.length < 12; tick += step) {
    out.push(Number(tick.toFixed(places)))
  }
  return out
}
