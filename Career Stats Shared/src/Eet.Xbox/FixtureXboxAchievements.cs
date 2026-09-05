using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// Achievements served from the recorded fixtures.
///
/// The important design point: the fixtures are RAW API-SHAPED JSON, exactly what
/// <c>achievements.xboxlive.com</c> would return, and they go through
/// <see cref="AchievementMapper"/> -- the same mapper the live client uses, on the same
/// code path. A fixture that was a pre-baked <see cref="TitleAchievements"/> would prove
/// only that a serializer round-trips; these prove the mapping works, and they break when
/// the mapping breaks.
///
/// The consequence worth stating: every gamerscore-out-of-a-string, every 0001-01-01 unlock
/// sentinel, every missing rarity block in the fixture is a live test of the real parsing.
/// </summary>
public sealed class FixtureXboxAchievements : IXboxAchievements
{
    /// <summary>Halo Infinite, ~120 achievements with unlocks spread over 90 days.</summary>
    public const string HaloAchievementsFixture = "achievements-halo-infinite.json";

    /// <summary>The cross-title recent-progress feed.</summary>
    public const string RecentAchievementsFixture = "achievements-recent.json";

    /// <summary>The title hub, with achievement decoration.</summary>
    public const string TitleHistoryFixture = "titlehub-titlehistory.json";

    /// <summary>One profile settings response.</summary>
    public const string ProfileFixture = "profile-settings.json";

    private readonly FixtureStore _fixtures;

    public FixtureXboxAchievements(FixtureStore? fixtures = null) => _fixtures = fixtures ?? new FixtureStore();

    public bool IsFixture => true;

    public async Task<XboxProfile?> ResolveGamertagAsync(string gamertag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamertag);

        var json = await _fixtures.ReadAsync(ProfileFixture, ct).ConfigureAwait(false);
        var profile = AchievementMapper.MapProfile(json);

        if (profile is null)
        {
            return null;
        }

        // Match the way Xbox actually behaves: an exact match, or nothing. Then the
        // homoglyph case is reproduced faithfully rather than papered over -- typing
        // "Ilissu" with a Latin I finds nothing here, exactly as it finds nothing live.
        if (string.Equals(profile.Gamertag, gamertag, StringComparison.Ordinal))
        {
            return profile;
        }

        // ...but if the typed name only LOOKS the same, say why, because a silent null is
        // the failure that makes people think the tracker is broken.
        if (Identity.LooksTheSame(profile.Gamertag, gamertag))
        {
            throw new TrackerException(
                Identity.Explain(profile.Gamertag) ??
                    "That gamertag is not the text it appears to be.",
                "Look this player up by XUID instead -- it is stable and unambiguous. The fixture " +
                $"player's XUID is {FixtureXboxAuth.FixtureXuid}.");
        }

        return null;
    }

    public async Task<TitleAchievements> GetTitleAchievementsAsync(
        string xuid,
        string titleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);

        var json = await _fixtures.ReadAsync(HaloAchievementsFixture, ct).ConfigureAwait(false);
        var achievements = AchievementMapper.MapAchievements(json, titleId);

        var titlesJson = await _fixtures.ReadAsync(TitleHistoryFixture, ct).ConfigureAwait(false);
        var titles = AchievementMapper.MapTitles(titlesJson);

        return AchievementMapper.Summarise(
            titleId,
            achievements,
            titles.TryGetValue(titleId, out var metadata) ? metadata : null);
    }

    public async Task<IReadOnlyList<Achievement>> GetRecentAchievementsAsync(
        string xuid,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);

        var json = await _fixtures.ReadAsync(RecentAchievementsFixture, ct).ConfigureAwait(false);
        var achievements = AchievementMapper.MapAchievements(json);

        // The live endpoint returns newest first; sort so the fixture cannot accidentally
        // pass by being stored in the right order.
        var ordered = achievements
            .OrderByDescending(a => a.UnlockedAt ?? DateTimeOffset.MinValue)
            .Take(maxItems)
            .ToList();

        return ordered;
    }

    public async Task<IReadOnlyList<TitleMetadata>> GetTitleHistoryAsync(
        string xuid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);

        var json = await _fixtures.ReadAsync(TitleHistoryFixture, ct).ConfigureAwait(false);
        return AchievementMapper.MapTitles(json).Values.ToList();
    }
}
