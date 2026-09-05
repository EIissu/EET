import type { CareerSnapshot, GameKey, Player, ProblemDetails } from '../types'
import { ApiError } from '../types'

/**
 * Talking to the two backends.
 *
 * Both are mounted under one origin -- `/api/halo/*` and `/api/destiny/*` -- by the Vite
 * proxy in development and by whatever fronts them in production. The app never holds a
 * credential: the Bungie key and the Xbox tokens live on the server, which is the whole
 * reason there is a server at all. A browser cannot reach Halo's endpoints regardless,
 * since they neither send CORS headers nor accept anything but a Spartan token.
 */

const BASE: Record<GameKey, string> = {
  halo: '/api/halo',
  destiny: '/api/destiny',
}

export const GAMES: ReadonlyArray<{ key: GameKey; name: string; searchHint: string }> = [
  { key: 'halo', name: 'Halo Infinite', searchHint: 'gamertag, or xuid(...)' },
  { key: 'destiny', name: 'Destiny 2', searchHint: 'Bungie name, e.g. Guardian#1234' },
]

/**
 * One request, with the two failure modes a person can actually act on separated from the
 * ones they cannot: a structured problem document from the API, and everything else.
 */
async function request<T>(url: string, signal?: AbortSignal): Promise<T> {
  let response: Response
  try {
    response = await fetch(url, {
      signal: signal ?? null,
      headers: { Accept: 'application/json' },
    })
  } catch (cause) {
    // A network-level failure. In development this is almost always the backend not
    // running, which is worth saying rather than making somebody open devtools.
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    throw new ApiError(
      'Could not reach the tracker service.',
      0,
      'Is the API running? Start it with: dotnet run --project "Halo Career Stats/src/Halo.Api"',
    )
  }

  if (!response.ok) {
    const problem = await readProblem(response)
    throw new ApiError(
      problem?.title ?? `The service answered ${response.status}.`,
      response.status,
      problem?.detail ?? undefined,
    )
  }

  return (await response.json()) as T
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    const body = (await response.json()) as ProblemDetails
    return body && typeof body === 'object' ? body : null
  } catch {
    // A non-JSON error body is not worth surfacing verbatim; the status carries the meaning.
    return null
  }
}

/** Resolve free text -- a gamertag, an XUID, a Bungie name -- to a player. */
export function searchPlayer(
  game: GameKey,
  query: string,
  signal?: AbortSignal,
): Promise<Player> {
  return request<Player>(`${BASE[game]}/player?q=${encodeURIComponent(query)}`, signal)
}

/** The full career snapshot for a resolved player. */
export function getCareer(
  game: GameKey,
  player: Player,
  signal?: AbortSignal,
): Promise<CareerSnapshot> {
  // Destiny's career endpoint takes `membershipType` + `membershipId`, but it also accepts
  // a bare `q`, and a Destiny membership id resolves on its own. Using `q` avoids having
  // to re-encode the membership type -- `player.platform` is a display name like "Steam",
  // not the numeric type the API's typed parameter wants, and passing the wrong one would
  // fail in a way that looks like "player not found".
  const params =
    game === 'destiny'
      ? `q=${encodeURIComponent(player.id)}`
      : `player=${encodeURIComponent(wrapXuid(player.id))}`
  return request<CareerSnapshot>(`${BASE[game]}/career?${params}`, signal)
}

/**
 * Halo names a player as `xuid(...)`. Accepts either spelling so a pasted id works.
 */
function wrapXuid(id: string): string {
  return /^xuid\(/i.test(id) ? id : `xuid(${id})`
}

export interface Health {
  status: string
  game: string
  isFixture: boolean
  source?: string
}

export function getHealth(game: GameKey, signal?: AbortSignal): Promise<Health> {
  return request<Health>(`${BASE[game]}/health`, signal)
}
