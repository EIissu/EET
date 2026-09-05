import type { JSX } from 'react'
import type { CareerSnapshot } from '../../types'
import { BreakdownPanel } from './BreakdownPanel'
import { CareerStyles } from './CareerStyles'
import { KpiRow } from './KpiRow'
import { FixtureNotice, PlayerHeader } from './PlayerHeader'
import { RecentMatches } from './RecentMatches'
import { TrendChart } from './TrendChart'

/**
 * Everything below the search bar: one player's career, in the order a person reads it.
 *
 *   1. who this is, and whether the numbers are real
 *   2. the headline KPIs, each against its previous window
 *   3. the trends -- daily points sized by evidence, over the server's smoothed line
 *   4. the breakdowns, ranked, with a table for every chart
 *   5. the recent matches
 *
 * Sections that have no data render nothing at all. An empty chart frame or a zero where
 * the API sent nothing would both be inventions, and this view never invents.
 */
export function CareerView({ snapshot }: { snapshot: CareerSnapshot }): JSX.Element {
  const trends = Array.isArray(snapshot.trends) ? snapshot.trends : []
  const breakdowns = Array.isArray(snapshot.breakdowns) ? snapshot.breakdowns : []
  const headline = Array.isArray(snapshot.headline) ? snapshot.headline : []
  const recent = Array.isArray(snapshot.recent) ? snapshot.recent : []
  const source = typeof snapshot.source === 'string' && snapshot.source.length > 0 ? snapshot.source : null

  return (
    <div className="cv" data-game={snapshot.game} data-fixture={snapshot.isFixture ? 'true' : 'false'}>
      <CareerStyles />

      {/* Synthetic data says so before anything else on the page, every time. */}
      {snapshot.isFixture ? <FixtureNotice source={source} /> : null}

      <PlayerHeader snapshot={snapshot} />

      <KpiRow items={headline} />

      {trends.length > 0 ? (
        <section className="cv-section" aria-labelledby="cv-trends-title">
          <div className="cv-section__head">
            <h2 id="cv-trends-title">Trends</h2>
            <span className="cv-section__note">
              Each chart has one y-axis. Direction is significance-tested on the server, not
              guessed from the shape of the line.
            </span>
          </div>
          <div className="cv-charts">
            {trends.map((series, index) => (
              <TrendChart key={series.key || `${series.label}-${index}`} series={series} />
            ))}
          </div>
        </section>
      ) : null}

      {breakdowns.length > 0 ? (
        <section className="cv-section" aria-labelledby="cv-breakdowns-title">
          <div className="cv-section__head">
            <h2 id="cv-breakdowns-title">Breakdowns</h2>
            <span className="cv-section__note">
              Match counts are shown beside every row: a strong number from three games is not
              the same claim as one from thirty.
            </span>
          </div>
          <div className="cv-breakdowns">
            {breakdowns.map((breakdown, index) => (
              <BreakdownPanel
                key={breakdown.key || `${breakdown.label}-${index}`}
                breakdown={breakdown}
              />
            ))}
          </div>
        </section>
      ) : null}

      <RecentMatches matches={recent} />
    </div>
  )
}
