# Career trackers

Two stat trackers — **Halo Infinite** and **Destiny 2** — sharing one data model, one set of
trend maths, and one dashboard design.

They run **with no credentials at all**, serving recorded fixtures, so you can see the whole
thing working before you go near an API key. Add credentials and the same code paths start
returning your real data.

```
dotnet run --project trackers/destiny-career-stats/src/Destiny.Api   # then open the printed URL
dotnet run --project trackers/halo-career-stats/src/Halo.Api
```

---

## The honest situation with the two APIs

These two games are not in remotely the same position, and it shapes everything below.

| | Destiny 2 | Halo Infinite |
|---|---|---|
| Official public API | **Yes** — documented, supported | **No** |
| Docs | [bungie-net.github.io](https://bungie-net.github.io/) | none; community reverse-engineering |
| Credentials | one free API key | Azure AD app + Xbox sign-in |
| Auth | a header | three token exchanges, then a fourth for Halo |
| Stability | stable, versioned | **can break without warning** |

**Destiny 2** is the pleasant one. Bungie publishes a real API, you register an application,
you get a key, and public career data needs nothing else — no OAuth, no user consent.

**Halo Infinite** has no public API and never has. 343 Industries never shipped one, and the
community project that powered most Halo trackers, HaloDotAPI, was shut down. What exists is
the set of endpoints the game client itself calls, which can be observed and used but are
undocumented and unendorsed.

We do one thing here that most Halo trackers don't: the endpoint list in
[`shared/halo-endpoint-manifest.json`](shared/halo-endpoint-manifest.json) is a **live
capture of 343's own settings service** — `settings.svc.halowaypoint.com`, which requires no
authentication and is what the game reads at startup to learn where everything lives. 177
endpoints, with their authority and whether they need a clearance header. That is a far
better source than a blog post, and it is refreshable:

```
curl -H "Accept: application/json" \
  https://settings.svc.halowaypoint.com/settings/hipc/e2a0a7c6-6efe-42af-9283-c2ab73250c48
```

> **Use the Halo side at your own risk.** It is not endorsed by Microsoft or Halo Studios,
> it can change or break at any time, and it is rate-limited by servers that owe you
> nothing. The client here retries politely, honours `Retry-After`, and caches aggressively
> for exactly that reason. Don't point it at other people's accounts in bulk.

---

## Running it

### With nothing at all

Both APIs start in fixture mode when no credentials are configured, and say so in the
response (`isFixture: true`) and on the dashboard. The fixtures are **synthetic** — realistic
shapes and believable variance, but not anyone's real matches. They exist so the mapping code
and the charts are exercised end to end.

### With Destiny 2 data

1. Sign in at [bungie.net/en/Application](https://www.bungie.net/en/Application) and create an
   application. Any name; the OAuth fields can stay empty because public stats need no OAuth.
2. Copy the **API key**.
3. Give it to the app, without putting it in the repository:

```bash
# option 1: environment variable
export EET_BUNGIE_API_KEY="your-key"

# option 2: user-secrets, which never touches the working tree
dotnet user-secrets --project trackers/destiny-career-stats/src/Destiny.Api \
  set "Bungie:ApiKey" "your-key"
```

Then look yourself up by Bungie name, including the four-digit code: `Guardian#1234`.

### With Halo data

Harder, and the difficulty is entirely Microsoft's. You need your own Azure application
because there is no shared public client to borrow.

1. [portal.azure.com](https://portal.azure.com) → **Microsoft Entra ID** → **App registrations**
   → **New registration**.
2. Supported account types: **Personal Microsoft accounts only**.
3. Leave the redirect URI empty, then under **Authentication** set
   **Allow public client flows → Yes**. Device-code sign-in does not work without it.
4. Copy the **Application (client) ID**. There is no client secret — this is a public client.

```bash
export EET_XBOX_CLIENT_ID="your-application-client-id"
dotnet run --project trackers/halo-career-stats/src/Halo.Api
# it prints a code and a URL; sign in on any device, once
```

Two details that waste an afternoon if you get them wrong, and which are why this is written
down rather than left to the reader:

* **Use the `/consumers` authority, not `/common`.** Requesting `XboxLive.signin` against
  `login.microsoftonline.com/common` fails unless the app is enrolled in the Xbox Developer
  Program. Against `/consumers` it is granted at sign-in.
* **The scopes are `XboxLive.signin offline_access`** — plain `offline_access`, the standard
  OAuth scope. There is no `XboxLive.offline_access`, and asking for it fails the whole
  request. `offline_access` is what gets you a refresh token, without which you re-authorise
  constantly.

The refresh token is cached under `%LOCALAPPDATA%\eet-trackers\`. **It is a live credential** —
treat a leak as a compromise and revoke the app, not just delete the file.

### Xbox achievements

Achievements come nearly free once Halo auth works. The token chain is identical; only the
relying party at the last exchange differs — `http://xboxlive.com` instead of the Halo one —
and that token opens `achievements.xboxlive.com`. No extra setup.

PlayStation trophies are deliberately **not** here: a different unofficial API with its own
auth (an NPSSO token lifted from a browser session). The interfaces would accommodate it; the
work has not been done.

---

## Layout

```
trackers/
├── shared/
│   ├── src/Eet.Trackers.Core/   the model both games map into, the trend maths, identity
│   ├── src/Eet.Xbox/            Azure AD → Xbox → XSTS → Spartan, and achievements
│   ├── web/                     the dashboard, served by both APIs
│   ├── fixtures/                synthetic API-shaped responses
│   └── halo-endpoint-manifest.json
├── halo-career-stats/
│   ├── src/Halo.Client/         halostats, skill and economy clients
│   └── src/Halo.Api/            minimal API
└── destiny-career-stats/
    ├── src/Destiny.Client/      Bungie.net client
    └── src/Destiny.Api/
```

Both games implement `ICareerSource` and produce a `CareerSnapshot`. That is the only reason
one dashboard can render either, and it means adding a third game is a client plus a
registration, not a new frontend.

---

## What this does that other trackers don't

**It refuses to call noise a trend.** Every "improving" or "declining" on the dashboard comes
from a weighted least-squares fit whose slope has cleared two standard errors. Anything that
hasn't is labelled *steady*, however much the line looks like it's going somewhere.
`Trends.Describe` is the whole of that policy, and there is a test that feeds it pure random
noise and asserts it stays quiet.

**A day with two matches doesn't count the same as a day with forty.** Daily aggregates carry
their sample count, the fit is weighted by it, and the dashboard visually de-emphasises thin
points. This is the single most common way a stat chart lies.

**Career rates are rates, not averages of ratios.** A 5–0 game and a 10–20 game give a true
K/D of 15/20 = 0.75. Averaging the per-match ratios gives 2.75. Both numbers appear on
various trackers; only one of them is the K/D. `Trends.Rate` computes career figures,
per-match values feed the trend charts, and the difference is documented where it matters.

**It can find you when your gamertag isn't typeable.** Xbox allows non-Latin letters, so a tag
that renders as `Elissu` may contain U+0406, a Cyrillic capital I, and no amount of typing
will ever match it. `Identity` detects that, explains which code point is responsible, and
routes the lookup through the XUID instead. Most trackers just return "player not found"
forever.

---

## Development

```bash
dotnet build trackers/Trackers.sln
dotnet test  trackers/Trackers.sln
```

The dashboard is plain HTML, CSS and ES modules — no npm, no bundler, no framework. Open
`shared/web/index.html` directly from disk and it renders against its mock payload.

Colours come from a validated categorical palette; if you change them, re-run the validator
rather than eyeballing it. Charts follow one rule above all others: **one y-axis, never two**.
