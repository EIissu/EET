import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useCareer } from './useCareer'
import type { CareerSnapshot, GameKey } from '../types'

/**
 * A fetch that hands control of every response back to the test.
 *
 * Nothing here touches the network. Requests pile up in `pending` and the test decides
 * what comes back and, crucially, in what order -- which is the only way to prove that a
 * slow first search cannot overwrite a fast second one.
 */
interface Pending {
  url: string
  signal: AbortSignal | null
  resolve: (value: Response) => void
  reject: (reason: unknown) => void
}

let pending: Pending[] = []
/**
 * A real aborted fetch rejects. Turning that off lets a test simulate the harder case: a
 * response that had already arrived before the abort landed, which the AbortSignal alone
 * cannot save us from.
 */
let rejectOnAbort = true

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** Resolve the oldest outstanding request whose URL contains `match`. */
function answer(match: string, body: unknown, status = 200): void {
  const index = pending.findIndex((entry) => entry.url.includes(match))
  if (index === -1) throw new Error(`No pending request matching "${match}".`)
  const [entry] = pending.splice(index, 1)
  if (!entry) throw new Error(`No pending request matching "${match}".`)
  entry.resolve(jsonResponse(body, status))
}

function waitForRequest(match: string): Promise<Pending> {
  return waitFor(() => {
    const entry = pending.find((item) => item.url.includes(match))
    if (!entry) throw new Error(`Still no request matching "${match}".`)
    return entry
  })
}

beforeEach(() => {
  pending = []
  rejectOnAbort = true
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const signal = init?.signal ?? null
      return new Promise<Response>((resolve, reject) => {
        pending.push({ url, signal, resolve, reject })
        signal?.addEventListener('abort', () => {
          if (rejectOnAbort) reject(new DOMException('The operation was aborted.', 'AbortError'))
        })
      })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
})

const PLAYER = {
  handle: 'Еlissu',
  id: '2814669301245176',
  platform: 'Xbox',
  iconUrl: null,
}

function snapshot(handle: string): CareerSnapshot {
  return {
    player: { handle, id: '1', platform: 'Xbox', iconUrl: null },
    game: 'HaloInfinite',
    generatedAt: '2026-09-05T00:00:00+00:00',
    isFixture: true,
    source: 'fixtures',
    headline: [],
    trends: [],
    recent: [],
    breakdowns: [],
    totals: {
      matches: 0,
      wins: 0,
      losses: 0,
      timePlayed: 0,
      kills: 0,
      deaths: 0,
      assists: 0,
      winRate: 0,
      kd: 0,
    },
    warnings: [],
  }
}

describe('useCareer', () => {
  it('does nothing at all until there is something to search for', () => {
    const { result } = renderHook(() => useCareer('halo', ''))
    expect(result.current.status).toBe('idle')
    expect(pending).toHaveLength(0)
  })

  it('resolves a player and then loads their career', async () => {
    const { result } = renderHook(() => useCareer('halo', 'Elissu'))
    expect(result.current.status).toBe('loading')

    const search = await waitForRequest('/api/halo/player')
    expect(search.url).toContain('q=Elissu')
    answer('/api/halo/player', { player: PLAYER, homoglyphNotice: null })

    const career = await waitForRequest('/api/halo/career')
    // The XUID from the envelope's inner player, not the envelope itself.
    expect(career.url).toContain('xuid(2814669301245176)')
    answer('/api/halo/career', snapshot('Еlissu'))

    await waitFor(() => expect(result.current.status).toBe('ready'))
    if (result.current.status !== 'ready') throw new Error('unreachable')
    expect(result.current.snapshot.player.handle).toBe('Еlissu')
  })

  it("surfaces Halo's homoglyph explanation when the handle is not what was typed", async () => {
    const { result } = renderHook(() => useCareer('halo', 'Elissu'))

    await waitForRequest('/api/halo/player')
    answer('/api/halo/player', {
      player: PLAYER,
      homoglyphNotice: '"Еlissu" renders like "Elissu" but is not the same text.',
    })
    await waitForRequest('/api/halo/career')
    answer('/api/halo/career', snapshot('Еlissu'))

    await waitFor(() => expect(result.current.status).toBe('ready'))
    if (result.current.status !== 'ready') throw new Error('unreachable')
    expect(result.current.notice).toContain('not the same text')
  })

  it('accepts a bare player, which is what Destiny returns', async () => {
    const player = { handle: 'AnaGuardian#4412', id: '4611686018400119004', platform: 'Steam', iconUrl: null }
    const { result } = renderHook(() => useCareer('destiny', 'AnaGuardian#4412'))

    await waitForRequest('/api/destiny/player')
    answer('/api/destiny/player', player)

    const career = await waitForRequest('/api/destiny/career')
    expect(career.url).toContain('q=4611686018400119004')
    answer('/api/destiny/career', snapshot('AnaGuardian#4412'))

    await waitFor(() => expect(result.current.status).toBe('ready'))
    if (result.current.status !== 'ready') throw new Error('unreachable')
    expect(result.current.notice).toBeNull()
  })

  it('aborts the in-flight request when the query changes', async () => {
    const { result, rerender } = renderHook(
      ({ query }: { query: string }) => useCareer('halo', query),
      { initialProps: { query: 'Eli' } },
    )

    const first = await waitForRequest('q=Eli')
    expect(first.signal?.aborted).toBe(false)

    rerender({ query: 'Elissu' })

    expect(first.signal?.aborted).toBe(true)
    // Aborting is not a failure -- it is this search being superseded.
    expect(result.current.status).toBe('loading')
    await waitForRequest('q=Elissu')
  })

  it('aborts when the game changes', async () => {
    const { rerender } = renderHook(
      ({ game }: { game: GameKey }) => useCareer(game, 'Elissu'),
      { initialProps: { game: 'halo' as GameKey } },
    )

    const first = await waitForRequest('/api/halo/player')
    rerender({ game: 'destiny' })

    expect(first.signal?.aborted).toBe(true)
    await waitForRequest('/api/destiny/player')
  })

  it('a slow first search cannot overwrite a fast second one', async () => {
    // The response was already on the wire when the abort landed, so the signal will not
    // save us: only the hook's own liveness check will.
    rejectOnAbort = false

    const { result, rerender } = renderHook(
      ({ query }: { query: string }) => useCareer('halo', query),
      { initialProps: { query: 'slow' } },
    )

    const slowSearch = await waitForRequest('q=slow')
    rerender({ query: 'fast' })
    await waitForRequest('q=fast')

    // The second search finishes first, in full.
    answer('q=fast', { player: { ...PLAYER, id: '222' } })
    await waitForRequest('/api/halo/career')
    answer('/api/halo/career', snapshot('FAST'))
    await waitFor(() => expect(result.current.status).toBe('ready'))

    // Now the abandoned first search comes back late.
    slowSearch.resolve(jsonResponse({ player: { ...PLAYER, id: '111' } }))
    await new Promise((resolve) => setTimeout(resolve, 0))

    // It must not even ask for the stale career, let alone render it.
    expect(pending.some((entry) => entry.url.includes('/career'))).toBe(false)
    if (result.current.status !== 'ready') throw new Error('unreachable')
    expect(result.current.snapshot.player.handle).toBe('FAST')
  })

  it('reports the API being down as a reachability problem, not a missing player', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))),
    )

    const { result } = renderHook(() => useCareer('halo', 'Elissu'))

    await waitFor(() => expect(result.current.status).toBe('error'))
    if (result.current.status !== 'error') throw new Error('unreachable')
    expect(result.current.error.status).toBe(0)
  })

  it("keeps the API's own detail on a 404", async () => {
    const { result } = renderHook(() => useCareer('halo', 'zzznotreal'))

    await waitForRequest('/api/halo/player')
    answer(
      '/api/halo/player',
      {
        title: 'No player matches "zzznotreal".',
        status: 404,
        detail: 'A Cyrillic Е looks exactly like a Latin E. Look the player up by XUID instead.',
      },
      404,
    )

    await waitFor(() => expect(result.current.status).toBe('error'))
    if (result.current.status !== 'error') throw new Error('unreachable')
    expect(result.current.error.status).toBe(404)
    expect(result.current.error.detail).toContain('XUID')
  })

  it('re-runs an identical search when the attempt is bumped', async () => {
    const { rerender } = renderHook(
      ({ attempt }: { attempt: number }) => useCareer('halo', 'Elissu', attempt),
      { initialProps: { attempt: 0 } },
    )

    await waitForRequest('q=Elissu')
    answer('q=Elissu', { player: PLAYER })
    await waitForRequest('/api/halo/career')

    rerender({ attempt: 1 })

    await waitFor(() => expect(pending.some((entry) => entry.url.includes('q=Elissu'))).toBe(true))
  })
})
