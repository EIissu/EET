import { useEffect, useRef, useState } from 'react'
import { GAMES } from './lib/api'
import type { GameKey } from './types'
import { navigate, useUrlState } from './lib/urlState'
import { useCareer, useHealth } from './lib/useCareer'
import { GameSwitch } from './components/GameSwitch'
import { SearchBar } from './components/SearchBar'
import { StatusBanner } from './components/StatusBanner'
import { EmptyState } from './components/EmptyState'
import { CareerView } from './components/career/CareerView'

type GameInfo = (typeof GAMES)[number]

/** No lookup table and no non-null assertion: the union has two members and both exist. */
function gameInfo(key: GameKey): GameInfo {
  for (const game of GAMES) {
    if (game.key === key) return game
  }
  throw new Error(`No game named "${key}".`)
}

/**
 * The command that starts the backend for a given game, spelled exactly as it is run from
 * the repository root. An error that says "the API is not running" and then makes you go
 * and find out how to start it has only done half the job.
 */
const START_COMMAND: Record<GameKey, string> = {
  halo: 'ASPNETCORE_URLS=http://127.0.0.1:5210 dotnet run --project "Halo Career Stats/src/Halo.Api"',
  destiny: 'dotnet run --project "Destiny Career Stats/src/Destiny.Api"',
}

/** What each backend will resolve. Phrased as instructions, not as a schema. */
const ACCEPTS: Record<GameKey, readonly string[]> = {
  halo: [
    'A gamertag, spelled the way it looks -- confusable letters are folded, so the Latin spelling finds a Cyrillic one.',
    'An XUID, as xuid(2814669301245176) or bare. Always unambiguous, and the only way to reach a tag that cannot be typed.',
  ],
  destiny: [
    'A Bungie name including the four digits after the hash, which are part of the name and change when a player renames.',
    'A Destiny membership id, which is stable however often the display name changes.',
  ],
}

/**
 * Sample players, shown ONLY when the backend reports it is serving fixtures. Both are
 * synthetic identities from Career Stats Shared/fixtures; the Halo one is deliberately the
 * typeable Latin spelling of a gamertag that is really spelled with a Cyrillic Е, because
 * the search finding it anyway is the whole point of the exercise.
 */
const SAMPLES: Record<GameKey, readonly string[]> = {
  halo: ['Elissu', 'xuid(2814669301245176)'],
  destiny: ['AnaGuardian#4412'],
}

type ThemeChoice = 'system' | 'light' | 'dark'

const THEME_KEY = 'eet.theme'

function isThemeChoice(value: string | null): value is ThemeChoice {
  return value === 'system' || value === 'light' || value === 'dark'
}

function readTheme(): ThemeChoice {
  try {
    const saved = window.localStorage.getItem(THEME_KEY)
    if (isThemeChoice(saved)) return saved
  } catch {
    // Storage can be unavailable -- private mode, blocked cookies. Following the OS is a
    // perfectly good answer and not worth an error for.
  }
  return 'system'
}

export function App() {
  const url = useUrlState()
  const game = gameInfo(url.game)

  // The text in the box, which is not the same thing as the search that has been run. It
  // follows the URL when the URL changes underneath it -- the back button, a deep link --
  // and is otherwise the visitor's to edit. Adjusting it during render rather than in an
  // effect avoids a frame showing the previous query in the box.
  const [draft, setDraft] = useState(url.query)
  const [syncedQuery, setSyncedQuery] = useState(url.query)
  if (url.query !== syncedQuery) {
    setSyncedQuery(url.query)
    setDraft(url.query)
  }

  const [attempt, setAttempt] = useState(0)
  const state = useCareer(url.game, url.query, attempt)
  const health = useHealth(url.game)
  const inputRef = useRef<HTMLInputElement>(null)

  const [theme, setTheme] = useState<ThemeChoice>(readTheme)
  useEffect(() => {
    const root = document.documentElement
    if (theme === 'system') root.removeAttribute('data-theme')
    else root.setAttribute('data-theme', theme)
    try {
      window.localStorage.setItem(THEME_KEY, theme)
    } catch {
      // A theme that does not survive a reload still beats one that throws.
    }
  }, [theme])

  function runSearch(query: string) {
    const trimmed = query.trim()
    if (!trimmed) return
    // Re-submitting the same text is a retry, not a navigation: pushing an identical entry
    // would make the back button appear broken.
    if (trimmed === url.query) setAttempt((n) => n + 1)
    else navigate({ game: url.game, query: trimmed }, 'push')
  }

  function changeGame(next: GameKey) {
    if (next === url.game) return
    // With a search running, switching game produces a different result and should be
    // undoable. Without one, nothing has happened yet worth a history entry.
    navigate({ game: next, query: url.query }, url.query ? 'push' : 'replace')
  }

  function editSearch() {
    const input = inputRef.current
    if (!input) return
    input.focus()
    input.select()
  }

  // Fixtures are declared by /api/health before anybody searches, and again on every
  // snapshot. Either is enough to put the warning on screen; a snapshot that says so is
  // the authoritative one, since it describes the numbers actually being rendered.
  const isFixture = state.status === 'ready' ? state.snapshot.isFixture : health?.isFixture === true

  // One polite region for progress and results. Errors are not routed through it: they
  // carry role="alert" instead, and announcing both would say everything twice.
  const announcement =
    state.status === 'loading'
      ? `Searching ${game.name} for ${state.query}.`
      : state.status === 'ready'
        ? `Showing the ${game.name} career of ${state.snapshot.player.handle}.`
        : ''

  return (
    <div className="page">
      <a className="skip-link" href="#results">
        Skip to results
      </a>

      {isFixture && (
        <div className="fixturebar">
          <div className="shell fixturebar-inner">
            <span className="fixturebar-tag">Sample data</span>
            <p>
              <strong>These numbers are synthetic and belong to no one.</strong>{' '}
              <span className="fixturebar-why">
                The tracker has no credentials configured, so it is serving invented
                fixtures. Nothing on this page is a real career.
              </span>
            </p>
          </div>
        </div>
      )}

      <header className="masthead">
        <div className="shell masthead-inner">
          <div className="wordmark">
            <h1>Career Stats</h1>
            <p className="tagline">
              Look up a player&rsquo;s career in Halo Infinite or Destiny 2. One search box,
              two games.
            </p>
          </div>
          <label className="themepick">
            Theme
            <select
              value={theme}
              onChange={(event) => {
                const next = event.target.value
                if (isThemeChoice(next)) setTheme(next)
              }}
            >
              <option value="system">System</option>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
            </select>
          </label>
        </div>

        <div className="shell controls">
          <GameSwitch value={url.game} onChange={changeGame} />
          <SearchBar
            value={draft}
            onValueChange={setDraft}
            onSubmit={() => runSearch(draft)}
            gameName={game.name}
            hint={game.searchHint}
            busy={state.status === 'loading'}
            inputRef={inputRef}
          />
        </div>
      </header>

      <main className="shell results" id="results" tabIndex={-1}>
        <p className="sr-only" aria-live="polite">
          {announcement}
        </p>

        {state.status === 'idle' && (
          <EmptyState
            gameName={game.name}
            accepts={ACCEPTS[url.game]}
            samples={isFixture ? SAMPLES[url.game] : []}
            onPickSample={(sample) => {
              setDraft(sample)
              runSearch(sample)
            }}
          />
        )}

        {state.status === 'loading' && (
          <div className="loading">
            <p>
              Looking up {state.query} in {game.name}…
            </p>
            <div className="skeleton-row" aria-hidden="true">
              <div className="skeleton skeleton-card" />
              <div className="skeleton skeleton-card" />
              <div className="skeleton skeleton-card" />
              <div className="skeleton skeleton-card" />
            </div>
            <div className="skeleton skeleton-chart" aria-hidden="true" />
          </div>
        )}

        {state.status === 'error' && (
          <ErrorReport
            status={state.error.status}
            title={state.error.message}
            detail={state.error.detail ?? null}
            query={state.query}
            command={START_COMMAND[url.game]}
            onEdit={editSearch}
            onRetry={() => setAttempt((n) => n + 1)}
          />
        )}

        {state.status === 'ready' && (
          <>
            {state.notice && (
              <StatusBanner tone="note" title="That is not quite the name you typed">
                <p>{state.notice}</p>
              </StatusBanner>
            )}
            <CareerView snapshot={state.snapshot} />
          </>
        )}
      </main>

      <footer className="shell sitefoot">
        <p>
          Halo Infinite and Destiny 2 careers, served by two local APIs. The browser never
          holds a key: that is the entire reason there is a server.
        </p>
        {health?.source && <p>Selected service: {game.name} &middot; {health.source}.</p>}
      </footer>
    </div>
  )
}

/**
 * Is this a service that is not there, rather than a service with something to say?
 *
 * A fetch that never connects gives status 0. But in development the app does not talk to
 * the backends directly -- it talks to the Vite proxy, and a proxy whose upstream is down
 * answers 500 with an EMPTY body rather than failing at the network layer. Measured, not
 * assumed: with the Halo API stopped, `/api/halo/health` returns exactly that.
 *
 * So an empty gateway-shaped status means the same thing to a person as status 0 does, and
 * saying "the service answered 500" instead of "start the server, here is how" would be
 * technically true and practically useless. A real fault inside a running API arrives as a
 * problem document with a detail, which is why the detail is what separates the two.
 */
function looksUnreachable(status: number, detail: string | null): boolean {
  if (status === 0) return true
  return !detail && (status === 500 || status === 502 || status === 503 || status === 504)
}

/**
 * A failure, explained.
 *
 * Three cases, because there are three genuinely different things to do about them: start
 * the server, fix the spelling, or read what the service said. Never a stack trace -- the
 * person reading this cannot fix our code and should not be shown it.
 */
function ErrorReport({
  status,
  title,
  detail,
  query,
  command,
  onEdit,
  onRetry,
}: {
  status: number
  title: string
  detail: string | null
  query: string
  command: string
  onEdit: () => void
  onRetry: () => void
}) {
  if (looksUnreachable(status, detail)) {
    return (
      <StatusBanner tone="warning" title="The tracker API is not answering" role="alert">
        <p>
          Nothing is serving this game&rsquo;s endpoint. Start the service from the
          repository root:
        </p>
        <pre>
          <code>{command}</code>
        </pre>
        <p>Then run the search again. No credentials are needed; it will serve fixtures.</p>
        {status !== 0 && (
          <p>
            (The dev server answered {status} with no explanation, which is what its proxy
            does when nothing is listening on the port behind it.)
          </p>
        )}
        <div className="banner-actions">
          <button type="button" onClick={onRetry}>
            Try again
          </button>
        </div>
      </StatusBanner>
    )
  }

  if (status === 404) {
    return (
      <StatusBanner tone="warning" title={`No player found for “${query}”`} role="alert">
        {/* The API's own words. Halo's explain the homoglyph case far better than a
            generic "not found" ever could, so they are shown rather than summarised. */}
        {detail ? <p>{detail}</p> : <p>{title}</p>}
        <div className="banner-actions">
          <button type="button" onClick={onEdit}>
            Edit “{query}”
          </button>
        </div>
      </StatusBanner>
    )
  }

  return (
    <StatusBanner tone="warning" title={title} role="alert">
      {detail && <p>{detail}</p>}
      <div className="banner-actions">
        <button type="button" onClick={onRetry}>
          Try again
        </button>
        <button type="button" onClick={onEdit}>
          Edit “{query}”
        </button>
      </div>
    </StatusBanner>
  )
}
