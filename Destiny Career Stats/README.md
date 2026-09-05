# Destiny 2 career stats

Reads a Guardian's career out of Bungie's public API and maps it into the shared
`CareerSnapshot` model, so the same dashboard renders Destiny and Halo alike.

## Run it now, with nothing configured

```
dotnet run --project Destiny Career Stats/src/Destiny.Api
```

With no API key the tracker serves the recorded fixtures in `Career Stats Shared/fixtures`. It
serves them *through the real client*: the same requests, the same ErrorCode envelope
handling, the same paging, the same manifest cache, the same mapping. Nothing is stubbed
above the HTTP handler, so the fixture path exercises the code that runs against bungie.net.

```
GET /api/health
GET /api/player?q=AnaGuardian%234412
GET /api/career?membershipType=3&membershipId=4611686018400119004
GET /api/matches?membershipType=3&membershipId=4611686018400119004&count=25
```

`/api/career` and `/api/matches` also accept `?q=Guardian%231234` instead of the pair.

`/api/health` reports `mode: "fixture"` or `mode: "live"`, and never reports the key.

## Going live

A free key comes from <https://www.bungie.net/en/Application>. Public career data needs
only the key -- no OAuth, no token chain. Supply it either way:

```
setx BUNGIE_API_KEY <your key>            # or Bungie__ApiKey
```

or copy `src/Destiny.Api/appsettings.Development.json.example` to
`appsettings.Development.json` (gitignored) and put it there. A key turns fixture mode off;
there is no other switch.

## Layout

| Path | What it is |
| --- | --- |
| `src/Destiny.Client` | HTTP, the ErrorCode envelope, the manifest cache, the mapping. No package references, so it restores offline. |
| `src/Destiny.Api` | Minimal API, assembly name `destiny-career-stats`. RFC 7807 on failure, static files from `Career Stats Shared/web` when that exists. |
| `tests/Destiny.Tests` | xUnit. Stub `HttpMessageHandler` throughout; no test opens a socket or needs a key. |
| `tools/generate-fixtures.py` | Regenerates the synthetic fixtures. |

## Things that are easy to get wrong here

**`ErrorCode`, not the status code.** Bungie answers HTTP 200 for an invalid key, a private
profile and a rate limit alike. `ErrorCode == 1` is success and nothing else is. Every read
goes through `BungieResponse`, and `destiny-error-privacy.json` is a recorded 200-with-a-
failure so the test for it reads like the real thing.

**A success can have no payload.** Activity history past the last page returns `ErrorCode 1`
with no `Response` at all. Treating that as a failure turns "no more matches" into "the
career could not be loaded".

**`characterId` 0 is not universal.** It aggregates on the *stats* endpoint, which is
documented. Activity history documents no such thing and returns nothing for it, so history
is fetched per character and merged.

**Cross Save.** A player with it enabled has memberships on several platforms and only one
of them holds the data, named by `crossSaveOverride`. Querying another returns an empty
profile with no error.

**The manifest is not the world file.** Only `DestinyActivityDefinition` and
`DestinyActivityModeDefinition` are fetched, from `jsonWorldComponentContentPaths`, then
projected down to name/icon/PvP-flag and cached on disk under the manifest version. The
hundred-megabyte SQLite world content is never downloaded.

**PvE has no winner.** Lifetime totals are the Crucible record so that `WinRate` and `Kd`
mean something; the PvE side is reported separately in the headline and the warnings. Rated
figures and trend lines use matches that had an opponent, because a Nightfall with 140 kills
for 2 deaths moves a K/D further than fifty Crucible games.

**There is no accuracy stat.** Destiny publishes no shots-fired or shots-hit figure
anywhere. The nearest thing is `precisionKills`, which is what the "Precision" headline
reports.

## Fixtures

`Career Stats Shared/fixtures/destiny-*.json` are raw Bungie-shaped responses, envelope
included, marked `_note: SYNTHETIC`. They describe 120 matches across 88 days on three
characters, with a real trend in them: K/D climbs from about 1.11 to about 1.43 and the win
rate from about 54% to about 62%, so the trend charts have something true to find and the
significance test in `Trends.cs` has a reason to say "improving".

Regenerate with:

```
python Destiny Career Stats/tools/generate-fixtures.py
```
