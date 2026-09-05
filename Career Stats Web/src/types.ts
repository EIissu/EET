/**
 * The shape both backends return.
 *
 * These mirror the C# records in `Career Stats Shared/src/Eet.Trackers.Core/Career.cs`,
 * serialised camelCase. They are hand-written rather than generated because there are only
 * a dozen of them and a generator would be more machinery than the problem deserves -- but
 * they are a contract, so if you change one, change the C# record too.
 *
 * Conventions the API documents on `/api/health`, worth repeating here because getting them
 * wrong produces plausible-looking nonsense rather than an error:
 *
 *   * durations are a number of SECONDS, not milliseconds
 *   * accuracy and win rate are fractions in [0,1], NOT percentages
 *   * every `formatted` string is already culture-invariant; render it, do not re-format it
 */

export type GameKey = 'halo' | 'destiny'

/** Whether a rising number is good news, bad news, or neither. */
export type Better = 'Higher' | 'Lower' | 'Neutral'

export interface Player {
  handle: string
  id: string
  platform: string
  iconUrl: string | null
}

export interface Kpi {
  key: string
  label: string
  value: number
  /** Already formatted and culture-invariant. Display this, not `value`. */
  formatted: string
  better: Better
  /**
   * Change against the previous window, or null when there is no previous window.
   * Null is NOT zero: "no baseline to compare against" and "no change" are different
   * facts and must not render the same way.
   */
  delta: number | null
  deltaFormatted: string | null
  note: string | null
  /** Did it move the way the player wants? Null when there is no delta, or no good direction. */
  improved: boolean | null
}

export interface TrendPoint {
  date: string
  value: number
  /**
   * How many matches produced this point. A day with two matches must not be drawn the
   * same size as a day with forty -- this is the field that stops the chart lying.
   */
  samples: number
}

export interface TrendSeries {
  key: string
  label: string
  unit: string
  better: Better
  points: TrendPoint[]
  /** Exponentially weighted average, already computed server-side. */
  smoothed: number[]
  slope: number
  slopePerWeek: number
  /**
   * 'improving' | 'declining' | 'steady' | 'rising' | 'falling'.
   *
   * Already significance-tested on the server: a slope that has not cleared two standard
   * errors comes back as 'steady' however convincing the line looks. Render this word as
   * given. Do not recompute a direction from `points` and do not soften or strengthen it.
   */
  direction: string
}

export interface MatchSummary {
  id: string
  game: string
  playedAt: string
  /** Seconds. */
  duration: number
  mode: string
  map: string
  playlist: string | null
  won: boolean | null
  kills: number
  deaths: number
  assists: number
  accuracy: number | null
  score: number | null
  kda: number | null
  kd: number
  extra?: Record<string, number> | null
}

export interface BreakdownRow {
  name: string
  value: number
  formatted: string
  samples: number
  share: number | null
  iconUrl: string | null
}

export interface Breakdown {
  key: string
  label: string
  valueLabel: string
  rows: BreakdownRow[]
}

export interface CareerTotals {
  matches: number
  wins: number
  losses: number
  /** Seconds. */
  timePlayed: number
  kills: number
  deaths: number
  assists: number
  winRate: number
  kd: number
}

export interface CareerSnapshot {
  player: Player
  game: string
  generatedAt: string
  /** True when the numbers are synthetic. The UI must say so, loudly and always. */
  isFixture: boolean
  source: string
  headline: Kpi[]
  trends: TrendSeries[]
  recent: MatchSummary[]
  breakdowns: Breakdown[]
  totals: CareerTotals
  warnings: string[]
}

/** RFC 7807 problem document, which is what both APIs return on failure. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
}

/** A failure we can show a person, as opposed to a stack trace. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    /** The API's own suggestion for what to do about it, when it offered one. */
    readonly detail?: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}
