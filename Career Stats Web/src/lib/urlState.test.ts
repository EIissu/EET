import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { navigate, parseUrlState, toSearch, useUrlState } from './urlState'

afterEach(() => {
  window.history.replaceState(null, '', '/')
})

describe('parseUrlState', () => {
  it('reads game and query', () => {
    expect(parseUrlState('?game=destiny&q=AnaGuardian%234412')).toEqual({
      game: 'destiny',
      query: 'AnaGuardian#4412',
    })
  })

  it('defaults to halo with no query', () => {
    expect(parseUrlState('')).toEqual({ game: 'halo', query: '' })
  })

  it('falls back rather than throwing on a game nobody has heard of', () => {
    expect(parseUrlState('?game=pong&q=Elissu')).toEqual({ game: 'halo', query: 'Elissu' })
  })

  it('trims a query padded by a copy-paste', () => {
    expect(parseUrlState('?q=%20Elissu%20').query).toBe('Elissu')
  })
})

describe('toSearch', () => {
  it('round-trips every state it can produce', () => {
    const states = [
      { game: 'halo', query: 'Elissu' },
      { game: 'destiny', query: 'AnaGuardian#4412' },
      { game: 'halo', query: 'xuid(2814669301245176)' },
      { game: 'destiny', query: '' },
    ] as const

    for (const state of states) {
      expect(parseUrlState(toSearch(state))).toEqual(state)
    }
  })

  it('keeps the game even at its default, so a copied link keeps meaning what it meant', () => {
    expect(toSearch({ game: 'halo', query: 'Elissu' })).toBe('?game=halo&q=Elissu')
  })

  it('omits an empty q rather than writing a broken-looking search', () => {
    expect(toSearch({ game: 'destiny', query: '' })).toBe('?game=destiny')
  })
})

describe('useUrlState', () => {
  it('reads the URL the page was opened on, so deep links work', () => {
    window.history.replaceState(null, '', '/?game=destiny&q=Guardian%231234')
    const { result } = renderHook(() => useUrlState())
    expect(result.current).toEqual({ game: 'destiny', query: 'Guardian#1234' })
  })

  it('re-renders when navigate writes a new state', () => {
    const { result } = renderHook(() => useUrlState())
    expect(result.current.query).toBe('')

    act(() => navigate({ game: 'halo', query: 'Elissu' }, 'push'))

    expect(result.current).toEqual({ game: 'halo', query: 'Elissu' })
    expect(window.location.search).toBe('?game=halo&q=Elissu')
  })

  it('follows the back button', async () => {
    const { result } = renderHook(() => useUrlState())

    act(() => navigate({ game: 'halo', query: 'Elissu' }, 'push'))
    act(() => navigate({ game: 'destiny', query: 'Elissu' }, 'push'))
    expect(result.current.game).toBe('destiny')

    window.history.back()

    // jsdom dispatches popstate on a later task than the call that queues it, and the
    // number of entries already on the stack is not this test's business.
    await waitFor(() => {
      expect(result.current).toEqual({ game: 'halo', query: 'Elissu' })
    })
  })

  it('push adds a history entry, replace does not', async () => {
    const { result } = renderHook(() => useUrlState())

    // Anchor on an entry this test owns. Pushing also drops any forward entries a previous
    // test left behind, which is what makes the count below mean anything.
    act(() => navigate({ game: 'halo', query: 'anchor' }, 'push'))
    const before = window.history.length

    act(() => navigate({ game: 'halo', query: 'Elissu' }, 'push'))
    expect(window.history.length).toBe(before + 1)

    // A game change with nothing searched yet was never a destination, so it overwrites
    // rather than stacking up entries for the back button to grind through.
    act(() => navigate({ game: 'destiny', query: 'Elissu' }, 'replace'))
    expect(window.history.length).toBe(before + 1)
    expect(window.location.search).toBe('?game=destiny&q=Elissu')

    // One press of back reaches the anchor: the replace left nothing in between.
    window.history.back()
    await waitFor(() => expect(result.current.query).toBe('anchor'))
  })
})
