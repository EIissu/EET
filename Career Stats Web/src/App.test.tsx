import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import type { CareerSnapshot } from './types'

/**
 * The shell, tested through the DOM a person actually gets.
 *
 * CareerView belongs to another agent and is stubbed: what is under test here is whether
 * the right thing is asked for, and whether the right thing is said when it cannot be
 * had. Nothing in this file touches the network.
 */
vi.mock('./components/career/CareerView', () => ({
  CareerView: ({ snapshot }: { snapshot: CareerSnapshot }) => (
    <div data-testid="career-view">{snapshot.player.handle}</div>
  ),
}))

type Reply = { status: number; body: unknown } | 'unreachable'

const HALO_PLAYER = {
  handle: 'Еlissu',
  id: '2814669301245176',
  platform: 'Xbox',
  iconUrl: null,
}

function snapshot(handle: string, isFixture = true): CareerSnapshot {
  return {
    player: { handle, id: '2814669301245176', platform: 'Xbox', iconUrl: null },
    game: 'HaloInfinite',
    generatedAt: '2026-09-05T00:00:00+00:00',
    isFixture,
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

/** Everything answers plausibly. Individual tests override the one thing they care about. */
function defaultReply(url: string): Reply {
  if (url.includes('/health')) {
    return { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: true, source: 'fixtures' } }
  }
  if (url.includes('/player')) {
    return { status: 200, body: { player: HALO_PLAYER, homoglyphNotice: null } }
  }
  return { status: 200, body: snapshot('Еlissu') }
}

let reply: (url: string) => Reply
let requested: string[]

beforeEach(() => {
  reply = defaultReply
  requested = []
  window.history.replaceState(null, '', '/')
  document.documentElement.removeAttribute('data-theme')
  window.localStorage.clear()

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      requested.push(url)
      const outcome = reply(url)
      if (outcome === 'unreachable') throw new TypeError('Failed to fetch')
      return new Response(JSON.stringify(outcome.body), {
        status: outcome.status,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function search(text: string): void {
  fireEvent.change(screen.getByLabelText(/^Search/), { target: { value: text } })
  fireEvent.click(screen.getByRole('button', { name: 'Search' }))
}

describe('the empty state', () => {
  it('names what can be searched instead of showing nothing', async () => {
    render(<App />)

    expect(screen.getByRole('heading', { name: /Search a Halo Infinite career/ })).toBeInTheDocument()
    expect(screen.getByText(/An XUID/)).toBeInTheDocument()
    expect(screen.queryByTestId('career-view')).not.toBeInTheDocument()
    // Let the health check settle before the test ends, or React reports a state update
    // outside act() for a render nobody was waiting on.
    await screen.findByText('Sample data')
  })

  it('changes its vocabulary with the game', async () => {
    render(<App />)
    fireEvent.click(screen.getByRole('radio', { name: 'Destiny 2' }))

    expect(await screen.findByText(/A Bungie name/)).toBeInTheDocument()
    expect(screen.getByLabelText(/^Search/)).toHaveAttribute(
      'placeholder',
      'Bungie name, e.g. Guardian#1234',
    )
  })
})

describe('the synthetic-data warning', () => {
  it('is on screen before anybody has searched, because health already said so', async () => {
    render(<App />)

    expect(await screen.findByText(/These numbers are synthetic and belong to no one/)).toBeInTheDocument()
    expect(screen.getByText('Sample data')).toBeInTheDocument()
  })

  it('stays up while a fixture career is displayed', async () => {
    window.history.replaceState(null, '', '/?game=halo&q=Elissu')
    render(<App />)

    expect(await screen.findByTestId('career-view')).toBeInTheDocument()
    expect(screen.getByText(/These numbers are synthetic/)).toBeInTheDocument()
  })

  it('is absent when neither health nor the snapshot claims fixtures', async () => {
    reply = (url) => {
      if (url.includes('/health')) {
        return { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: false } }
      }
      if (url.includes('/player')) return { status: 200, body: { player: HALO_PLAYER } }
      return { status: 200, body: snapshot('Еlissu', false) }
    }
    window.history.replaceState(null, '', '/?game=halo&q=Elissu')
    render(<App />)

    expect(await screen.findByTestId('career-view')).toBeInTheDocument()
    expect(screen.queryByText(/These numbers are synthetic/)).not.toBeInTheDocument()
  })
})

describe('searching', () => {
  it('reproduces a deep-linked result, game and all', async () => {
    window.history.replaceState(null, '', '/?game=destiny&q=AnaGuardian%234412')
    reply = (url) => {
      if (url.includes('/health')) return { status: 200, body: { status: 'ok', game: 'destiny-2', isFixture: true } }
      if (url.includes('/player')) {
        return {
          status: 200,
          body: { handle: 'AnaGuardian#4412', id: '4611686018400119004', platform: 'Steam', iconUrl: null },
        }
      }
      return { status: 200, body: snapshot('AnaGuardian#4412') }
    }
    render(<App />)

    expect(await screen.findByTestId('career-view')).toHaveTextContent('AnaGuardian#4412')
    expect(screen.getByRole('radio', { name: 'Destiny 2' })).toBeChecked()
    expect(screen.getByLabelText(/^Search/)).toHaveValue('AnaGuardian#4412')
    expect(requested.some((url) => url.startsWith('/api/destiny/player'))).toBe(true)
  })

  it('puts the search in the URL so the result can be linked to', async () => {
    render(<App />)
    search('Elissu')

    expect(await screen.findByTestId('career-view')).toBeInTheDocument()
    expect(window.location.search).toBe('?game=halo&q=Elissu')
  })

  it('announces the result politely rather than silently swapping the page', async () => {
    render(<App />)
    search('Elissu')

    await waitFor(() => {
      expect(screen.getByText(/Showing the Halo Infinite career of Еlissu/)).toBeInTheDocument()
    })
  })

  it('re-runs the search against the other backend when the game changes', async () => {
    render(<App />)
    search('Elissu')
    expect(await screen.findByTestId('career-view')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: 'Destiny 2' }))

    await waitFor(() => {
      expect(requested.some((url) => url.startsWith('/api/destiny/player?q=Elissu'))).toBe(true)
    })
    expect(window.location.search).toBe('?game=destiny&q=Elissu')
  })

  it('explains a handle that is not the text that was typed', async () => {
    reply = (url) => {
      if (url.includes('/health')) return { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: true } }
      if (url.includes('/player')) {
        return {
          status: 200,
          body: {
            player: HALO_PLAYER,
            homoglyphNotice: '"Еlissu" renders like "Elissu" but is not the same text: Е is U+0415.',
          },
        }
      }
      return { status: 200, body: snapshot('Еlissu') }
    }
    render(<App />)
    search('Elissu')

    expect(await screen.findByText(/U\+0415/)).toBeInTheDocument()
  })
})

describe('when it goes wrong', () => {
  it('says the API is not running, and how to run it', async () => {
    reply = (url) => (url.includes('/health') ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: false } } : 'unreachable')
    render(<App />)
    search('Elissu')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The tracker API is not answering')
    expect(alert).toHaveTextContent('dotnet run --project "Halo Career Stats/src/Halo.Api"')
  })

  it('gives the command for the game actually selected', async () => {
    window.history.replaceState(null, '', '/?game=destiny')
    reply = (url) => (url.includes('/health') ? { status: 200, body: { status: 'ok', game: 'destiny-2', isFixture: false } } : 'unreachable')
    render(<App />)
    search('AnaGuardian#4412')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('dotnet run --project "Destiny Career Stats/src/Destiny.Api"')
    expect(alert).not.toHaveTextContent('Halo.Api')
  })

  it('treats the dev proxy’s empty 500 as the backend being down, because that is what it means', async () => {
    // Measured against the real thing: with the Halo API stopped, Vite's proxy answers 500
    // with an empty body rather than refusing the connection.
    reply = (url) =>
      url.includes('/health')
        ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: false } }
        : { status: 500, body: '' }
    render(<App />)
    search('Elissu')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The tracker API is not answering')
    expect(alert).toHaveTextContent('dotnet run --project "Halo Career Stats/src/Halo.Api"')
  })

  it('still reports a 500 that came with an explanation as a real fault', async () => {
    reply = (url) =>
      url.includes('/health')
        ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: false } }
        : {
            status: 500,
            body: { title: 'The tracker fell over.', status: 500, detail: 'Halo returned a match with no players.' },
          }
    render(<App />)
    search('Elissu')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The tracker fell over.')
    expect(alert).toHaveTextContent('Halo returned a match with no players.')
    expect(alert).not.toHaveTextContent('dotnet run')
  })

  it("shows the API's own explanation of a 404, not a generic one", async () => {
    reply = (url) => {
      if (url.includes('/health')) return { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: true } }
      return {
        status: 404,
        body: {
          title: 'No player matches "zzznotreal".',
          status: 404,
          detail:
            'A Cyrillic Е looks exactly like a Latin E and cannot be typed on a normal keyboard. Look the player up by XUID instead.',
        },
      }
    }
    render(<App />)
    search('zzznotreal')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('No player found for “zzznotreal”')
    expect(alert).toHaveTextContent('Look the player up by XUID instead')
  })

  it('hands the searched text back for editing, with the cursor in it', async () => {
    reply = (url) =>
      url.includes('/health')
        ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: true } }
        : { status: 404, body: { title: 'Not found', status: 404, detail: 'Nothing matches that.' } }
    render(<App />)
    search('zzznotreal')

    fireEvent.click(await screen.findByRole('button', { name: /Edit/ }))

    const input = screen.getByLabelText(/^Search/)
    expect(input).toHaveValue('zzznotreal')
    expect(input).toHaveFocus()
  })

  it('reports any other failure by its title and detail, and never a stack trace', async () => {
    reply = (url) =>
      url.includes('/health')
        ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: true } }
        : {
            status: 502,
            body: {
              title: 'Bungie is not answering.',
              status: 502,
              detail: 'The upstream service returned 502. Try again in a minute.',
            },
          }
    render(<App />)
    search('Elissu')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Bungie is not answering.')
    expect(alert).toHaveTextContent('Try again in a minute.')
    expect(alert.textContent ?? '').not.toMatch(/at .*\.tsx?:\d+/)
  })

  it('retries the same query, which the URL alone could not express', async () => {
    reply = (url) => (url.includes('/health') ? { status: 200, body: { status: 'ok', game: 'halo-infinite', isFixture: false } } : 'unreachable')
    render(<App />)
    search('Elissu')
    await screen.findByRole('alert')

    reply = defaultReply
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByTestId('career-view')).toBeInTheDocument()
  })
})

describe('theme', () => {
  it('follows the OS until somebody says otherwise', async () => {
    render(<App />)
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
    await screen.findByText('Sample data')
  })

  it('stamps an explicit choice so it wins over the OS, and remembers it', async () => {
    render(<App />)
    await screen.findByText('Sample data')
    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'dark' } })

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    expect(window.localStorage.getItem('eet.theme')).toBe('dark')

    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'system' } })
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
  })
})
