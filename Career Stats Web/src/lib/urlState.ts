import { useMemo, useSyncExternalStore } from 'react'
import type { GameKey } from '../types'

/**
 * The URL is the state.
 *
 * This is a site, not an app that happens to run in a browser: a result you cannot link to
 * is not a result. `?game=halo&q=Elissu` is therefore the only place the current search
 * lives, and every other piece of the UI reads it from here. That buys three things at
 * once and they are all the same thing: deep links work, the back button works, and a
 * pasted link reproduces somebody else's page.
 *
 * The one subtlety: `history.pushState` deliberately does NOT fire `popstate`, so a
 * component subscribed only to `popstate` would miss its own navigations. We publish a
 * private event alongside every write and subscribe to both.
 */

export interface UrlState {
  game: GameKey
  /** The submitted query. Trimmed, and empty when nothing has been searched yet. */
  query: string
}

/** Halo first, because it is the tracker with the fixture player people are sent to try. */
export const DEFAULT_GAME: GameKey = 'halo'

const URL_EVENT = 'eet:urlchange'

export function isGameKey(value: string | null): value is GameKey {
  return value === 'halo' || value === 'destiny'
}

/**
 * Read a query string. Unknown or missing values fall back rather than throwing: a link
 * someone has hand-edited should still land on a working page.
 */
export function parseUrlState(search: string): UrlState {
  const params = new URLSearchParams(search)
  const game = params.get('game')
  return {
    game: isGameKey(game) ? game : DEFAULT_GAME,
    query: (params.get('q') ?? '').trim(),
  }
}

/**
 * The inverse. `game` is always written, even at its default, so that a copied link keeps
 * meaning what it meant if the default ever changes; `q` is omitted when empty because
 * `?q=` in a shared link looks like a broken search rather than an unstarted one.
 */
export function toSearch(state: UrlState): string {
  const params = new URLSearchParams()
  params.set('game', state.game)
  if (state.query) params.set('q', state.query)
  return `?${params.toString()}`
}

/**
 * Write the state to the address bar.
 *
 * `push` for anything a person would expect the back button to undo -- a search, or a game
 * change that re-runs one. `replace` for corrections that were never a destination, such
 * as switching game before any search has happened.
 */
export function navigate(next: UrlState, mode: 'push' | 'replace'): void {
  const url = `${window.location.pathname}${toSearch(next)}`
  if (mode === 'push') window.history.pushState(null, '', url)
  else window.history.replaceState(null, '', url)
  window.dispatchEvent(new Event(URL_EVENT))
}

function subscribe(onChange: () => void): () => void {
  window.addEventListener('popstate', onChange)
  window.addEventListener(URL_EVENT, onChange)
  return () => {
    window.removeEventListener('popstate', onChange)
    window.removeEventListener(URL_EVENT, onChange)
  }
}

// A string, not an object: useSyncExternalStore compares snapshots by identity, and a
// freshly parsed object would differ on every read and loop forever.
function getSearch(): string {
  return window.location.search
}

function getServerSearch(): string {
  return ''
}

/** The current URL state, re-rendering on `navigate` and on the back and forward buttons. */
export function useUrlState(): UrlState {
  const search = useSyncExternalStore(subscribe, getSearch, getServerSearch)
  return useMemo(() => parseUrlState(search), [search])
}
