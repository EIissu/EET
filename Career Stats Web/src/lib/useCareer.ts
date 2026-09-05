import { useEffect, useState } from 'react'
import { getCareer, getHealth, searchPlayer } from './api'
import type { Health } from './api'
import type { CareerSnapshot, GameKey, Player } from '../types'
import { ApiError } from '../types'

/**
 * Searching, as a hook.
 *
 * Two requests in sequence -- resolve the text to a player, then fetch that player's
 * career -- with one rule that matters more than the rest: a search that is no longer the
 * one being asked for must not be allowed to land. Somebody types "Eli", waits, then types
 * "Elissu"; if the first request is slow and the second is fast, the slow one resolving
 * afterwards would replace the right answer with a stale one. So every run owns an
 * AbortController, the effect cleanup aborts it, and a `live` flag catches the narrow case
 * where a response had already arrived before the abort landed.
 */

/** No HTTP status: a failure that never reached, or never came back from, the network. */
export const NO_STATUS = -1

export type CareerState =
  | { status: 'idle' }
  | { status: 'loading'; query: string }
  | {
      status: 'ready'
      snapshot: CareerSnapshot
      /**
       * Halo's resolver explains itself when the handle it found is not the text that was
       * typed -- the homoglyph case. Null when there is nothing to explain, or when the
       * backend is Destiny, which returns a bare player.
       */
      notice: string | null
    }
  | { status: 'error'; error: ApiError; query: string }

/**
 * @param attempt bump to re-run an identical search. The URL cannot express "try that
 *   again", and after starting the API that failed a moment ago, retrying is exactly what
 *   a person wants to do.
 */
export function useCareer(game: GameKey, query: string, attempt = 0): CareerState {
  const [state, setState] = useState<CareerState>({ status: 'idle' })

  useEffect(() => {
    if (!query) {
      setState({ status: 'idle' })
      return
    }

    const controller = new AbortController()
    let live = true
    setState({ status: 'loading', query })

    void (async () => {
      try {
        const resolved = await searchPlayer(game, query, controller.signal)
        // Checked between the two calls as well as after them. An aborted signal would
        // make the second request fail immediately anyway, but there is no reason to open
        // a connection for an answer nobody is waiting for any more.
        if (!live) return
        const snapshot = await getCareer(game, playerOf(resolved), controller.signal)
        if (!live) return
        setState({ status: 'ready', snapshot, notice: noticeOf(resolved) })
      } catch (cause) {
        // An abort is not a failure, it is this search being superseded. Reporting it
        // would flash an error between two perfectly good results.
        if (!live || controller.signal.aborted) return
        setState({ status: 'error', error: asApiError(cause), query })
      }
    })()

    return () => {
      live = false
      controller.abort()
    }
  }, [game, query, attempt])

  return state
}

/**
 * Whether the selected backend is serving fixtures, asked before anybody searches.
 *
 * This is what lets the synthetic-data warning be on screen from the first paint rather
 * than appearing only once a result arrives. A failure here is deliberately silent: if the
 * API is down, the search will say so properly, and two error messages for one cause is
 * one too many.
 */
export function useHealth(game: GameKey): Health | null {
  const [health, setHealth] = useState<Health | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let live = true
    setHealth(null)

    void (async () => {
      try {
        const result = await getHealth(game, controller.signal)
        if (live) setHealth(result)
      } catch {
        if (live) setHealth(null)
      }
    })()

    return () => {
      live = false
      controller.abort()
    }
  }, [game])

  return health
}

/**
 * The two backends do not agree on the shape of `/api/player`.
 *
 * Destiny returns a bare player. Halo wraps it -- `{ player, typedQuery, homoglyphNotice,
 * handleIsTypeable }` -- because it has something to say about the difference between what
 * was typed and what was found. Taking the wrapper at face value would send
 * `player=xuid(undefined)` to the career endpoint and produce a 404 that blamed the user
 * for a mistake the client made, so unwrap it here, defensively, without assuming which
 * backend answered.
 */
function playerOf(result: Player): Player {
  const value: unknown = result
  if (value && typeof value === 'object' && 'player' in value && isPlayer(value.player)) {
    return value.player
  }
  return result
}

function noticeOf(result: Player): string | null {
  const value: unknown = result
  if (value && typeof value === 'object' && 'homoglyphNotice' in value) {
    const notice = value.homoglyphNotice
    if (typeof notice === 'string' && notice.trim().length > 0) return notice
  }
  return null
}

function isPlayer(value: unknown): value is Player {
  return (
    !!value &&
    typeof value === 'object' &&
    'id' in value &&
    typeof value.id === 'string' &&
    'handle' in value &&
    typeof value.handle === 'string'
  )
}

/** Everything the UI shows a person is an ApiError, so there is one shape to render. */
function asApiError(cause: unknown): ApiError {
  if (cause instanceof ApiError) return cause
  if (cause instanceof Error) return new ApiError(cause.message, NO_STATUS)
  return new ApiError('Something went wrong while loading the career.', NO_STATUS)
}
