import { useState } from 'react'
import type { CareerSnapshot, CareerTotals, Player } from '../../types'
import { formatDuration, formatFixed, formatInt, formatPercent, formatUtcDateTime } from './format'

/**
 * Who the numbers belong to, and the lifetime totals behind them.
 *
 * `CareerTotals` is the one block on the snapshot with no `formatted` companions -- it is
 * raw numbers -- so it is formatted here, invariantly, and any field that is missing or
 * non-finite is dropped from the grid rather than shown as a zero.
 */

const GAME_NAMES: Record<string, string> = {
  HaloInfinite: 'Halo Infinite',
  Destiny2: 'Destiny 2',
  halo: 'Halo Infinite',
  destiny: 'Destiny 2',
}

export function gameName(game: string): string {
  return GAME_NAMES[game] ?? game
}

export function FixtureNotice({ source }: { source: string | null }) {
  return (
    <div className="cv-fixture" role="alert">
      <span className="cv-fixture__mark" aria-hidden="true">
        &#9888;
      </span>
      <p className="cv-fixture__title">Synthetic sample data</p>
      <p className="cv-fixture__body">
        Every number below &mdash; totals, deltas, trends, matches &mdash; was invented by a
        fixture. This is not anybody&rsquo;s real career.
      </p>
      {source ? <p className="cv-fixture__source">{source}</p> : null}
    </div>
  )
}

export function PlayerHeader({ snapshot }: { snapshot: CareerSnapshot }) {
  const player: Player | null = snapshot.player ?? null
  const handle = player && typeof player.handle === 'string' && player.handle.length > 0
    ? player.handle
    : 'Unknown player'
  const icon = player && typeof player.iconUrl === 'string' && player.iconUrl.length > 0
    ? player.iconUrl
    : null
  const updated = formatUtcDateTime(snapshot.generatedAt)
  const warnings = Array.isArray(snapshot.warnings) ? snapshot.warnings.filter(isText) : []

  return (
    <section className="cv-card" aria-labelledby="cv-player-handle">
      <div className="cv-identity">
        <div className="cv-identity__who">
          {icon ? <Avatar src={icon} /> : null}
          <div>
            <h2 className="cv-handle" id="cv-player-handle">
              {handle}
            </h2>
            <div className="cv-meta">
              <span>{gameName(snapshot.game)}</span>
              {player && isText(player.platform) ? <span>{player.platform}</span> : null}
              {player && isText(player.id) ? <code>{player.id}</code> : null}
              {snapshot.isFixture ? (
                <span className="cv-badge cv-badge--sample">Sample data</span>
              ) : null}
              {updated ? <span>Updated {updated} UTC</span> : null}
            </div>
          </div>
        </div>
        <TotalsGrid totals={snapshot.totals} />
      </div>
      {warnings.length > 0 ? (
        <ul className="cv-warnings">
          {warnings.map((warning) => (
            <li key={warning}>{warning}</li>
          ))}
        </ul>
      ) : null}
    </section>
  )
}

/**
 * A player icon that fails to load leaves a broken-image glyph beside the handle, which
 * reads as a bug in the tracker rather than a gap at the game's CDN. If it does not load,
 * there is simply no avatar.
 */
function Avatar({ src }: { src: string }) {
  const [failed, setFailed] = useState(false)
  if (failed) return null
  return (
    <span className="cv-avatar">
      <img
        src={src}
        alt=""
        width={56}
        height={56}
        loading="lazy"
        referrerPolicy="no-referrer"
        onError={() => setFailed(true)}
      />
    </span>
  )
}

function TotalsGrid({ totals }: { totals: CareerTotals | null | undefined }) {
  if (!totals) return null
  const record =
    formatInt(totals.wins) !== null && formatInt(totals.losses) !== null
      ? `${formatInt(totals.wins) ?? ''}–${formatInt(totals.losses) ?? ''}`
      : null
  const entries: Array<[string, string | null]> = [
    ['Matches', formatInt(totals.matches)],
    ['Record (W–L)', record],
    ['Win rate', formatPercent(totals.winRate)],
    ['K/D', formatFixed(totals.kd, 2)],
    ['Kills', formatInt(totals.kills)],
    ['Deaths', formatInt(totals.deaths)],
    ['Assists', formatInt(totals.assists)],
    ['Time played', formatDuration(totals.timePlayed)],
  ]
  const shown = entries.filter((entry): entry is [string, string] => entry[1] !== null)
  if (shown.length === 0) return null
  return (
    <dl className="cv-totals">
      {shown.map(([label, value]) => (
        <div key={label}>
          <dt className="cv-total__label">{label}</dt>
          <dd className="cv-total__value" style={{ margin: 0 }}>
            {value}
          </dd>
        </div>
      ))}
    </dl>
  )
}

function isText(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
