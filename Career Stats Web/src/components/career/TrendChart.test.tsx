import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { TrendSeries } from '../../types'
import { TrendChart, buildTrendModel } from './TrendChart'
import { findBadNumbers, findOutOfBounds, findTrend, loadSnapshot } from './fixtures'

const snapshot = loadSnapshot()
const kd = findTrend(snapshot, 'kd')
const winRate = findTrend(snapshot, 'winRate')

function radii(container: HTMLElement): Array<{ samples: number; r: number }> {
  return Array.from(container.querySelectorAll('[data-testid="trend-point"]')).map((dot) => ({
    samples: Number(dot.getAttribute('data-samples')),
    r: Number(dot.getAttribute('r')),
  }))
}

describe('sample weighting', () => {
  it('sizes each daily point by how many matches produced it', () => {
    const { container } = render(<TrendChart series={kd} />)
    const marks = radii(container)
    expect(marks.length).toBe(kd.points.length)

    const bySamples = [...marks].sort((a, b) => a.samples - b.samples)
    const smallest = bySamples[0]
    const largest = bySamples[bySamples.length - 1]
    if (!smallest || !largest) throw new Error('Expected at least two points.')

    expect(largest.samples).toBeGreaterThan(smallest.samples)
    // The whole point of the chart: a seven-match day is visibly bigger than a one-match day.
    expect(largest.r).toBeGreaterThan(smallest.r * 1.4)

    // Monotone: more matches never draws smaller.
    for (let i = 1; i < bySamples.length; i += 1) {
      const previous = bySamples[i - 1]
      const current = bySamples[i]
      if (!previous || !current) continue
      if (current.samples > previous.samples) expect(current.r).toBeGreaterThan(previous.r)
      else expect(current.r).toBeCloseTo(previous.r, 6)
    }

    // ...and at least three distinct sizes are actually on screen.
    expect(new Set(marks.map((mark) => mark.r)).size).toBeGreaterThanOrEqual(3)
  })

  it('draws a thin-evidence day hollow as well as small, so colour is not the only channel', () => {
    const { container } = render(<TrendChart series={kd} />)
    const dots = Array.from(container.querySelectorAll('[data-testid="trend-point"]'))
    const thin = dots.filter((dot) => Number(dot.getAttribute('data-samples')) < 3)
    const solid = dots.filter((dot) => Number(dot.getAttribute('data-samples')) >= 3)
    expect(thin.length).toBeGreaterThan(0)
    expect(solid.length).toBeGreaterThan(0)
    expect(thin.every((dot) => dot.getAttribute('fill') === 'var(--c-surface-1)')).toBe(true)
    expect(solid.every((dot) => dot.getAttribute('fill') === 'var(--c-series)')).toBe(true)
  })
})

describe('direction', () => {
  it('renders "steady" as steady, however convincing the line looks', () => {
    // winRate's slope is positive in the fixture and the eye reads it as a climb, but the
    // server's significance test came back "steady". The chart must not upgrade it.
    expect(winRate.direction).toBe('steady')
    expect(winRate.slopePerWeek).toBeGreaterThan(0)

    const { container } = render(<TrendChart series={winRate} />)
    const chip = container.querySelector('.cv-dir')
    if (!chip) throw new Error('Expected a direction chip.')
    expect(chip.getAttribute('data-direction')).toBe('steady')
    expect(chip.textContent).toContain('steady')
    expect(container.querySelector('[data-direction="improving"]')).toBeNull()
    expect(container.querySelector('[data-direction="rising"]')).toBeNull()
    expect(container.textContent).not.toMatch(/improving/i)
  })

  it('renders the server word verbatim, including one it has never seen', () => {
    const invented: TrendSeries = { ...winRate, direction: 'wobbling' }
    const { container } = render(<TrendChart series={invented} />)
    const chip = container.querySelector('.cv-dir')
    if (!chip) throw new Error('Expected a direction chip.')
    expect(chip.getAttribute('data-direction')).toBe('wobbling')
    expect(chip.textContent).toContain('wobbling')
  })
})

describe('geometry', () => {
  it('puts no NaN in the SVG and nothing outside the viewBox', () => {
    for (const series of snapshot.trends) {
      const { container, unmount } = render(<TrendChart series={series} />)
      expect(findBadNumbers(container)).toEqual([])
      expect(findOutOfBounds(container)).toEqual([])
      unmount()
    }
  })

  it('survives the degenerate series that would divide by zero', () => {
    const cases: TrendSeries[] = [
      { ...kd, points: [{ date: '2026-06-06', value: 1, samples: 1 }], smoothed: [1] },
      {
        ...kd,
        points: [
          { date: '2026-06-06', value: 2, samples: 4 },
          { date: '2026-06-07', value: 2, samples: 4 },
        ],
        smoothed: [2, 2],
      },
      {
        ...kd,
        points: [
          { date: 'not a date', value: 1, samples: 2 },
          { date: '2026-06-07', value: 3, samples: 2 },
        ],
        smoothed: [],
      },
    ]
    for (const series of cases) {
      const model = buildTrendModel(series)
      for (const mark of model.marks) {
        expect(Number.isFinite(mark.x)).toBe(true)
        expect(Number.isFinite(mark.y)).toBe(true)
        expect(Number.isFinite(mark.r)).toBe(true)
      }
      expect(model.linePath).not.toContain('NaN')

      const { container, unmount } = render(<TrendChart series={series} />)
      expect(findBadNumbers(container)).toEqual([])
      expect(findOutOfBounds(container)).toEqual([])
      unmount()
    }
  })

  it('renders nothing at all rather than an empty frame when there are no points', () => {
    const { container } = render(<TrendChart series={{ ...kd, points: [], smoothed: [] }} />)
    expect(container.querySelector('svg')).toBeNull()
  })
})

describe('reading the values without the chart', () => {
  it('offers a table view carrying every point, its match count and the smoothed value', () => {
    render(<TrendChart series={kd} />)
    const table = screen.getByRole('table')
    const body = table.querySelectorAll('tbody tr')
    expect(body.length).toBe(kd.points.length)

    const first = kd.points[0]
    if (!first) throw new Error('Expected the fixture series to have points.')
    const firstRow = body[0]
    if (!firstRow) throw new Error('Expected a first row.')
    expect(within(firstRow as HTMLElement).getByText(String(first.samples))).toBeInTheDocument()

    // The caption states the direction, again exactly as the server sent it.
    const caption = table.querySelector('caption')
    expect(caption?.textContent).toContain(kd.direction)
  })

  it('answers the keyboard the way it answers a pointer', () => {
    const { container } = render(<TrendChart series={kd} />)
    const plot = container.querySelector('svg')
    if (!plot) throw new Error('Expected a plot.')

    expect(container.querySelector('.cv-tip')).toBeNull()
    fireEvent.focus(plot)
    const tip = container.querySelector('.cv-tip')
    if (!tip) throw new Error('Focus should open the readout.')
    expect(tip.textContent).toMatch(/UTC/)
    expect(tip.textContent).toMatch(/match/)

    const firstReadout = tip.textContent
    fireEvent.keyDown(plot, { key: 'Home' })
    expect(container.querySelector('.cv-tip')?.textContent).not.toBe(firstReadout)

    fireEvent.blur(plot)
    expect(container.querySelector('.cv-tip')).toBeNull()
  })

  it('carries a legend, because two marks share the plot', () => {
    const { container } = render(<TrendChart series={kd} />)
    const legend = container.querySelector('.cv-legend')
    expect(legend?.textContent).toMatch(/matches that day/i)
    expect(legend?.textContent).toMatch(/smoothed/i)
  })
})
