using System.Globalization;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Mapping;

/// <summary>
/// Keys used in <see cref="MatchSummary.Extra"/>. Named constants rather than string
/// literals because both the trend selectors and the dashboard read them, and a typo in
/// one place would silently produce an empty chart rather than an error.
/// </summary>
public static class HaloMetrics
{
    public const string DamageDealt = "damageDealt";
    public const string DamageTaken = "damageTaken";
    public const string DamagePerMinute = "damagePerMinute";
    public const string ShotsFired = "shotsFired";
    public const string ShotsHit = "shotsHit";
    public const string HeadshotKills = "headshotKills";
    public const string HeadshotRate = "headshotRate";
    public const string PersonalScore = "personalScore";
    public const string ScorePerMinute = "scorePerMinute";
    public const string MaxKillingSpree = "maxKillingSpree";
    public const string Betrayals = "betrayals";
    public const string Suicides = "suicides";

    /// <summary>CSR after this match. Present only when the clearance-aware skill endpoint answered.</summary>
    public const string Csr = "csr";

    /// <summary>Change in CSR across this match.</summary>
    public const string CsrDelta = "csrDelta";

    /// <summary>Team MMR, the hidden number matchmaking actually uses.</summary>
    public const string TeamMmr = "teamMmr";

    /// <summary>
    /// Kills above what the skill service expected of a player at this rank in this match.
    /// The single most interesting number these APIs expose and one that no mainstream
    /// tracker surfaces: it separates "played well" from "played easy opponents".
    /// </summary>
    public const string KillsVsExpected = "killsVsExpected";

    /// <summary>Deaths below expectation. Positive is good, so the sign is flipped from the raw figure.</summary>
    public const string DeathsVsExpected = "deathsVsExpected";
}

/// <summary>
/// Turns raw Halo responses into the normalised shared model.
///
/// Everything here is pure and synchronous. Names for maps and modes arrive pre-resolved
/// through <paramref name="assetNames"/>, so this class never does I/O and can be tested
/// against a fixture with no HTTP stack in sight.
/// </summary>
public static class HaloMapper
{
    /// <summary>
    /// One match, from the player's point of view.
    /// </summary>
    /// <param name="playerXuid">Bare or wrapped; both are accepted.</param>
    /// <param name="assetNames">Asset id (lower-case) to public name. Missing entries fall back to a short id.</param>
    /// <returns>Null when the player is not in the match, or the match carries no usable stats.</returns>
    public static MatchSummary? ToMatchSummary(
        HaloMatchStatsResponse? stats,
        string playerXuid,
        IReadOnlyDictionary<string, string>? assetNames = null,
        HaloMatchSkillResult? skill = null)
    {
        if (stats?.MatchInfo is not { } info)
        {
            return null;
        }

        var player = FindPlayer(stats, playerXuid);
        if (player is null)
        {
            return null;
        }

        var core = SumCoreStats(player);
        if (core is null)
        {
            return null;
        }

        // Prefer how long this player was actually in the match over how long the match
        // ran. They differ for anyone who joined late or quit early, and rate metrics
        // divided by the wrong one are wrong in the direction that flatters a quitter.
        var duration = player.ParticipationInfo?.TimePlayed
            ?? info.Duration
            ?? TimeSpan.Zero;

        var minutes = duration.TotalMinutes;
        var extra = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [HaloMetrics.DamageDealt] = core.DamageDealt,
            [HaloMetrics.DamageTaken] = core.DamageTaken,
            [HaloMetrics.ShotsFired] = core.ShotsFired,
            [HaloMetrics.ShotsHit] = core.ShotsHit,
            [HaloMetrics.HeadshotKills] = core.HeadshotKills,
            [HaloMetrics.PersonalScore] = core.PersonalScore,
            [HaloMetrics.MaxKillingSpree] = core.MaxKillingSpree,
            [HaloMetrics.Betrayals] = core.Betrayals,
            [HaloMetrics.Suicides] = core.Suicides,
        };

        if (minutes > 0)
        {
            extra[HaloMetrics.DamagePerMinute] = core.DamageDealt / minutes;
            extra[HaloMetrics.ScorePerMinute] = core.PersonalScore / minutes;
        }

        if (core.Kills > 0)
        {
            extra[HaloMetrics.HeadshotRate] = (double)core.HeadshotKills / core.Kills;
        }

        if (skill is not null)
        {
            AddSkillMetrics(extra, skill);
        }

        return new MatchSummary(
            Id: stats.MatchId,
            Game: GameId.HaloInfinite,
            PlayedAt: info.StartTime,
            Duration: duration,
            Mode: ModeName(info, assetNames),
            Map: MapName(info, assetNames),
            Playlist: AssetName(info.Playlist, assetNames),
            Won: HaloEnums.ToWon(player.Outcome),
            Kills: core.Kills,
            Deaths: core.Deaths,
            Assists: core.Assists,
            Accuracy: AccuracyFraction(core),
            Score: core.PersonalScore,
            Kda: core.KDA,
            Extra: extra);
    }

    private static void AddSkillMetrics(Dictionary<string, double> extra, HaloMatchSkillResult skill)
    {
        if (skill.TeamMmr > 0)
        {
            extra[HaloMetrics.TeamMmr] = skill.TeamMmr;
        }

        var post = skill.RankRecap?.PostMatchCsr;
        var pre = skill.RankRecap?.PreMatchCsr;
        if (post is { Value: > 0 })
        {
            extra[HaloMetrics.Csr] = post.Value;
            if (pre is { Value: > 0 })
            {
                extra[HaloMetrics.CsrDelta] = post.Value - pre.Value;
            }
        }

        if (skill.StatPerformances is not { } performances)
        {
            return;
        }

        if (performances.TryGetValue("Kills", out var kills))
        {
            extra[HaloMetrics.KillsVsExpected] = kills.Count - kills.Expected;
        }

        if (performances.TryGetValue("Deaths", out var deaths))
        {
            // Fewer deaths than expected is good news, so store it the way a chart with
            // Better.Higher can read without a special case.
            extra[HaloMetrics.DeathsVsExpected] = deaths.Expected - deaths.Count;
        }
    }

    /// <summary>
    /// Accuracy as a fraction in [0,1].
    ///
    /// The service reports it as a percentage (46.875 meaning 46.875%) while the shared
    /// model and <see cref="Format.Percent"/> both want a fraction, so the division by 100
    /// happens exactly once and it happens here. Shots are preferred where present because
    /// summing a player's two team-stat blocks gives correct shot totals but only an
    /// average-of-averages accuracy.
    /// </summary>
    internal static double? AccuracyFraction(HaloCoreStats core)
    {
        if (core.ShotsFired > 0)
        {
            return (double)core.ShotsHit / core.ShotsFired;
        }

        return core.Accuracy > 0 ? core.Accuracy / 100.0 : null;
    }

    /// <summary>
    /// Find the player among everyone in the match.
    ///
    /// Compares on the bare XUID, because the same person appears as <c>xuid(2814...)</c>
    /// here and as a bare id elsewhere, and bots appear as <c>bid(...)</c> and must never
    /// match.
    /// </summary>
    internal static HaloMatchPlayer? FindPlayer(HaloMatchStatsResponse stats, string playerXuid)
    {
        var wanted = Identity.BareXuid(playerXuid);
        foreach (var candidate in stats.Players ?? [])
        {
            if (candidate.PlayerId is { } id
                && id.StartsWith("xuid(", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Identity.BareXuid(id), wanted, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Add up a player's stat blocks.
    ///
    /// PlayerTeamStats is a list because a player who switches teams mid-match accrues
    /// stats under each team id. Taking the first block -- the obvious implementation --
    /// silently under-reports every such match.
    /// </summary>
    internal static HaloCoreStats? SumCoreStats(HaloMatchPlayer player)
    {
        var blocks = (player.PlayerTeamStats ?? [])
            .Select(t => t.Stats?.CoreStats)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        if (blocks.Count == 0)
        {
            return null;
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        var first = blocks[0];
        return first with
        {
            Score = blocks.Sum(b => b.Score),
            PersonalScore = blocks.Sum(b => b.PersonalScore),
            Kills = blocks.Sum(b => b.Kills),
            Deaths = blocks.Sum(b => b.Deaths),
            Assists = blocks.Sum(b => b.Assists),
            KDA = blocks.Sum(b => b.KDA),
            Suicides = blocks.Sum(b => b.Suicides),
            Betrayals = blocks.Sum(b => b.Betrayals),
            GrenadeKills = blocks.Sum(b => b.GrenadeKills),
            HeadshotKills = blocks.Sum(b => b.HeadshotKills),
            MeleeKills = blocks.Sum(b => b.MeleeKills),
            PowerWeaponKills = blocks.Sum(b => b.PowerWeaponKills),
            ShotsFired = blocks.Sum(b => b.ShotsFired),
            ShotsHit = blocks.Sum(b => b.ShotsHit),
            DamageDealt = blocks.Sum(b => b.DamageDealt),
            DamageTaken = blocks.Sum(b => b.DamageTaken),
            CalloutAssists = blocks.Sum(b => b.CalloutAssists),
            MaxKillingSpree = blocks.Max(b => b.MaxKillingSpree),
            Spawns = blocks.Sum(b => b.Spawns),
        };
    }

    /// <summary>
    /// A readable mode name.
    ///
    /// Prefers the UGC game variant's published name ("Slayer:Ranked") because it is
    /// authoritative and season-proof, and falls back to the community-derived
    /// GameVariantCategory table, which is neither. See <see cref="HaloEnums"/> for how
    /// much to trust each.
    /// </summary>
    internal static string ModeName(HaloMatchInfo info, IReadOnlyDictionary<string, string>? assetNames)
    {
        if (AssetName(info.UgcGameVariant, assetNames) is { } published)
        {
            // "Slayer:Ranked" and "Slayer" are the same mode for form purposes.
            var colon = published.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 ? published[..colon].Trim() : published;
        }

        return HaloEnums.GameVariantCategoryName(info.GameVariantCategory);
    }

    internal static string MapName(HaloMatchInfo info, IReadOnlyDictionary<string, string>? assetNames) =>
        AssetName(info.MapVariant, assetNames)
        ?? ShortId(info.MapVariant?.AssetId ?? info.LevelId);

    internal static string? AssetName(HaloAssetRef? asset, IReadOnlyDictionary<string, string>? assetNames)
    {
        if (asset?.AssetId is null || assetNames is null)
        {
            return null;
        }

        return assetNames.TryGetValue(asset.AssetId.ToLowerInvariant(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }

    /// <summary>
    /// A GUID nobody can name, shortened so a breakdown row stays readable. Marked with a
    /// leading "Map " so it is obviously a gap in our data rather than a map called
    /// "8420410b".
    /// </summary>
    internal static string ShortId(string? id) =>
        string.IsNullOrEmpty(id)
            ? "Unknown"
            : string.Create(CultureInfo.InvariantCulture, $"Map {id[..Math.Min(8, id.Length)]}");
}
