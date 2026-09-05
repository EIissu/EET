import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { Kpi } from '../../types'
import { KpiRow, deltaState } from './KpiRow'
import { findKpi, loadSnapshot } from './fixtures'

const snapshot = loadSnapshot()

function renderRow(items: Kpi[]) {
  return render(<KpiRow items={items} />)
}

describe('KPI deltas', () => {
  it('renders a null delta differently from a zero delta', () => {
    // Both live in the real fixture: `timePlayed` has no delta field at all, and
    // `assistsPerMatch` has a delta of exactly 0.
    const noBaseline = findKpi(snapshot, 'timePlayed')
    const unchanged = findKpi(snapshot, 'assistsPerMatch')
    expect(noBaseline.delta ?? null).toBeNull()
    expect(unchanged.delta).toBe(0)

    const { container } = renderRow([noBaseline, unchanged])
    const none = container.querySelector('[data-delta="none"]')
    const zero = container.querySelector('[data-delta="zero"]')
    if (!none || !zero) throw new Error('Expected one "none" chip and one "zero" chip.')

    expect(none.textContent).not.toBe(zero.textContent)
    expect(none.textContent).toMatch(/No prior window/i)
    expect(zero.textContent).toMatch(/no change/i)
    // "no baseline" must not be spoken as "no change", nor the other way round.
    expect(none.textContent).not.toMatch(/no change/i)
    expect(zero.textContent).not.toMatch(/No prior window/i)
  })

  it('never paints a zero delta as an improvement or a decline', () => {
    // The fixture ships assists/match as delta 0 with improved:false. Zero is zero.
    const unchanged = findKpi(snapshot, 'assistsPerMatch')
    expect(unchanged.improved).toBe(false)
    expect(deltaState(unchanged)).toBe('zero')

    const { container } = renderRow([unchanged])
    expect(container.querySelector('[data-delta="bad"]')).toBeNull()
    expect(container.querySelector('[data-delta="good"]')).toBeNull()
  })

  it('uses `improved` for direction, not the sign of the number', () => {
    // Deaths per match: the number ROSE (+0.92) and that is bad news, so the chip is "worse"
    // even though the figure is positive.
    const deaths = findKpi(snapshot, 'deathsPerMatch')
    expect(deaths.better).toBe('Lower')
    expect(deltaState(deaths)).toBe('bad')

    const { container } = renderRow([deaths])
    const chip = container.querySelector('[data-delta="bad"]')
    if (!chip) throw new Error('Expected a "bad" chip for a KPI that moved against the player.')
    expect(chip.textContent).toContain('+0.92')
    expect(chip.textContent).toMatch(/worse/i)
  })

  it('renders the pre-formatted value, not a re-formatted one', () => {
    renderRow(snapshot.headline)
    // 23.6417 hours arrives as "23h 38m" and 0.4493 as "44.9%": both rendered verbatim.
    expect(screen.getByText('23h 38m')).toBeInTheDocument()
    expect(screen.getByText('44.9%')).toBeInTheDocument()
    expect(screen.queryByText('23.6417')).toBeNull()
  })

  it('classifies a missing delta as "none" even when the field is absent rather than null', () => {
    const absent: Kpi = {
      key: 'x',
      label: 'X',
      value: 1,
      formatted: '1',
      better: 'Higher',
      delta: null,
      deltaFormatted: null,
      note: null,
      improved: null,
    }
    expect(deltaState(absent)).toBe('none')
    expect(deltaState({ ...absent, delta: 0, deltaFormatted: '0.00' })).toBe('zero')
    expect(deltaState({ ...absent, delta: 0.5, deltaFormatted: '+0.50', improved: true })).toBe('good')
    expect(deltaState({ ...absent, delta: 0.5, deltaFormatted: '+0.50', improved: null })).toBe('neutral')
  })
})
