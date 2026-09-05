# Career stats, as a site

Type a name, pick a game, see that player's career. Two games, one search box, one switcher.

The React app lives here. The data comes from two ASP.NET Core services that sit beside it,
one per game, because the two games have nothing in common at the network layer: Destiny is
a documented public API reachable with a key, Halo is an undocumented one reachable only
with a Spartan token minted from an Xbox sign-in. Neither key ever reaches the browser.
That is the entire reason a server exists.

```
   browser :5173                Vite dev server (proxy)
        |                                |
        |  /api/halo/*    ------------>  http://127.0.0.1:5210/api/*     halo-career-stats
        |  /api/destiny/* ------------>  http://127.0.0.1:5231/api/*     destiny-career-stats
        |
   one origin, no credentials, no CORS
```

## Prerequisites

| | |
| --- | --- |
| .NET SDK 10 | Both APIs target `net10.0`. |
| Node 20.19+ or 22.12+ | What Vite 7 requires. Developed on Node 24 / npm 11. |
| Credentials | **None.** Both services serve recorded fixtures out of the box. |

## Run the whole thing, locally, right now

Once, to install the front end's dependencies:

```
cd "Career Stats Web" && npm install
```

Then three commands, each in its own terminal, from the repository root:

```
dotnet run --project "Halo Career Stats/src/Halo.Api"
dotnet run --project "Destiny Career Stats/src/Destiny.Api"
cd "Career Stats Web" && npm run dev
```

Open <http://localhost:5173>. With nothing configured you get a complete career for a
synthetic player, and the site says so, loudly, because it is invented data.

### Ports

| Port | What is on it |
| --- | --- |
| 5173 | Vite dev server. This is the site. |
| 5210 | `halo-career-stats`. Its own `/api/*`, and the built site at `/`. |
| 5231 | `destiny-career-stats`. Its own `/api/*`, and the built site at `/`. |

The dev server proxies `/api/halo/*` to 5210 and `/api/destiny/*` to 5231, stripping the
game prefix, so the browser only ever talks to one origin. If 5210 is taken:

```
HALO_API=http://127.0.0.1:5310 npm run dev      # DESTINY_API for the other one
```

The two APIs are independent processes and know nothing about each other. Start one and
not the other and the proxy for the missing one simply fails to connect, which `src/lib/api.ts`
turns into a named error rather than a stack trace.

### Or without the dev server at all

```
cd "Career Stats Web" && npm run build
dotnet run --project "Halo Career Stats/src/Halo.Api"
```

Then open <http://127.0.0.1:5210>. Each API prefers the built app in `Career Stats Web/dist`
and falls back to the dependency-free dashboard in `Career Stats Shared/web` when nobody has
run npm. Which one it chose is the first line it logs:

```
info: halo-career-stats[0]
      Serving the built React app from ...\Career Stats Web\dist.
```

The vanilla dashboard is not legacy and is not going away. It is the zero-build path, it
needs no toolchain at all, and it must keep working.

## Scripts

| | |
| --- | --- |
| `npm run dev` | Vite on 5173, with the two proxies. |
| `npm run build` | Type-checks (`tsc -b`) and writes `dist/`. Strict, with `noUncheckedIndexedAccess` and `exactOptionalPropertyTypes`. |
| `npm run preview` | Serves `dist/` without the proxy. Point it at a running API or it has no data. |
| `npm test` | Vitest. |
| `npm run typecheck` | Types only, no emit. |

## Going live

Fixture mode is not a demo mode with a reduced code path. Both trackers serve fixtures
*through the real client*: the same requests, the same envelope handling, the same paging,
the same mapping. Turning credentials on changes where the bytes come from and nothing else.

### Destiny 2

A free key from <https://www.bungie.net/en/Application>. Public career data needs the key
and nothing else -- no OAuth, no token chain.

```
setx BUNGIE_API_KEY <your key>
```

`Bungie__ApiKey`, or `appsettings.Development.json` (gitignored, there is an `.example`
beside it), work identically. `/api/health` then reports `"mode": "live"`. It never reports
the key.

### Halo Infinite

Harder, and deliberately not a config flag. `Halo.Api` does not reference `Eet.Xbox` at all;
`Halo.Client` codes against the `IXboxAuth` interface and serves fixtures whenever nothing
implements it. To go live you add the reference and one registration, above
`AddHaloCareerSource` in `Halo Career Stats/src/Halo.Api/Program.cs`:

```csharp
builder.Services.AddSingleton<IXboxAuth>(
    XboxServices.CreateAuth(XboxOptions.FromEnvironment()));
```

and set `EET_XBOX_CLIENT_ID` to the application (client) id of an Azure AD public-client app
with the delegated `XboxLive.signin` permission. The first run prompts a device-code sign-in;
`ConsoleDeviceCodePrompt` prints the code and the URL, and the refresh token is cached at
`EET_XBOX_TOKEN_CACHE` (a per-user default when that is unset).

`/api/health` reports which credential-shaped variables it can see -- names only, never
values -- and says plainly that configuration alone changes nothing until `IXboxAuth` is
registered. That gap is the failure this endpoint exists to catch: a tracker quietly serving
somebody else's synthetic K/D to a person who did the configuration correctly.

## Read this before you put a Halo tracker on the internet

**Halo's API is undocumented and unendorsed.** Nothing this project talks to on the Halo
side is a published interface. There is no contract, no deprecation notice, and no
permission: the endpoints come from a manifest the game itself fetches, the clearance values
are what retail clients happen to send, and 343 can change or withdraw any of it without
telling anyone. The client identifies itself honestly as `eet-halo-career-stats/1.0` and
keeps four requests in flight precisely so that, if this tool ever needs blocking, it can be
blocked by name rather than by IP range. Treat every Halo response as a favour, not a right.

**An openly searchable Halo tracker spends the operator's own Xbox account.** This is the
part worth stopping over. Destiny's key authenticates an application; Halo's Spartan token
authenticates *a person* -- you. There is no application identity to hold it, so every
lookup any visitor runs is made with the credentials of whoever deployed the site, against
Microsoft's and 343's services, under that account's name and rate limit. A single search
box on a public URL therefore means:

* every stranger's query is attributable to your gamertag;
* your account absorbs the rate limiting, and the ban if one ever lands;
* a scraper pointed at the box is indistinguishable from you scraping.

The browser holding no credential protects your visitors. It does not protect you. If this
goes anywhere public, put your own rate limit and cache in front of it, and go in knowing
whose account is paying for it. Destiny alone is a much smaller thing to expose.

## The contract between the app and the APIs

Typed in `src/types.ts`, mirrored from `Career Stats Shared/src/Eet.Trackers.Core/Career.cs`.
Three conventions, because getting them wrong produces plausible-looking nonsense rather
than an error:

* **Durations are seconds.** Not milliseconds, and not `"24.22:30:00"` -- both APIs write a
  `TimeSpan` as a number.
* **Accuracy and win rate are fractions in `[0,1]`.** Not percentages.
* **Every number ships a pre-formatted, culture-invariant string.** Render `formatted`.
  Do not re-format it, do not round it again, do not call `toLocaleString`: a K/D is `1.42`
  for every reader on earth, and re-formatting it makes it `1,42` for half of Europe.

And one rule that is not about formatting: **a field the API did not send is not zero.**
`delta: null` means there was no previous window to compare against, which is a different
fact from "no change" and must not render the same way.

### Endpoints

Both services answer the same four routes under their own origin.

| | Halo | Destiny |
| --- | --- | --- |
| `GET /api/health` | mode, manifest size, credential names | mode, manifest version, key presence |
| `GET /api/player?q=` | gamertag, `xuid(...)`, or a bare XUID | Bungie name `Guardian#1234`, or a membership id |
| `GET /api/career?...` | `?player=` | `?q=`, or `?membershipType=&membershipId=` |
| `GET /api/matches?...` | `?player=&count=` | `?q=&count=` |

`?q=` is forgiving on both: leading and trailing whitespace, the zero-width characters a
copy-and-paste drags along, and a pasted `xuid(...)` wrapper are all removed before anything
is matched. Nothing else is touched -- in particular, the letters are left exactly as typed,
because which letters they are is the whole problem below.

Anything else under `/api` is a JSON 404 with a `detail` naming the routes that do exist.
Anything else *outside* `/api` is `index.html`, so a deep link survives a reload.

## Things that are easy to get wrong here

**The Halo fixture player cannot be typed.** The gamertag renders as `Elissu` and begins
with U+0415, CYRILLIC CAPITAL LETTER IE. It is not a Latin `E`, it does not come off a
keyboard, and a tracker that searches only for exact text finds nothing, forever, with no
error that says why. Searching the Latin spelling here *does* find them: the API folds
homoglyphs to an ASCII skeleton, tells you it did (`"matchedBy": "homoglyph"`), and names the
offending code point. There is a second, deliberately similar player in the fixtures to prove
the fold has not turned search into "close enough". Destiny's fixtures carry the same trap at
`Ilissu#9007` (U+0406), where Bungie's search is exact and cannot be folded -- so a miss
there answers with the code point and tells you to look the player up by membership id.

**Fixture data must be unmistakable.** Every snapshot carries `isFixture`, and the UI has to
say so unmissably whenever it is true. These numbers are invented and belong to nobody; a
person must never come away thinking they read somebody's real career.

**CORS exists only in Development.** The dev proxy makes everything same-origin, so the
policy is a safety net for a developer who bypasses it: exactly
`http://localhost:5173` and `http://127.0.0.1:5173`, GET/HEAD/OPTIONS, nothing else. In
Production no policy is registered at all -- the API serves the built app itself, so every
legitimate request is same-origin. `AllowAnyOrigin` on a Halo tracker would hand any site on
the internet the ability to run searches through the operator's Xbox account.

**`index.html` is never cached; the bundles always are.** The bundles are content-hashed, so
their names change when they do. `index.html` is the file that names them, and a cached copy
of it points at files the next deploy has already deleted.

**A duration of `null` and a duration of `0` are different.** So are an absent `accuracy` and
an accuracy of zero. The APIs omit what they do not know rather than inventing a value for
it, and the app must render nothing rather than a confident `0`.

## Layout

| Path | What it is |
| --- | --- |
| `src/types.ts` | The JSON both backends return, hand-written and authoritative. |
| `src/lib/api.ts` | `searchPlayer`, `getCareer`, `getHealth`, `GAMES`. |
| `vite.config.ts` | Dev server, the two proxies, and the Vitest configuration. |
| `dist/` | `npm run build` output. Either API serves it in preference to the vanilla dashboard. |
| `../Career Stats Shared/web` | The no-build fallback dashboard. Vanilla JS, zero dependencies. |
| `../Career Stats Shared/fixtures` | The synthetic data both trackers serve with no credentials. |
