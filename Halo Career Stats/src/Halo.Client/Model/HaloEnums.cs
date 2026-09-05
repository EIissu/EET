using System.Globalization;

namespace Eet.Halo.Client.Model;

/// <summary>
/// The numeric enumerations these services return, and an honest account of how well we
/// know each one.
///
/// PROVENANCE, because it differs sharply between the three:
///
///   Outcome              High confidence. Four values, self-evident from any real match
///                        set (the winning team's players all carry the same one), and
///                        consistent across every community client.
///
///   GameVariantCategory  Medium confidence, community-derived. 343 does not publish this
///                        mapping anywhere in the endpoint manifest. The values below are
///                        the ones fan clients agree on, but the list has grown with every
///                        season and there is no reason to think it is complete. Unknown
///                        ids therefore render as "Mode 37" rather than being guessed at
///                        or dropped, and callers should prefer the UGC game variant's
///                        PublicName when the discovery service is reachable.
///
///   Tier / SubTier       High confidence on the tier names, medium on the off-by-one:
///                        SubTier is zero-based on the wire and one-based in every UI.
/// </summary>
public static class HaloEnums
{
    /// <summary>How a match ended, from the point of view of one player or team.</summary>
    public enum Outcome
    {
        Unknown = 0,
        Tie = 1,
        Win = 2,
        Loss = 3,

        /// <summary>
        /// Quit, or was still connected but not present at the end. Distinct from a loss
        /// and deliberately kept distinct: counting DNFs as losses is how a tracker tells
        /// somebody their win rate is worse than it is.
        /// </summary>
        DidNotFinish = 4,
    }

    /// <summary>
    /// Won / lost / neither, as the shared model wants it. Null for a tie or a DNF, which
    /// <see cref="Eet.Trackers.Core.MatchSummary.Won"/> models as null rather than false.
    /// </summary>
    public static bool? ToWon(int outcome) => (Outcome)outcome switch
    {
        Outcome.Win => true,
        Outcome.Loss => false,
        _ => null,
    };

    public static bool IsDidNotFinish(int outcome) => (Outcome)outcome == Outcome.DidNotFinish;

    /// <summary>Match history query values for the <c>type</c> parameter.</summary>
    public static class MatchType
    {
        public const string All = "all";
        public const string Matchmade = "matchmade";
        public const string Custom = "custom";
        public const string Local = "local";
    }

    /// <summary>
    /// LifecycleMode. 3 is matchmaking, 1 is custom. Campaign and Forge use others we
    /// never see because the history query filters to matchmade.
    /// </summary>
    public const int LifecycleMatchmaking = 3;

    private static readonly Dictionary<int, string> GameVariantCategories = new()
    {
        [6] = "Slayer",
        [7] = "Attrition",
        [9] = "Fiesta",
        [11] = "Strongholds",
        [12] = "Bastion",
        [13] = "King of the Hill",
        [14] = "Total Control",
        [15] = "Capture the Flag",
        [16] = "Assault",
        [17] = "Extraction",
        [18] = "Oddball",
        [19] = "Stockpile",
        [20] = "Juggernaut",
        [23] = "Escalation",
        [24] = "Grifball",
        [25] = "Land Grab",
        [39] = "Minigame",
        [41] = "Firefight Bastion",
        [42] = "Firefight King of the Hill",
    };

    /// <summary>
    /// A readable mode name, or a stable placeholder. Never throws and never invents:
    /// an id we do not recognise comes back as "Mode 37", which at least groups correctly
    /// in a breakdown and is obviously a gap rather than a wrong answer.
    /// </summary>
    public static string GameVariantCategoryName(int category) =>
        GameVariantCategories.TryGetValue(category, out var name)
            ? name
            : string.Create(CultureInfo.InvariantCulture, $"Mode {category}");

    /// <summary>True when we actually know the name, as opposed to having made a placeholder.</summary>
    public static bool IsKnownGameVariantCategory(int category) => GameVariantCategories.ContainsKey(category);

    /// <summary>
    /// "Diamond 3", "Onyx 1487", "Unranked".
    ///
    /// Onyx is the one tier with no sub-tiers -- above it the number itself is the rank --
    /// so it is formatted with the raw CSR instead. Everywhere else SubTier gets its
    /// off-by-one corrected.
    /// </summary>
    public static string FormatRank(HaloCsr? csr)
    {
        if (csr is null || string.IsNullOrEmpty(csr.Tier))
        {
            return "Unranked";
        }

        if (csr.MeasurementMatchesRemaining > 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Placement: {csr.MeasurementMatchesRemaining} to go");
        }

        return csr.Tier.Equals("Onyx", StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"Onyx {csr.Value}")
            : string.Create(CultureInfo.InvariantCulture, $"{csr.Tier} {csr.SubTier + 1}");
    }
}
