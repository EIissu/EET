import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { CareerSnapshot } from '../../types'
import { CareerView } from './CareerView'
import { findBadNumbers, findOutOfBounds, loadSnapshot } from './fixtures'

const halo = loadSnapshot()
const destiny = loadSnapshot('destiny-career.json')

/** The recent-matches section, so a query cannot wander into a chart's table view. */
function recentSection(root: ParentNode): HTMLElement {
  const section = root.querySelector('section[aria-labelledby="cv-recent-title"]')
  if (!(section instanceof HTMLElement)) throw new Error('Expected a recent-matches section.')
  return section
}

describe('CareerView, on the real fixture snapshot', () => {
  it('says the data is synthetic, unmissably', () => {
    const { container } = render(<CareerView snapshot={halo} />)
    expect(halo.isFixture).toBe(true)

    const notice = container.querySelector('.cv-fixture')
    if (!notice) throw new Error('A fixture snapshot must announce itself.')
    expect(notice.getAttribute('role')).toBe('alert')
    expect(notice.textContent).toMatch(/synthetic/i)
    expect(notice.textContent).toMatch(/not anybody’s real career/i)
    // and again beside the player, where the eye actually lands
    expect(screen.getByText('Sample data')).toBeInTheDocument()
  })

  it('drops the notice when the numbers are real', () => {
    const live: CareerSnapshot = { ...halo, isFixture: false }
    const { container } = render(<CareerView snapshot={live} />)
    expect(container.querySelector('.cv-fixture')).toBeNull()
    expect(screen.queryByText('Sample data')).toBeNull()
  })

  it('renders the player as spelled, Cyrillic first letter and all', () => {
    render(<CareerView snapshot={halo} />)
    expect(screen.getByRole('heading', { name: halo.player.handle })).toBeInTheDocument()
    expect(screen.getByText(halo.player.id)).toBeInTheDocument()
    expect(screen.getByText('Xbox')).toBeInTheDocument()
  })

  it('shows the career totals, formatted once and invariantly', () => {
    render(<CareerView snapshot={halo} />)
    expect(screen.getByText('121')).toBeInTheDocument() // matches
    expect(screen.getByText('67–54')).toBeInTheDocument() // record
    expect(screen.getByText('55.4%')).toBeInTheDocument() // win rate, a fraction on the wire
    expect(screen.getAllByText('1,577').length).toBeGreaterThan(0) // kills, grouped
    expect(screen.getAllByText('23h 38m').length).toBeGreaterThan(0) // 85,110 seconds
  })

  it('surfaces the API warnings rather than swallowing them', () => {
    render(<CareerView snapshot={halo} />)
    const first = halo.warnings[0]
    if (!first) throw new Error('The fixture is expected to carry warnings.')
    expect(screen.getByText(first)).toBeInTheDocument()
  })

  it('draws one chart per trend series and one panel per breakdown', () => {
    const { container } = render(<CareerView snapshot={halo} />)
    const charts = container.querySelectorAll('.cv-charts figure')
    const panels = container.querySelectorAll('.cv-breakdowns figure')
    expect(charts.length).toBe(halo.trends.length)
    expect(panels.length).toBe(halo.breakdowns.length)
  })

  it('makes win and loss readable without colour', () => {
    const { container } = render(<CareerView snapshot={halo} />)
    const rows = recentSection(container).querySelectorAll('tbody tr')
    expect(rows.length).toBe(halo.recent.length)

    const wins = container.querySelectorAll('[data-result="win"]')
    const losses = container.querySelectorAll('[data-result="loss"]')
    expect(wins.length + losses.length).toBeGreaterThan(0)
    const firstWin = wins[0]
    if (!firstWin) throw new Error('The fixture has wins in it.')
    expect(firstWin.textContent).toContain('W')
    expect(firstWin.textContent).toContain('Win')
  })

  it('labels match times as UTC and never in the reader’s time zone', () => {
    render(<CareerView snapshot={halo} />)
    expect(screen.getByText('Played (UTC)')).toBeInTheDocument()
    // 2026-09-02T20:10:09Z, rendered without a local-time shift.
    const table = recentSection(document.body).querySelector('table')
    if (!table) throw new Error('Expected the recent-matches table.')
    expect(within(table).getAllByText(/2 Sep 2026 20:10/).length).toBeGreaterThan(0)
  })

  it('leaves a missing number blank instead of inventing a zero', () => {
    const gaps: CareerSnapshot = {
      ...halo,
      recent: [
        {
          ...(halo.recent[0] ?? {
            id: 'x',
            game: 'HaloInfinite',
            playedAt: '2026-09-02T20:10:09Z',
            duration: 600,
            mode: 'Slayer',
            map: 'Live Fire',
            playlist: null,
            won: null,
            kills: 1,
            deaths: 1,
            assists: 1,
            accuracy: null,
            score: null,
            kda: null,
            kd: 1,
          }),
          accuracy: null,
          score: null,
          playlist: null,
          won: null,
        },
      ],
    }
    const { container } = render(<CareerView snapshot={gaps} />)
    const row = recentSection(container).querySelector('tbody tr')
    if (!row) throw new Error('Expected a match row.')
    expect(row.textContent).not.toMatch(/0%/)
    expect(row.textContent).toContain('Not recorded')
    expect(row.querySelectorAll('.cv-empty').length).toBeGreaterThanOrEqual(3)
  })

  it('keeps every SVG finite and inside its own viewBox, for both games', () => {
    for (const snapshot of [halo, destiny]) {
      const { container, unmount } = render(<CareerView snapshot={snapshot} />)
      expect(container.querySelectorAll('svg').length).toBeGreaterThan(0)
      expect(findBadNumbers(container)).toEqual([])
      expect(findOutOfBounds(container)).toEqual([])
      unmount()
    }
  })

  it('renders Destiny, whose durations arrive as TimeSpan strings rather than seconds', () => {
    const { container } = render(<CareerView snapshot={destiny} />)
    // "19:27:56" of lifetime play, and "00:08:09" on the newest match.
    expect(screen.getAllByText('19h 27m').length).toBeGreaterThan(0)
    expect(within(container).getAllByText('8m 9s').length).toBeGreaterThan(0)
  })

  it('gives every chart a table view of the same numbers', () => {
    const { container } = render(<CareerView snapshot={halo} />)
    const figures = container.querySelectorAll('figure')
    expect(figures.length).toBe(halo.trends.length + halo.breakdowns.length)
    for (const figure of Array.from(figures)) {
      expect(figure.querySelector('details table')).not.toBeNull()
    }
  })

  it('survives a snapshot with nothing in it', () => {
    const bare: CareerSnapshot = {
      ...halo,
      headline: [],
      trends: [],
      breakdowns: [],
      recent: [],
      warnings: [],
    }
    const { container } = render(<CareerView snapshot={bare} />)
    expect(container.querySelector('.cv-handle')?.textContent).toBe(halo.player.handle)
    expect(container.querySelectorAll('figure').length).toBe(0)
    expect(container.querySelectorAll('table').length).toBe(0)
  })
})
