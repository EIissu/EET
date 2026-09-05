import type { MatchSummary } from '../../types'
import {
  formatDuration,
  formatFixed,
  formatInt,
  formatPercent,
  formatUtcDateTime,
} from './format'

/**
 * The most recent matches, as a table you can scan.
 *
 * Win and loss are carried by a letter and a word before they are carried by a colour, so
 * the column survives a greyscale print, a colour-blind reader and a mistyped token. Times
 * are UTC and the header says so -- a match time that silently shifts with the reader's
 * time zone is the kind of small lie that makes a tracker untrustworthy.
 *
 * Only `MatchSummary` fields the API actually sent are rendered. A null accuracy is an
 * empty cell, not 0%.
 */

export function RecentMatches({ matches }: { matches: MatchSummary[] }) {
  const rows = Array.isArray(matches) ? matches.filter((match) => match && typeof match.id === 'string') : []
  if (rows.length === 0) return null

  return (
    <section className="cv-section" aria-labelledby="cv-recent-title">
      <div className="cv-section__head">
        <h2 id="cv-recent-title">Recent matches</h2>
        <span className="cv-section__note">
          {rows.length} most recent &middot; all times UTC
        </span>
      </div>
      <div className="cv-card" style={{ padding: 0, overflow: 'hidden' }}>
        <div className="cv-scroll">
          <table className="cv-table">
            <caption style={{ padding: '10px 12px 6px' }}>
              Newest first. Times are UTC, durations are wall-clock length.
            </caption>
            <thead>
              <tr>
                <th scope="col">Result</th>
                <th scope="col">Played (UTC)</th>
                <th scope="col">Mode</th>
                <th scope="col">Map</th>
                <th scope="col">Playlist</th>
                <th scope="col" className="cv-num">
                  K
                </th>
                <th scope="col" className="cv-num">
                  D
                </th>
                <th scope="col" className="cv-num">
                  A
                </th>
                <th scope="col" className="cv-num">
                  K/D
                </th>
                <th scope="col" className="cv-num">
                  KDA
                </th>
                <th scope="col" className="cv-num">
                  Accuracy
                </th>
                <th scope="col" className="cv-num">
                  Score
                </th>
                <th scope="col" className="cv-num">
                  Length
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((match) => (
                <tr key={match.id}>
                  <td>
                    <Result won={match.won} />
                  </td>
                  <th scope="row" style={{ fontWeight: 500 }}>
                    {formatUtcDateTime(match.playedAt) ?? <Empty />}
                  </th>
                  <td>{text(match.mode)}</td>
                  <td>{text(match.map)}</td>
                  <td>{text(match.playlist)}</td>
                  <td className="cv-num">{formatInt(match.kills) ?? <Empty />}</td>
                  <td className="cv-num">{formatInt(match.deaths) ?? <Empty />}</td>
                  <td className="cv-num">{formatInt(match.assists) ?? <Empty />}</td>
                  <td className="cv-num">{formatFixed(match.kd, 2) ?? <Empty />}</td>
                  <td className="cv-num">{formatFixed(match.kda, 2) ?? <Empty />}</td>
                  <td className="cv-num">{formatPercent(match.accuracy) ?? <Empty />}</td>
                  <td className="cv-num">{formatInt(match.score) ?? <Empty />}</td>
                  <td className="cv-num">{formatDuration(match.duration) ?? <Empty />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  )
}

function Result({ won }: { won: boolean | null | undefined }) {
  if (won === true) {
    return (
      <span className="cv-result" data-result="win">
        <span className="cv-result__chip" aria-hidden="true">
          W
        </span>
        Win
      </span>
    )
  }
  if (won === false) {
    return (
      <span className="cv-result" data-result="loss">
        <span className="cv-result__chip" aria-hidden="true">
          L
        </span>
        Loss
      </span>
    )
  }
  return (
    <span className="cv-result" data-result="unknown">
      <span className="cv-result__chip" aria-hidden="true">
        ?
      </span>
      Not recorded
    </span>
  )
}

function Empty() {
  return <span className="cv-empty">&mdash;</span>
}

function text(value: string | null | undefined) {
  return typeof value === 'string' && value.trim().length > 0 ? value : <Empty />
}
