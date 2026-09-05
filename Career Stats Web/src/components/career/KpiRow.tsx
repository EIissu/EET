import type { Kpi } from '../../types'
import { isFiniteNumber } from './format'

/**
 * The headline row: one stat tile per KPI.
 *
 * The delta is the part worth being careful about. Four facts arrive separately and mean
 * different things, and collapsing any two of them would be a lie:
 *
 *   delta === null   there was no previous window -- nothing to compare against
 *   delta === 0      there was a previous window and the number is identical
 *   improved true    it moved the way the player wants (which, for a "Lower is better"
 *                    KPI like deaths per match, means the number went DOWN)
 *   improved false   it moved against them
 *
 * So the arrow means "better" or "worse", not "up" or "down" -- the signed figure beside
 * it carries the numeric direction, and the screen-reader text spells the meaning out.
 *
 * Note the order of the checks below: a zero delta is neutral *before* `improved` is
 * consulted. The fixture ships `assistsPerMatch` as delta 0 with `improved: false`, and
 * painting that red would tell the reader they got worse at something that did not move.
 */

export type DeltaState = 'good' | 'bad' | 'zero' | 'neutral' | 'none'

export function deltaState(kpi: Kpi): DeltaState {
  if (!isFiniteNumber(kpi.delta)) return 'none'
  if (kpi.delta === 0) return 'zero'
  if (kpi.improved === true) return 'good'
  if (kpi.improved === false) return 'bad'
  return 'neutral'
}

const ARROWS: Record<DeltaState, string> = {
  good: '▲',
  bad: '▼',
  zero: '—',
  neutral: '◆',
  none: '',
}

const WORDS: Record<DeltaState, string> = {
  good: 'better',
  bad: 'worse',
  zero: 'no change',
  neutral: 'changed',
  none: 'No prior window',
}

const SPOKEN: Record<DeltaState, string> = {
  good: 'improved against the previous window',
  bad: 'worse than the previous window',
  zero: 'unchanged against the previous window',
  neutral: 'changed against the previous window, with no better or worse direction',
  none: 'no previous window to compare against',
}

const EXPLAIN: Record<DeltaState, string> = {
  good: 'Moved the way the player wants, against the previous window.',
  bad: 'Moved against the player, compared with the previous window.',
  zero: 'Identical to the previous window.',
  neutral: 'Changed, but this measure has no better or worse direction.',
  none: 'There is no earlier window to compare against, so no change is shown.',
}

export function KpiRow({ items }: { items: Kpi[] }) {
  const kpis = Array.isArray(items) ? items.filter((kpi) => kpi && typeof kpi.label === 'string') : []
  if (kpis.length === 0) return null
  return (
    <section className="cv-section" aria-labelledby="cv-kpis-title">
      <div className="cv-section__head">
        <h2 id="cv-kpis-title">Recent form</h2>
        <span className="cv-section__note">Each tile compares a window against the one before it.</span>
      </div>
      <ul className="cv-kpis">
        {kpis.map((kpi) => (
          <KpiTile key={kpi.key || kpi.label} kpi={kpi} />
        ))}
      </ul>
    </section>
  )
}

function KpiTile({ kpi }: { kpi: Kpi }) {
  const state = deltaState(kpi)
  const value = typeof kpi.formatted === 'string' && kpi.formatted.length > 0 ? kpi.formatted : null
  return (
    <li className="cv-kpi">
      <div className="cv-kpi__label">{kpi.label}</div>
      {/* The API pre-formats every value. This renders that string and never re-formats it. */}
      <div className="cv-kpi__value">{value ?? <span className="cv-empty">&mdash;</span>}</div>
      <DeltaChip kpi={kpi} state={state} />
      {typeof kpi.note === 'string' && kpi.note.length > 0 ? (
        <div className="cv-kpi__note">{kpi.note}</div>
      ) : null}
    </li>
  )
}

function DeltaChip({ kpi, state }: { kpi: Kpi; state: DeltaState }) {
  const arrow = ARROWS[state]
  const number = state === 'none' ? null : (kpi.deltaFormatted ?? null)
  return (
    <div className="cv-delta" data-delta={state} title={EXPLAIN[state]}>
      {arrow ? (
        <span className="cv-delta__arrow" aria-hidden="true">
          {arrow}
        </span>
      ) : null}
      {number ? <span className="cv-delta__num">{number}</span> : null}
      <span className="cv-delta__word">{WORDS[state]}</span>
      <span className="cv-sr">{SPOKEN[state]}</span>
    </div>
  )
}
