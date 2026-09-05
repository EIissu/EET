using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// Achievements for one player, live or from fixtures.
///
/// This interface is not in <c>Contracts.cs</c> because achievements are a platform
/// concern rather than a career-stats one -- the shared contract defines the
/// <see cref="Achievement"/> and <see cref="TitleAchievements"/> records that both
/// implementations produce, and this is the seam between them and whichever transport
/// filled them in. <see cref="IsFixture"/> mirrors <c>ICareerSource.IsFixture</c> so the
/// dashboard can badge fixture data the same way whichever source it came from.
/// </summary>
public interface IXboxAchievements
{
    /// <summary>True when this is serving recorded fixtures rather than the live service.</summary>
    bool IsFixture { get; }

    /// <summary>
    /// Look a player up by gamertag.
    ///
    /// Returns null rather than throwing when nothing matches, because "nothing matches" is
    /// the normal answer for a homoglyph gamertag -- see
    /// <c>Eet.Trackers.Core.Identity.LooksLikeHomoglyph</c>. Callers holding an XUID should
    /// skip this entirely; an XUID is stable and a display name is not.
    /// </summary>
    Task<XboxProfile?> ResolveGamertagAsync(string gamertag, CancellationToken ct = default);

    /// <summary>Every achievement for one title, unlocked or not, following pagination.</summary>
    Task<TitleAchievements> GetTitleAchievementsAsync(
        string xuid,
        string titleId,
        CancellationToken ct = default);

    /// <summary>
    /// Recent achievement progress across every title, newest first. This is the feed a
    /// dashboard's "what have I done lately" panel is built from.
    /// </summary>
    Task<IReadOnlyList<Achievement>> GetRecentAchievementsAsync(
        string xuid,
        int maxItems = 100,
        CancellationToken ct = default);

    /// <summary>
    /// The player's title history, with achievement decoration -- what they have played and
    /// how much gamerscore each game holds.
    /// </summary>
    Task<IReadOnlyList<TitleMetadata>> GetTitleHistoryAsync(string xuid, CancellationToken ct = default);
}
