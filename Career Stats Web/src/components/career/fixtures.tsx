import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import type { CareerSnapshot, Kpi, TrendSeries } from '../../types'

/**
 * Test scaffolding: the real fixture JSON, and two geometry auditors.
 *
 * The snapshot is read from `Career Stats Shared/web/mock/halo-career.json` rather than
 * hand-written here, so the tests exercise the shape the services actually emit -- a
 * gamertag whose first letter is Cyrillic, a KPI with no delta at all, a KPI whose delta is
 * exactly zero, four trend series of which one is "steady" despite a rising line, and days
 * ranging from one match to seven.
 */

const HERE = path.dirname(fileURLToPath(import.meta.url))
const MOCK_DIR = path.resolve(HERE, '../../../../Career Stats Shared/web/mock')

export function loadSnapshot(file = 'halo-career.json'): CareerSnapshot {
  const text = readFileSync(path.join(MOCK_DIR, file), 'utf8')
  return JSON.parse(text) as CareerSnapshot
}

export function findKpi(snapshot: CareerSnapshot, key: string): Kpi {
  const found = snapshot.headline.find((kpi) => kpi.key === key)
  if (!found) throw new Error(`The fixture has no "${key}" KPI; the tests depend on it.`)
  return found
}

export function findTrend(snapshot: CareerSnapshot, key: string): TrendSeries {
  const found = snapshot.trends.find((series) => series.key === key)
  if (!found) throw new Error(`The fixture has no "${key}" trend; the tests depend on it.`)
  return found
}

/** Any attribute anywhere containing NaN, Infinity or an empty coordinate. */
export function findBadNumbers(root: ParentNode): string[] {
  const bad: string[] = []
  for (const element of Array.from(root.querySelectorAll('*'))) {
    for (const attribute of Array.from(element.attributes)) {
      const value = attribute.value
      if (/NaN|Infinity/.test(value) || value === 'undefined' || value === 'null') {
        bad.push(`<${element.tagName} ${attribute.name}="${attribute.value}">`)
      }
    }
  }
  return bad
}

interface Box {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

function readViewBox(svg: Element): Box | null {
  const raw = svg.getAttribute('viewBox')
  if (!raw) return null
  const parts = raw.trim().split(/[\s,]+/).map(Number)
  const [x, y, w, h] = parts
  if (x === undefined || y === undefined || w === undefined || h === undefined) return null
  if (![x, y, w, h].every((n) => Number.isFinite(n))) return null
  return { minX: x, minY: y, maxX: x + w, maxY: y + h }
}

function num(element: Element, name: string): number | null {
  const raw = element.getAttribute(name)
  if (raw === null) return null
  const value = Number(raw)
  return Number.isFinite(value) ? value : Number.NaN
}

/** Every point of a `d` this component set could have written: M, L, H, V, Q, Z. */
function pathPoints(d: string): Array<[number, number]> {
  const points: Array<[number, number]> = []
  const tokens = d.match(/[MLHVQZ]|-?\d+(?:\.\d+)?/gi) ?? []
  let command = ''
  let x = 0
  let y = 0
  let index = 0
  const next = (): number => {
    const token = tokens[index]
    index += 1
    return token === undefined ? Number.NaN : Number(token)
  }
  while (index < tokens.length) {
    const token = tokens[index]
    if (token === undefined) break
    if (/[MLHVQZ]/i.test(token)) {
      command = token.toUpperCase()
      index += 1
      if (command === 'Z') continue
    }
    if (command === 'M' || command === 'L') {
      x = next()
      y = next()
    } else if (command === 'H') {
      x = next()
    } else if (command === 'V') {
      y = next()
    } else if (command === 'Q') {
      const cx = next()
      const cy = next()
      points.push([cx, cy])
      x = next()
      y = next()
    } else {
      index += 1
      continue
    }
    points.push([x, y])
  }
  return points
}

/**
 * Anything drawn outside its own viewBox. Marker radii count: a dot whose centre is in the
 * box but whose edge is not is still clipped on screen.
 */
export function findOutOfBounds(root: ParentNode): string[] {
  const escapes: string[] = []
  for (const svg of Array.from(root.querySelectorAll('svg'))) {
    const box = readViewBox(svg)
    if (!box) {
      escapes.push('<svg> without a usable viewBox')
      continue
    }
    const check = (label: string, px: number, py: number) => {
      if (!Number.isFinite(px) || !Number.isFinite(py)) {
        escapes.push(`${label} is not finite (${px}, ${py})`)
        return
      }
      if (px < box.minX - 0.5 || px > box.maxX + 0.5 || py < box.minY - 0.5 || py > box.maxY + 0.5) {
        escapes.push(`${label} at (${px}, ${py}) is outside ${box.minX} ${box.minY} ${box.maxX} ${box.maxY}`)
      }
    }
    for (const circle of Array.from(svg.querySelectorAll('circle'))) {
      const cx = num(circle, 'cx') ?? 0
      const cy = num(circle, 'cy') ?? 0
      const r = num(circle, 'r') ?? 0
      check('circle', cx - r, cy - r)
      check('circle', cx + r, cy + r)
    }
    for (const line of Array.from(svg.querySelectorAll('line'))) {
      check('line start', num(line, 'x1') ?? 0, num(line, 'y1') ?? 0)
      check('line end', num(line, 'x2') ?? 0, num(line, 'y2') ?? 0)
    }
    for (const rect of Array.from(svg.querySelectorAll('rect'))) {
      const x = num(rect, 'x') ?? 0
      const y = num(rect, 'y') ?? 0
      check('rect origin', x, y)
      check('rect corner', x + (num(rect, 'width') ?? 0), y + (num(rect, 'height') ?? 0))
    }
    for (const text of Array.from(svg.querySelectorAll('text'))) {
      check('text anchor', num(text, 'x') ?? 0, num(text, 'y') ?? 0)
    }
    for (const element of Array.from(svg.querySelectorAll('path'))) {
      const d = element.getAttribute('d') ?? ''
      for (const [px, py] of pathPoints(d)) check('path point', px, py)
    }
  }
  return escapes
}
