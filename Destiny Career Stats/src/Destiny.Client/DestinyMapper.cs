using System.Globalization;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// The stat ids Bungie uses inside a <c>values</c> dictionary. They are not published in
/// the OpenAPI document -- the authoritative list is whatever
/// <c>/Destiny2/Stats/Definition/</c> returns at runtime -- so every read of one is
/// tolerant of it being absent.
/// </summary>
public static class DestinyStat
{
    public const string Kills = "kills";
    public const string Deaths = "deaths";
    public const string Assists = "assists";
    public const string OpponentsDefeated = "opponentsDefeated";
    public const string Efficiency = "efficiency";
    public const string KillsDeathsRatio = "killsDeathsRatio";
    public const string KillsDeathsAssists = "killsDeathsAssists";
    public const string Score = "score";
    public const string TeamScore = "teamScore";
    public const string Standing = "standing";
    public const string Completed = "completed";
    public const string CompletionReason = "completionReason";
    public const string ActivityDurationSeconds = "activityDurationSeconds";
    public const string TimePlayedSeconds = "timePlayedSeconds";
    public const string PlayerCount = "playerCount";
    public const string PrecisionKills = "precisionKills";

    // All-time only.
    public const string ActivitiesEntered = "activitiesEntered";
    public const string ActivitiesWon = "activitiesWon";
    public const string SecondsPlayed = "secondsPlayed";
    public const string WinLossRatio = "winLossRatio";
}

/// <summary>
/// Bungie's shapes, turned into the shared career model.
///
/// The one decision worth calling out: career rates go through
/// <see cref="Trends.Rate"/> -- total kills over total deaths -- while trend points and
/// deltas use per-match values. Trends.cs explains why, and the difference is exactly the
/// bug that makes other trackers show a headline K/D that no arithmetic on the totals
/// reproduces.
/// </summary>
public static class DestinyMapper
{
    /// <summary>How many recent matches a headline delta compares against.</summary>
    public const int DeltaWindow = 25;

    /// <summary>A stat's raw value, or null when the game did not report it.</summary>
    public static double? Stat(IReadOnlyDictionary<string, HistoricalStatsValue>? values, string key)
    {
        if (values is null)
        {
            return null;
        }

        // Bungie's own casing is stable, but a dictionary deserialized from JSON is
        // case-sensitive and a single renamed key would silently zero a column.
        if (!values.TryGetValue(key, out var value))
        {
            foreach (var candidate in values)
            {
                if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    break;
                }
            }
        }

        var basic = value?.Basic?.Value;
        return basic is null || double.IsNaN(basic.Value) || double.IsInfinity(basic.Value) ? null : basic;
    }

    private static int StatInt(IReadOnlyDictionary<string, HistoricalStatsValue>? values, string key) =>
        Count(Stat(values, key));

    /// <summary>
    /// A count from a stat, with the range check the cast does not do.
    /// </summary>
    /// <remarks>
    /// <c>(int)</c> on a double outside int's range is unchecked in C#: it does not throw,
    /// it lands on <see cref="int.MinValue"/>. One of those in a match list then makes
    /// <see cref="Enumerable.Sum(IEnumerable{int})"/> -- which *is* checked -- throw an
    /// OverflowException a long way from the row that caused it. Clamping here keeps a
    /// single corrupt stat from taking down a whole career page.
    /// </remarks>
    public static int Count(double? value) =>
        value is not { } v || double.IsNaN(v)
            ? 0
            : (int)Math.Clamp(Math.Round(v), 0, int.MaxValue);

    /// <summary>
    /// Twenty years of continuous play. Past this a "seconds" stat is corrupt, not a career.
    /// </summary>
    private const double MaxReportedSeconds = 20 * 365.25 * 24 * 60 * 60;

    /// <summary>
    /// A seconds figure as a duration, with the range check
    /// <see cref="TimeSpan.FromSeconds(double)"/> does not survive.
    /// </summary>
    /// <remarks>
    /// <c>TimeSpan.FromSeconds</c> throws <see cref="OverflowException"/> rather than
    /// saturating, and one activity row carrying an absurd
    /// <c>activityDurationSeconds</c> is enough to fail the entire career request with an
    /// unhandled exception. Zero is what this mapper already reports for a duration Bungie
    /// did not send at all, so an unusable one is reported the same way rather than
    /// fabricating a century of playtime.
    /// </remarks>
    public static TimeSpan Duration(double? seconds) =>
        seconds is not { } value
        || double.IsNaN(value)
        || double.IsInfinity(value)
        || value <= 0
        || value > MaxReportedSeconds
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(value);

    /// <summary>
    /// One activity-history row as a normalised match, or null when the row carries no
    /// usable stats -- which happens for activities the player joined and immediately left.
    /// </summary>
    public static MatchSummary? ToMatch(HistoricalStatsPeriodGroup group, IDestinyDefinitions definitions)
    {
        var details = group.ActivityDetails;
        if (details is null)
        {
            return null;
        }

        var values = group.Values;
        if (Stat(values, DestinyStat.Kills) is null && Stat(values, DestinyStat.Deaths) is null)
        {
            return null;
        }

        var mode = DestinyActivityMode.MostSpecific(details.Mode, details.Modes, definitions);
        var category = definitions.ModeCategory(mode);
        var isPvp = definitions.ActivityIsPvp(details.ReferenceId) ?? category == 2;

        // Gambit is category 3, PvE Competitive: not Crucible, but it still has a winner.
        var decided = isPvp || category == 2 || category == 3;
        var standing = Stat(values, DestinyStat.Standing);
        bool? won = decided && standing is not null ? standing.Value < 0.5 : null;

        var kills = StatInt(values, DestinyStat.Kills);
        var deaths = StatInt(values, DestinyStat.Deaths);
        var assists = StatInt(values, DestinyStat.Assists);

        var map = definitions.ActivityName(details.ReferenceId)
            ?? string.Create(CultureInfo.InvariantCulture, $"Activity {details.ReferenceId}");

        // The playlist a player queued into is often a different definition from the
        // activity they landed in. Showing both is only useful when they differ.
        var playlist = details.DirectorActivityHash != 0 && details.DirectorActivityHash != details.ReferenceId
            ? definitions.ActivityName(details.DirectorActivityHash)
            : null;

        var duration = Duration(
            Stat(values, DestinyStat.ActivityDurationSeconds)
            ?? Stat(values, DestinyStat.TimePlayedSeconds));

        // Bungie publishes killsDeathsAssists as (kills + assists / 2) / deaths. Preferring
        // their number over a local recomputation keeps this tracker agreeing with the game.
        var kda = Stat(values, DestinyStat.KillsDeathsAssists)
            ?? (deaths == 0 ? kills + (assists / 2.0) : (kills + (assists / 2.0)) / deaths);

        var extra = new Dictionary<string, double>(StringComparer.Ordinal);
        AddIfPresent(extra, values, DestinyStat.Score);
        AddIfPresent(extra, values, DestinyStat.TeamScore);
        AddIfPresent(extra, values, DestinyStat.OpponentsDefeated);
        AddIfPresent(extra, values, DestinyStat.Efficiency);
        AddIfPresent(extra, values, DestinyStat.Completed);
        AddIfPresent(extra, values, DestinyStat.CompletionReason);
        AddIfPresent(extra, values, DestinyStat.PlayerCount);
        AddIfPresent(extra, values, DestinyStat.TimePlayedSeconds);
        AddIfPresent(extra, values, DestinyStat.PrecisionKills);
        extra["mode"] = mode;
        extra["referenceId"] = details.ReferenceId;

        // Accuracy stays null, always. Destiny publishes no shots-fired or shots-hit stat
        // anywhere, so there is no accuracy to report -- and the shared MatchSummary.Accuracy
        // is the same column the Halo tracker fills with real shots-hit-over-shots-fired.
        // Putting a precision-kill share there would print one quantity under another one's
        // heading, on the same table, next to rows where the heading is true. The precision
        // count still rides along in Extra above, labelled as itself.
        double? accuracy = null;

        return new MatchSummary(
            details.InstanceId ?? string.Empty,
            GameId.Destiny2,
            group.Period,
            duration,
            definitions.ModeName(mode),
            map,
            playlist,
            won,
            kills,
            deaths,
            assists,
            accuracy,
            Stat(values, DestinyStat.Score) is { } score ? Count(score) : null,
            kda,
            extra);
    }

    private static void AddIfPresent(
        Dictionary<string, double> target,
        IReadOnlyDictionary<string, HistoricalStatsValue>? values,
        string key)
    {
        if (Stat(values, key) is { } value)
        {
            target[key] = value;
        }
    }

    /// <summary>
    /// A career split in two, because Destiny is two games.
    ///
    /// <see cref="CareerTotals"/> derives <c>WinRate</c> as wins over matches and <c>Kd</c>
    /// as kills over deaths. Pour a PvE career into it and both come out meaningless: a
    /// player with 3,218 Crucible matches at 48% and 1,907 strikes reads as 30%, because
    /// strikes have no winner, and their K/D reads as 6.2, because a Nightfall hands out
    /// two hundred kills for two deaths. Neither number is one anybody would recognise.
    ///
    /// So <see cref="Competitive"/> is what fills the snapshot's Totals -- every derived
    /// property on it is then correct -- and <see cref="Pve"/> carries the rest, which the
    /// headline still uses for lifetime time played and activity count.
    /// </summary>
    public sealed record LifetimeStats(CareerTotals Competitive, CareerTotals Pve)
    {
        public TimeSpan TotalTimePlayed => Competitive.TimePlayed + Pve.TimePlayed;

        public int TotalActivities => Competitive.Matches + Pve.Matches;

        public int TotalKills => Competitive.Kills + Pve.Kills;
    }

    /// <summary>
    /// Lifetime totals, preferring Bungie's own all-time aggregates over anything derived
    /// from the fetched match window. Those aggregates cover a player's whole history; the
    /// window is at most a few hundred games.
    /// </summary>
    /// <param name="stats">The per-mode dictionary from GetHistoricalStats.</param>
    /// <param name="matches">Fallback, used only when the account has no all-time stats.</param>
    public static LifetimeStats ToLifetime(
        IReadOnlyDictionary<string, HistoricalStatsByPeriod>? stats,
        IReadOnlyList<MatchSummary> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var pvp = AllTime(stats, "allPvP");
        var pve = AllTime(stats, "allPvE");

        var pvpEntered = Count(Stat(pvp, DestinyStat.ActivitiesEntered));
        var pveEntered = Count(Stat(pve, DestinyStat.ActivitiesEntered));

        if (pvpEntered + pveEntered == 0)
        {
            // No all-time stats at all: a private stats component, or a brand new account.
            // The fetched window is a poor substitute, but it is honest about being one.
            return new LifetimeStats(
                FromMatches(matches.Where(m => m.Won is not null).ToList()),
                FromMatches(matches.Where(m => m.Won is null).ToList()));
        }

        var wins = Count(Stat(pvp, DestinyStat.ActivitiesWon));

        var competitive = new CareerTotals(
            pvpEntered,
            wins,
            Math.Max(0, pvpEntered - wins),
            Duration(Stat(pvp, DestinyStat.SecondsPlayed)),
            Count(Stat(pvp, DestinyStat.Kills)),
            Count(Stat(pvp, DestinyStat.Deaths)),
            Count(Stat(pvp, DestinyStat.Assists)));

        // PvE activities are completed or abandoned, never lost, so wins and losses stay at
        // zero here rather than being invented.
        var pveTotals = new CareerTotals(
            pveEntered,
            0,
            0,
            Duration(Stat(pve, DestinyStat.SecondsPlayed)),
            Count(Stat(pve, DestinyStat.Kills)),
            Count(Stat(pve, DestinyStat.Deaths)),
            Count(Stat(pve, DestinyStat.Assists)));

        return new LifetimeStats(competitive, pveTotals);
    }

    /// <summary>Lifetime precision-kill rate, or null when Bungie did not report it.</summary>
    public static double? LifetimePrecisionRate(IReadOnlyDictionary<string, HistoricalStatsByPeriod>? stats)
    {
        var pvp = AllTime(stats, "allPvP");
        var pve = AllTime(stats, "allPvE");

        var precision = (Stat(pvp, DestinyStat.PrecisionKills) ?? 0) + (Stat(pve, DestinyStat.PrecisionKills) ?? 0);
        var kills = (Stat(pvp, DestinyStat.Kills) ?? 0) + (Stat(pve, DestinyStat.Kills) ?? 0);

        return precision > 0 && kills > 0 ? precision / kills : null;
    }

    private static IReadOnlyDictionary<string, HistoricalStatsValue>? AllTime(
        IReadOnlyDictionary<string, HistoricalStatsByPeriod>? stats, string mode)
    {
        if (stats is null)
        {
            return null;
        }

        if (stats.TryGetValue(mode, out var byPeriod))
        {
            return byPeriod.AllTime;
        }

        foreach (var candidate in stats)
        {
            if (string.Equals(candidate.Key, mode, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Value.AllTime;
            }
        }

        return null;
    }

    private static CareerTotals FromMatches(IReadOnlyList<MatchSummary> matches) => new(
        matches.Count,
        matches.Count(m => m.Won == true),
        matches.Count(m => m.Won == false),
        matches.Aggregate(TimeSpan.Zero, (total, m) => total + m.Duration),
        matches.Sum(m => m.Kills),
        matches.Sum(m => m.Deaths),
        matches.Sum(m => m.Assists));

    // -----------------------------------------------------------------------------------
    // Headline
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Which matches a K/D should be computed over.
    ///
    /// Not all of them. A Nightfall with 140 kills and two deaths is a K/D of 70, and one of
    /// those in the window moves a headline number further than fifty Crucible games do. So
    /// the rated figures use matches that had an opponent -- Crucible and Gambit -- and the
    /// activity list and the breakdowns still show everything the player did.
    ///
    /// Below five such matches there is nothing to filter to, and showing a mixed number
    /// beats showing none.
    /// </summary>
    /// <returns>The basis, and whether it is the filtered one.</returns>
    public static (IReadOnlyList<MatchSummary> Matches, bool Filtered) RatedBasis(
        IReadOnlyList<MatchSummary> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        var decided = all.Where(m => m.Won is not null).ToList();
        return decided.Count >= 5 ? (decided, true) : (all, false);
    }

    /// <summary>
    /// The top row of the dashboard: the numbers that answer "how am I doing" without
    /// scrolling.
    /// </summary>
    /// <param name="matchesNewestFirst">The rated basis from <see cref="RatedBasis"/>, newest first.</param>
    /// <param name="totals">Lifetime totals, which cover far more than the fetched window.</param>
    /// <param name="lifetimePrecisionRate">Lifetime precision-kill share, or null.</param>
    /// <param name="competitiveOnly">Whether the basis was filtered to matches with a winner.</param>
    public static IReadOnlyList<Kpi> Headline(
        IReadOnlyList<MatchSummary> matchesNewestFirst,
        LifetimeStats lifetime,
        double? lifetimePrecisionRate,
        bool competitiveOnly = false)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(matchesNewestFirst);

        var kpis = new List<Kpi>();
        var window = matchesNewestFirst.Count;
        var note = window == 0
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Last {window} {(competitiveOnly ? "competitive matches" : "matches")}");

        if (window > 0)
        {
            // Career rate, not the mean of per-match ratios. See Trends.Rate.
            var kd = Trends.Rate(matchesNewestFirst, m => m.Kills, m => m.Deaths);
            var (_, kdDelta) = Trends.Window(matchesNewestFirst, m => m.Kd, DeltaWindow);
            kpis.Add(new Kpi(
                "kd", "K/D", kd, Format.Ratio(kd), Better.Higher,
                kdDelta, kdDelta is null ? null : Format.Signed(kdDelta.Value), note));

            var kda = Trends.Rate(matchesNewestFirst, m => m.Kills + (m.Assists / 2.0), m => m.Deaths);
            var (_, kdaDelta) = Trends.Window(matchesNewestFirst, m => m.Kda, DeltaWindow);
            kpis.Add(new Kpi(
                "kda", "K/D/A", kda, Format.Ratio(kda), Better.Higher,
                kdaDelta, kdaDelta is null ? null : Format.Signed(kdaDelta.Value),
                "Bungie's formula: (kills + assists / 2) / deaths"));

            var efficiency = Trends.Rate(matchesNewestFirst, m => m.Kills + m.Assists, m => m.Deaths);
            var (_, efficiencyDelta) = Trends.Window(matchesNewestFirst, Efficiency, DeltaWindow);
            kpis.Add(new Kpi(
                "efficiency", "Efficiency", efficiency, Format.Ratio(efficiency), Better.Higher,
                efficiencyDelta, efficiencyDelta is null ? null : Format.Signed(efficiencyDelta.Value),
                "(kills + assists) / deaths"));

            var decided = matchesNewestFirst.Where(m => m.Won is not null).ToList();
            if (decided.Count > 0)
            {
                var winRate = (double)decided.Count(m => m.Won == true) / decided.Count;
                var (_, winDelta) = Trends.Window(matchesNewestFirst, WinValue, DeltaWindow);
                kpis.Add(new Kpi(
                    "winrate", "Win Rate", winRate, Format.Percent(winRate), Better.Higher,
                    winDelta,
                    winDelta is null ? null : Format.Signed(winDelta.Value * 100, 1) + "pp",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Over {decided.Count} matches that had a winner. Percentage points, not percent.")));
            }
        }

        if (lifetimePrecisionRate is { } precision)
        {
            kpis.Add(new Kpi(
                "precision", "Precision", precision, Format.Percent(precision), Better.Higher,
                null, null,
                "Share of lifetime kills that were precision hits. Destiny publishes no "
                + "shots-fired stat, so there is no true accuracy figure to show."));
        }

        // These two are the only headline numbers that cover the whole account rather than
        // the competitive half, and they say so.
        kpis.Add(new Kpi(
            "timeplayed", "Time Played", lifetime.TotalTimePlayed.TotalHours,
            Format.Hours(lifetime.TotalTimePlayed), Better.Neutral, null, null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lifetime, every activity. {Format.Hours(lifetime.Competitive.TimePlayed)} of it in the Crucible.")));

        kpis.Add(new Kpi(
            "matches", "Matches", lifetime.TotalActivities, Format.Integer(lifetime.TotalActivities),
            Better.Neutral, null, null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lifetime activities entered, of which {Format.Integer(lifetime.Competitive.Matches)} had a winner.")));

        return kpis;
    }

    /// <summary>
    /// Destiny's own efficiency: (kills + assists) / deaths, with a deathless game counting
    /// as its own numerator rather than dividing by zero -- the same convention
    /// <see cref="MatchSummary.Kd"/> uses.
    /// </summary>
    /// <remarks>Nullable so it can be passed straight to the Trends selectors.</remarks>
    private static double? Efficiency(MatchSummary match) =>
        match.Deaths == 0 ? match.Kills + match.Assists : (match.Kills + match.Assists) / (double)match.Deaths;

    private static double? WinValue(MatchSummary match) =>
        match.Won is null ? null : match.Won.Value ? 1 : 0;

    // -----------------------------------------------------------------------------------
    // Trends
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The series behind the "am I actually getting better" half of the dashboard. Every one
    /// goes through <see cref="Trends.Build"/>, so every one is weighted by matches per day
    /// and refuses to call a direction it cannot support.
    /// </summary>
    public static IReadOnlyList<TrendSeries> BuildTrends(
        IReadOnlyList<MatchSummary> matches, bool competitiveOnly = false)
    {
        ArgumentNullException.ThrowIfNull(matches);

        // The suffix goes in the label rather than a footnote, because a chart axis is read
        // in isolation and "K/D" over a basis the reader has to go and look up is the kind
        // of quiet dishonesty this tracker exists to avoid.
        var suffix = competitiveOnly ? " (competitive)" : string.Empty;

        var series = new List<TrendSeries>
        {
            Trends.Build("kd", "K/D" + suffix, "ratio", Better.Higher, matches, m => m.Kd),
            Trends.Build("kda", "K/D/A" + suffix, "ratio", Better.Higher, matches, m => m.Kda),
            Trends.Build("efficiency", "Efficiency" + suffix, "ratio", Better.Higher, matches, Efficiency),
            // Fractions, not percentages: Format.Percent multiplies, and one convention
            // across every rate in this payload beats two.
            Trends.Build("winrate", "Win Rate", "%", Better.Higher, matches, WinValue),
            Trends.Build("kills", "Kills per Match" + suffix, "kills", Better.Higher, matches, m => m.Kills),
            Trends.Build("deaths", "Deaths per Match" + suffix, "deaths", Better.Lower, matches, m => m.Deaths),
        };

        if (matches.Any(m => m.Score is > 0))
        {
            series.Add(Trends.Build(
                "score", "Score per Match" + suffix, "points", Better.Higher, matches, m => m.Score));
        }

        return series;
    }

    // -----------------------------------------------------------------------------------
    // Breakdowns
    // -----------------------------------------------------------------------------------

    /// <summary>Ranked cuts: which modes go well, and where the time actually goes.</summary>
    /// <param name="matches">Everything fetched. "Most Played" covers all of it.</param>
    /// <param name="rated">
    /// The competitive basis from <see cref="RatedBasis"/>. The K/D ranking uses this, because
    /// a ranking that puts Dungeon at 207.00 above Trials at 1.79 is not a ranking of
    /// anything.
    /// </param>
    public static IReadOnlyList<Breakdown> BuildBreakdowns(
        IReadOnlyList<MatchSummary> matches, IReadOnlyList<MatchSummary>? rated = null)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var breakdowns = new List<Breakdown>();
        if (matches.Count == 0)
        {
            return breakdowns;
        }

        // Three matches is the floor for appearing in a ranking at all. Below that a single
        // good game tops the table, which is the exact failure Trends.cs is written against.
        const int MinimumSamples = 3;

        var ranked = rated is { Count: > 0 } ? rated : matches;
        var byMode = ranked.GroupBy(m => m.Mode, StringComparer.Ordinal)
            .Where(g => g.Count() >= MinimumSamples)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Kd = Trends.Rate(g, m => m.Kills, m => m.Deaths),
                Decided = g.Count(m => m.Won is not null),
                Wins = g.Count(m => m.Won == true),
            })
            .ToList();

        if (byMode.Count > 0)
        {
            breakdowns.Add(new Breakdown(
                "modes", "Best Modes", "K/D",
                byMode.OrderByDescending(m => m.Kd)
                    .Select(m => new BreakdownRow(
                        m.Name, m.Kd, Format.Ratio(m.Kd), m.Count, (double)m.Count / ranked.Count))
                    .ToList()));

            var decidedModes = byMode.Where(m => m.Decided >= MinimumSamples).ToList();
            if (decidedModes.Count > 0)
            {
                breakdowns.Add(new Breakdown(
                    "mode-winrate", "Win Rate by Mode", "Win Rate",
                    decidedModes
                        .Select(m => new { m.Name, m.Decided, Rate = (double)m.Wins / m.Decided })
                        .OrderByDescending(m => m.Rate)
                        .Select(m => new BreakdownRow(
                            m.Name, m.Rate, Format.Percent(m.Rate), m.Decided))
                        .ToList()));
            }
        }

        var byActivity = matches.GroupBy(m => m.Map, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g => new BreakdownRow(
                g.Key,
                g.Count(),
                Format.Integer(g.Count()),
                g.Count(),
                (double)g.Count() / matches.Count))
            .ToList();

        if (byActivity.Count > 0)
        {
            breakdowns.Add(new Breakdown("activities", "Most Played", "Matches", byActivity));
        }

        return breakdowns;
    }
}
