using System.Globalization;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// Destiny 2 as an <see cref="ICareerSource"/>.
///
/// Bungie's API is the well-behaved one of the two: a real, documented, public API where
/// career data needs an API key and nothing else -- no OAuth, no token chain, no reverse
/// engineering. What is left to get right is the shape of the data.
/// </summary>
public sealed class DestinyCareerSource : ICareerSource
{
    private readonly IBungieApi _api;
    private readonly DestinyManifestCache _definitions;
    private readonly BungieOptions _options;

    public DestinyCareerSource(IBungieApi api, DestinyManifestCache definitions, BungieOptions options)
    {
        _api = api;
        _definitions = definitions;
        _options = options;
    }

    public GameId Game => GameId.Destiny2;

    public bool IsFixture => _api.IsFixture;

    /// <summary>
    /// Resolve a Bungie name, or a bare membership id, to a player.
    ///
    /// Two forms are accepted. "Guardian#1234" goes to the exact-match search, which is the
    /// only search Bungie offers -- the two halves must be sent separately, and sending the
    /// combined string matches nothing while reporting success. A bare numeric id goes to
    /// the membership lookup instead, which is the reliable route for a display name that
    /// cannot be typed; see <see cref="Identity.LooksLikeHomoglyph"/> for why that case is
    /// not hypothetical.
    /// </summary>
    public async Task<Player?> ResolveAsync(string query, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // "3/4611686018467284386", the form /api/career takes, so a caller can round-trip.
        var slash = trimmed.IndexOf('/');
        if (slash > 0
            && BungieMembershipType.TryParse(trimmed[..slash], out var explicitType)
            && IsMembershipId(trimmed[(slash + 1)..]))
        {
            return await FromMembershipAsync(explicitType, trimmed[(slash + 1)..], ct).ConfigureAwait(false);
        }

        if (IsMembershipId(trimmed))
        {
            var memberships = await _api
                .GetMembershipsByIdAsync(trimmed, BungieMembershipType.All, ct)
                .ConfigureAwait(false);

            var card = Best(memberships?.DestinyMemberships, memberships?.PrimaryMembershipId);
            return card is null ? null : ToPlayer(card);
        }

        if (!Identity.TryParseBungieName(trimmed, out var displayName, out var code))
        {
            throw new TrackerException(
                $"\"{trimmed}\" is not a Bungie name or a membership id.",
                "A Bungie name looks like Guardian#1234 -- the four digits after the hash are part "
                + "of the name, not decoration, and Bungie's search takes the two halves separately. "
                + "A 17 to 19 digit membership id also works and is the only reliable route for a "
                + "display name containing characters a keyboard cannot produce.");
        }

        var cards = await _api.SearchByBungieNameAsync(displayName, code, ct).ConfigureAwait(false);
        if (cards.Count == 0)
        {
            // Bungie reports a miss as ErrorCode 1 with an empty array, so there is no error
            // code to map -- the caller is told the HTTP status to use instead.
            var explanation = Identity.Explain(displayName);
            var notFound = new TrackerException(
                $"Bungie has no player called {displayName}#{code:0000}.",
                explanation is not null
                    ? "The name you typed is not the text it appears to be. " + explanation
                    : "Check the four-digit code; it changes when a player renames. If the display "
                    + "name contains a character that only looks like a Latin letter, searching for "
                    + "the typed version will never match -- look the player up by membership id "
                    + "instead, which is stable.");

            notFound.Data["httpStatus"] = 404;
            throw notFound;
        }

        var best = Best(cards, primaryMembershipId: null);
        return best is null ? null : ToPlayer(best);
    }

    public async Task<CareerSnapshot> GetSnapshotAsync(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!BungieMembershipType.TryParse(player.Platform, out var membershipType)
            || membershipType is BungieMembershipType.None or BungieMembershipType.All)
        {
            throw new TrackerException(
                $"\"{player.Platform}\" is not a Destiny platform.",
                "Bungie keys on a (membershipType, membershipId) pair, and the shared Player record "
                + "has one id slot, so this client carries the membership type in Player.Platform. "
                + "Use one of Xbox, PlayStation, Steam, Blizzard, Stadia, Epic Games -- or the "
                + "numeric membershipType.");
        }

        var warnings = new List<string>();
        if (IsFixture)
        {
            warnings.Add(
                "Serving recorded fixtures, not live data. Every number below is synthetic. Set "
                + "BUNGIE_API_KEY (a free key from https://www.bungie.net/en/Application) to read "
                + "real career data.");
        }

        var definitionWarning = await _definitions.LoadAsync(ct).ConfigureAwait(false);
        if (definitionWarning is not null)
        {
            warnings.Add(definitionWarning);
        }

        var profile = await _api
            .GetProfileAsync(membershipType, player.Id, _options.ProfileComponents, ct)
            .ConfigureAwait(false);

        var resolved = Enrich(player, profile, membershipType);
        WarnAboutComponents(profile, warnings);

        var characterIds = CharacterIds(profile);
        if (characterIds.Count == 0)
        {
            warnings.Add(
                "This profile reports no characters. Either every Guardian has been deleted, or the "
                + "Characters component is private -- the two look identical from outside.");
        }

        // characterId 0 aggregates all-time stats, and that is documented for this endpoint.
        var stats = await _api
            .GetHistoricalStatsAsync(membershipType, player.Id, "0", "General", "AllPvP,AllPvE", ct)
            .ConfigureAwait(false);

        var matches = await LoadHistoryAsync(membershipType, player.Id, characterIds, warnings, ct)
            .ConfigureAwait(false);

        var lifetime = DestinyMapper.ToLifetime(stats, matches);
        var precision = DestinyMapper.LifetimePrecisionRate(stats);
        var (basis, competitiveOnly) = DestinyMapper.RatedBasis(matches);

        if (lifetime.Pve.Matches > 0)
        {
            // Saying this out loud, because the alternative is a career page reporting a 30%
            // win rate for a player who wins half their Crucible games.
            var pveActivities = Format.Integer(lifetime.Pve.Matches);
            var pveHours = Format.Hours(lifetime.Pve.TimePlayed);
            var pvpActivities = Format.Integer(lifetime.Competitive.Matches);
            warnings.Add(
                $"Lifetime totals are the Crucible record: {pvpActivities} matches. This account "
                + $"also completed {pveActivities} PvE activities over {pveHours}, which are counted "
                + "in Time Played and Matches but kept out of the totals -- a strike has no winner, "
                + "and a Nightfall's 200 kills for 2 deaths would put the career K/D above 6.");
        }

        if (competitiveOnly && basis.Count < matches.Count)
        {
            var rated = basis.Count.ToString(CultureInfo.InvariantCulture);
            var fetched = matches.Count.ToString(CultureInfo.InvariantCulture);
            warnings.Add(
                $"Rated figures and trend lines cover the {rated} matches that had an opponent, out "
                + $"of {fetched} fetched. A Nightfall with 140 kills and two deaths would otherwise "
                + "move a Crucible K/D further than fifty Crucible games do. The activity list and "
                + "the breakdowns below still include everything.");
        }

        if (matches.Count == 0)
        {
            warnings.Add(
                "No activity history came back, so there are no trends to draw. A brand new account "
                + "looks like this; so does one whose privacy settings hide match history.");
        }
        else
        {
            var span = matches[0].PlayedAt - matches[^1].PlayedAt;
            if (span < TimeSpan.FromDays(14))
            {
                var days = span.TotalDays.ToString("0", CultureInfo.InvariantCulture);
                warnings.Add(
                    $"The fetched history spans only {days} days. Trend directions over a window "
                    + "this short are reported as steady unless the change is large.");
            }
        }

        return new CareerSnapshot(
            resolved,
            GameId.Destiny2,
            DateTimeOffset.UtcNow,
            IsFixture,
            IsFixture ? "fixture" : "bungie.net",
            DestinyMapper.Headline(basis, lifetime, precision, competitiveOnly),
            DestinyMapper.BuildTrends(basis, competitiveOnly),
            matches,
            DestinyMapper.BuildBreakdowns(matches, basis),
            lifetime.Competitive,
            warnings);
    }

    /// <summary>
    /// The recent matches on their own, for the dashboard's activity list.
    /// </summary>
    public async Task<IReadOnlyList<MatchSummary>> GetMatchesAsync(
        Player player, int count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        // Same guard as GetSnapshotAsync: None and All are not platforms Destiny will answer
        // for, and All (-1) in particular is only valid on the player search.
        if (!BungieMembershipType.TryParse(player.Platform, out var membershipType)
            || membershipType is BungieMembershipType.None or BungieMembershipType.All)
        {
            throw new TrackerException(
                $"\"{player.Platform}\" is not a Destiny platform.",
                "Pass the membership type as the platform: Xbox, PlayStation, Steam, Epic Games, or "
                + "the numeric value.");
        }

        await _definitions.LoadAsync(ct).ConfigureAwait(false);

        var profile = await _api
            .GetProfileAsync(membershipType, player.Id, "Profiles,Characters", ct)
            .ConfigureAwait(false);

        var matches = await LoadHistoryAsync(membershipType, player.Id, CharacterIds(profile), [], ct)
            .ConfigureAwait(false);

        return count <= 0 || count >= matches.Count ? matches : matches.Take(count).ToList();
    }

    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Activity history, per character and then merged newest first.
    ///
    /// Per character on purpose. The stats endpoint documents characterId 0 as "aggregate
    /// across all characters"; the activity history endpoint documents no such thing, and in
    /// practice a 0 there returns nothing at all. Merging three characters by hand is the
    /// only way to get a complete history, and it is also the only way the trend charts see
    /// a player who switched mains.
    /// </summary>
    private async Task<IReadOnlyList<MatchSummary>> LoadHistoryAsync(
        int membershipType,
        string membershipId,
        IReadOnlyList<string> characterIds,
        List<string> warnings,
        CancellationToken ct)
    {
        var matches = new List<MatchSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var characterId in characterIds)
        {
            for (var page = 0; page < _options.MaxActivityPages; page++)
            {
                IReadOnlyList<HistoricalStatsPeriodGroup> activities;
                try
                {
                    activities = await _api.GetActivityHistoryAsync(
                            membershipType,
                            membershipId,
                            characterId,
                            DestinyActivityMode.None,
                            _options.ActivityPageSize,
                            page,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (TrackerException ex) when (IsRecoverable(ex))
                {
                    // One unreadable character should cost that character's matches, not the
                    // whole career page.
                    warnings.Add($"History for character {characterId} is unavailable. {ex.Message}");
                    break;
                }

                foreach (var activity in activities)
                {
                    var match = DestinyMapper.ToMatch(activity, _definitions);
                    // The same activity appears once per character that was in it, and a
                    // fireteam of one player's own characters is impossible -- but a retry or
                    // an overlapping page is not, so instance ids are deduplicated.
                    if (match is not null && (match.Id.Length == 0 || seen.Add(match.Id)))
                    {
                        matches.Add(match);
                    }
                }

                // A short page is the end of the history. Bungie also answers a page past the
                // end with ErrorCode 1 and no payload, which arrives here as an empty list.
                if (activities.Count < _options.ActivityPageSize)
                {
                    break;
                }
            }
        }

        matches.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt));
        return matches.Count > _options.MaxMatches
            ? matches.Take(_options.MaxMatches).ToList()
            : matches;
    }

    private static bool IsRecoverable(TrackerException ex) =>
        ex.Data["errorCode"] is int code
        && (BungiePlatformError.IsNotFound(code) || code == BungiePlatformError.DestinyPrivacyRestriction);

    private async Task<Player?> FromMembershipAsync(int membershipType, string membershipId, CancellationToken ct)
    {
        var memberships = await _api.GetMembershipsByIdAsync(membershipId, membershipType, ct)
            .ConfigureAwait(false);

        var card = Best(memberships?.DestinyMemberships, memberships?.PrimaryMembershipId);
        if (card is not null)
        {
            return ToPlayer(card);
        }

        // The lookup is a convenience. If it fails, the id and platform the caller supplied
        // are still enough for every other endpoint.
        return new Player(membershipId, membershipId, BungieMembershipType.Name(membershipType));
    }

    /// <summary>
    /// Pick the membership that other endpoints will actually answer for.
    ///
    /// Cross Save is the trap: a player with it enabled has memberships on several platforms
    /// but only one of them holds the data, named by <c>crossSaveOverride</c>. Querying any
    /// of the others returns an empty or stale profile with no error at all.
    /// </summary>
    private static UserInfoCard? Best(IReadOnlyList<UserInfoCard>? cards, long? primaryMembershipId)
    {
        if (cards is null || cards.Count == 0)
        {
            return null;
        }

        if (primaryMembershipId is { } primary)
        {
            var match = cards.FirstOrDefault(c => c.MembershipId == primary);
            if (match is not null)
            {
                return match;
            }
        }

        return cards
            .OrderByDescending(c => c.CrossSaveOverride != 0 && c.CrossSaveOverride == c.MembershipType)
            .ThenByDescending(c => c.IsPublic)
            .ThenByDescending(c => c.ApplicableMembershipTypes?.Length ?? 0)
            .First();
    }

    private static Player ToPlayer(UserInfoCard card) => new(
        card.Handle,
        card.MembershipId.ToString(CultureInfo.InvariantCulture),
        BungieMembershipType.Name(card.EffectiveMembershipType),
        string.IsNullOrWhiteSpace(card.IconPath) ? null : "https://www.bungie.net" + card.IconPath);

    /// <summary>
    /// Fill in the display name and emblem from the profile, so a career fetched by raw
    /// membership id still renders with a name on it.
    /// </summary>
    private static Player Enrich(Player player, DestinyProfileResponse profile, int membershipType)
    {
        var card = profile.Profile?.Data?.UserInfo;
        var handle = card is not null && !string.IsNullOrWhiteSpace(card.Handle) ? card.Handle : player.Handle;

        var emblem = profile.Characters?.Data?.Values
            .OrderByDescending(c => c.DateLastPlayed ?? DateTimeOffset.MinValue)
            .Select(c => c.EmblemPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return new Player(
            handle,
            player.Id,
            BungieMembershipType.Name(membershipType),
            emblem is not null ? "https://www.bungie.net" + emblem : player.IconUrl);
    }

    private static List<string> CharacterIds(DestinyProfileResponse profile)
    {
        // Two sources for the same list. The Characters component is the richer one, but the
        // Profiles component still lists ids when Characters is private.
        var fromCharacters = profile.Characters?.Data;
        if (fromCharacters is { Count: > 0 })
        {
            return fromCharacters
                .OrderByDescending(c => c.Value.DateLastPlayed ?? DateTimeOffset.MinValue)
                .Select(c => c.Value.CharacterId ?? c.Key)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
        }

        return profile.Profile?.Data?.CharacterIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
    }

    private static void WarnAboutComponents(DestinyProfileResponse profile, List<string> warnings)
    {
        // A private component comes back present and empty. Saying so beats rendering zeroes.
        if (profile.Profile?.IsPrivate == true || profile.Characters?.IsPrivate == true)
        {
            warnings.Add(
                "Part of this profile is marked private by the player, so some of it is missing "
                + "rather than zero. Only the player can change that, under Bungie.net privacy "
                + "settings.");
        }

        if (profile.Profile?.Disabled == true || profile.Characters?.Disabled == true)
        {
            warnings.Add(
                "Bungie reported a profile component as disabled, which they do during maintenance. "
                + "Retry after the next reset.");
        }
    }

    /// <summary>
    /// Destiny membership ids are 17 to 19 digits. Anything shorter that happens to be
    /// numeric is far more likely to be someone typing a name badly.
    /// </summary>
    private static bool IsMembershipId(string value) =>
        value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);
}
