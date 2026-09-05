using System.Globalization;
using Eet.Halo.Client.Mapping;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client;

/// <summary>
/// Halo Infinite's implementation of <see cref="ICareerSource"/>: everything the shared
/// dashboard needs, in one payload.
///
/// The shape of the answer is driven by the two things this tracker is trying to beat the
/// existing ones on.
///
///   "How am I doing" in three seconds -- the headline row is a career rate with an arrow
///   comparing the last 25 matches against the 25 before them. Not against a lifetime
///   average, which no amount of recent play can move, and so which always reads "steady"
///   and tells nobody anything.
///
///   Trends that are true -- every series goes through <see cref="Trends.Build"/>, which
///   weights each day by how many matches produced it and refuses to call a direction it
///   cannot distinguish from noise. A tracker that draws a confident upward line through
///   six evenings of variance is worse than one that draws nothing.
/// </summary>
public sealed class HaloCareerSource : ICareerSource
{
    /// <summary>Matches compared against the preceding equal-sized stretch for the headline arrows.</summary>
    private const int FormWindow = 25;

    /// <summary>A map or mode needs this many games before it is worth ranking.</summary>
    private const int MinimumSamplesForBreakdown = 3;

    private readonly HaloClient _client;
    private readonly IHaloPlayerDirectory _directory;
    private readonly HaloOptions _options;
    private readonly ILogger<HaloCareerSource> _logger;

    public HaloCareerSource(
        HaloClient client,
        IHaloPlayerDirectory directory,
        HaloOptions options,
        ILogger<HaloCareerSource>? logger = null)
    {
        _client = client;
        _directory = directory;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HaloCareerSource>.Instance;
    }

    public GameId Game => GameId.HaloInfinite;

    public bool IsFixture => _client.IsFixture;

    public Task<Player?> ResolveAsync(string query, CancellationToken ct = default) =>
        _directory.ResolveAsync(query, ct);

    /// <summary>
    /// Just the matches, newest first, for callers that want the list rather than the whole
    /// dashboard payload. Same fetch and same mapping as
    /// <see cref="GetSnapshotAsync"/> -- it is the cheaper prefix of it, not a second
    /// implementation.
    /// </summary>
    public async Task<IReadOnlyList<MatchSummary>> GetMatchesAsync(
        Player player,
        int count,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var warnings = new List<string>();
        var history = await _client
            .GetRecentMatchesAsync(player.Id, Math.Clamp(count, 1, _options.MatchesToAnalyse), ct)
            .ConfigureAwait(false);

        var details = await LoadMatchDetailAsync(history, player.Id, ct).ConfigureAwait(false);
        var assetNames = await ResolveAssetNamesAsync(details, warnings, ct).ConfigureAwait(false);

        return details
            .Select(d => HaloMapper.ToMatchSummary(d.Stats, player.Id, assetNames, d.Skill))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderByDescending(m => m.PlayedAt)
            .ToList();
    }

    public async Task<CareerSnapshot> GetSnapshotAsync(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var warnings = new List<string>();
        if (IsFixture)
        {
            warnings.Add(
                "Serving SYNTHETIC fixtures, not live data. No Xbox Live credentials are configured, so every number below is invented -- realistic in shape, but not anybody's real career.");
        }

        var history = await _client
            .GetRecentMatchesAsync(player.Id, _options.MatchesToAnalyse, ct)
            .ConfigureAwait(false);

        if (history.Count == 0)
        {
            warnings.Add(
                "No matchmade games found for this player. Either they have not played Halo Infinite, or their match history is set to private -- that setting is theirs and no tracker can work around it.");
            return Empty(player, warnings);
        }

        var details = await LoadMatchDetailAsync(history, player.Id, ct).ConfigureAwait(false);
        var assetNames = await ResolveAssetNamesAsync(details, warnings, ct).ConfigureAwait(false);

        var matches = details
            .Select(d => HaloMapper.ToMatchSummary(d.Stats, player.Id, assetNames, d.Skill))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderByDescending(m => m.PlayedAt)
            .ToList();

        if (matches.Count == 0)
        {
            warnings.Add(
                "Match history listed games but none of their stats documents could be read. Match stats are written asynchronously after a game ends, so very recent matches can legitimately 404 for a while.");
            return Empty(player, warnings);
        }

        if (matches.Count < history.Count)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{history.Count - matches.Count} of {history.Count} matches could not be read in full and are excluded from every figure below."));
        }

        var lifetime = await _client.GetMatchCountAsync(player.Id, ct).ConfigureAwait(false);
        var (totals, totalsSource) = await BuildTotalsAsync(player, matches, warnings, ct).ConfigureAwait(false);
        var csr = await LoadCurrentCsrAsync(player, matches, warnings, ct).ConfigureAwait(false);

        NoteUnknownModes(details, warnings);
        if (lifetime is not null && lifetime.MatchmadeMatchesPlayedCount > matches.Count)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Analysing the most recent {matches.Count} matchmade games of {lifetime.MatchmadeMatchesPlayedCount} played. Trends and headline figures describe that window, not the whole career."));
        }

        return new CareerSnapshot(
            Player: player,
            Game: GameId.HaloInfinite,
            GeneratedAt: DateTimeOffset.UtcNow,
            IsFixture: IsFixture,
            Source: string.Create(CultureInfo.InvariantCulture, $"{_client.SourceDescription}; totals from {totalsSource}"),
            Headline: BuildHeadline(matches, csr, lifetime),
            Trends: BuildTrends(matches),
            Recent: matches.Take(Math.Min(matches.Count, 50)).ToList(),
            Breakdowns: BuildBreakdowns(matches),
            Totals: totals,
            Warnings: warnings);
    }

    // ---------------------------------------------------------------- fetching

    private sealed record MatchDetail(
        HaloMatchHistoryResult Listing,
        HaloMatchStatsResponse? Stats,
        HaloMatchSkillResult? Skill);

    /// <summary>
    /// Pull the stats document, and where possible the skill document, for every listed
    /// match.
    ///
    /// Bounded concurrency is applied here as well as in the HTTP handler because the
    /// fixture transport has no handler chain -- without it, a 120-match fixture load
    /// would spawn 240 simultaneous tasks to read files. Same politeness, one layer up.
    /// </summary>
    private async Task<IReadOnlyList<MatchDetail>> LoadMatchDetailAsync(
        IReadOnlyList<HaloMatchHistoryResult> history,
        string xuid,
        CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
        var tasks = history.Select(async listing =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var stats = await _client.GetMatchStatsAsync(listing.MatchId, ct).ConfigureAwait(false);
                HaloMatchSkillResult? skill = null;
                try
                {
                    skill = await _client.GetMatchSkillAsync(listing.MatchId, xuid, ct).ConfigureAwait(false);
                }
                catch (TrackerException ex)
                {
                    // The skill service is clearance-aware. Losing it costs the CSR series
                    // and nothing else, so it must never cost the whole snapshot.
                    _logger.LogDebug(ex, "Skill lookup failed for {MatchId}.", listing.MatchId);
                }

                return new MatchDetail(listing, stats, skill);
            }
            finally
            {
                slots.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Put names on the map and game-variant GUIDs.
    ///
    /// Every one of these lookups is clearance-aware and cached forever, so the cost is
    /// paid once per distinct asset and never again. Failure is survivable: a breakdown row
    /// labelled "Map 8420410b" is worse than one labelled "Live Fire" but still groups and
    /// ranks correctly.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveAssetNamesAsync(
        IReadOnlyList<MatchDetail> details,
        List<string> warnings,
        CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_options.ResolveAssetNames)
        {
            return names;
        }

        var wanted = new Dictionary<string, (HaloAssetRef Asset, HaloAssetKind Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in details.Select(d => d.Stats?.MatchInfo).Where(i => i is not null).Select(i => i!))
        {
            Want(info.MapVariant, HaloAssetKind.Map);
            Want(info.UgcGameVariant, HaloAssetKind.GameVariant);
            Want(info.Playlist, HaloAssetKind.Playlist);
        }

        if (wanted.Count == 0)
        {
            return names;
        }

        using var slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
        var lookups = wanted.Values.Select(async entry =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return (entry.Asset.AssetId!, Name: await _client
                    .GetAssetNameAsync(entry.Asset, entry.Kind, ct)
                    .ConfigureAwait(false));
            }
            finally
            {
                slots.Release();
            }
        });

        foreach (var (assetId, name) in await Task.WhenAll(lookups).ConfigureAwait(false))
        {
            if (name is not null)
            {
                names[assetId.ToLowerInvariant()] = name;
            }
        }

        if (names.Count == 0)
        {
            warnings.Add(
                "Map and mode names could not be resolved, so breakdowns show asset ids. The UGC discovery service is clearance-aware; without a valid 343-clearance value it refuses every request. Match history and stats are unaffected -- those endpoints do not use clearance.");
        }

        return names;

        void Want(HaloAssetRef? asset, HaloAssetKind kind)
        {
            if (asset?.AssetId is not null && (asset.VersionId is not null || kind == HaloAssetKind.Playlist))
            {
                wanted.TryAdd(asset.AssetId, (asset, kind));
            }
        }
    }

    private async Task<(CareerTotals Totals, string Source)> BuildTotalsAsync(
        Player player,
        IReadOnlyList<MatchSummary> matches,
        List<string> warnings,
        CancellationToken ct)
    {
        if (_options.UseServiceRecord)
        {
            var record = await _client.GetServiceRecordAsync(player.Id, ct: ct).ConfigureAwait(false);
            if (record is { CoreStats: { } core })
            {
                warnings.Add(
                    "Lifetime totals come from the Waypoint service record. That endpoint is not in 343's published manifest -- it is known only from traffic capture -- so it is the least trustworthy figure on this page. Everything else is derived from manifest endpoints.");

                return (
                    new CareerTotals(
                        Matches: record.MatchesCompleted,
                        Wins: record.Wins,
                        Losses: record.Losses,
                        TimePlayed: record.TimePlayed ?? Sum(matches),
                        Kills: core.Kills,
                        Deaths: core.Deaths,
                        Assists: core.Assists),
                    "the Waypoint service record (capture-derived, not in the manifest)");
            }

            warnings.Add(
                "The Waypoint service record was requested but did not answer, so lifetime totals are aggregated from the analysed matches instead. This is the documented fallback, not a failure.");
        }

        // Aggregating what we actually paged keeps the totals internally consistent: wins,
        // matches and the win rate derived from them all describe the same set of games.
        // Mixing a lifetime match count with a windowed win count is how a tracker ends up
        // reporting a 4% win rate.
        return (
            new CareerTotals(
                Matches: matches.Count,
                Wins: matches.Count(m => m.Won == true),
                Losses: matches.Count(m => m.Won == false),
                TimePlayed: Sum(matches),
                Kills: matches.Sum(m => m.Kills),
                Deaths: matches.Sum(m => m.Deaths),
                Assists: matches.Sum(m => m.Assists)),
            string.Create(CultureInfo.InvariantCulture, $"aggregation of the last {matches.Count} matches"));

        static TimeSpan Sum(IReadOnlyList<MatchSummary> all) =>
            all.Aggregate(TimeSpan.Zero, (total, m) => total + m.Duration);
    }

    private async Task<HaloCsr?> LoadCurrentCsrAsync(
        Player player,
        IReadOnlyList<MatchSummary> matches,
        List<string> warnings,
        CancellationToken ct)
    {
        if (_options.RankedPlaylistId is not { Length: > 0 } playlist)
        {
            return null;
        }

        try
        {
            var result = await _client.GetPlaylistCsrAsync(playlist, player.Id, ct).ConfigureAwait(false);
            if (result?.Current is not null)
            {
                return result.Current;
            }
        }
        catch (TrackerException ex)
        {
            _logger.LogDebug(ex, "Playlist CSR lookup failed.");
        }

        if (!matches.Any(m => m.Extra?.ContainsKey(HaloMetrics.Csr) == true))
        {
            warnings.Add(
                "No competitive rank available. The skill endpoints are the clearance-aware ones, so this is the first thing to disappear when the flight-configuration id goes stale -- and it disappears without affecting anything served from halostats.");
        }

        return null;
    }

    private static void NoteUnknownModes(IReadOnlyList<MatchDetail> details, List<string> warnings)
    {
        var unknown = details
            .Select(d => d.Stats?.MatchInfo?.GameVariantCategory)
            .Where(c => c is not null && !HaloEnums.IsKnownGameVariantCategory(c.Value))
            .Select(c => c!.Value)
            .Distinct()
            .Order()
            .ToList();

        if (unknown.Count > 0)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Game variant categories {string.Join(", ", unknown)} are not in this client's mode table, so those matches are grouped under a placeholder name. The table is community-derived -- 343 does not publish it -- and grows every season."));
        }
    }

    // ---------------------------------------------------------------- headline

    /// <summary>
    /// The top row: a career rate, and whether recent form is better or worse than the
    /// stretch before it.
    /// </summary>
    internal static IReadOnlyList<Kpi> BuildHeadline(
        IReadOnlyList<MatchSummary> matches,
        HaloCsr? csr,
        HaloMatchCountResponse? lifetime)
    {
        var kpis = new List<Kpi>();

        // K/D, accuracy and damage/min are aggregate rates: total over total, never the
        // mean of per-match ratios. Trends.Rate documents exactly why, and the difference
        // is not academic -- a 5-0 game and a 30-25 game are not equally informative about
        // a player's kill/death ratio, and averaging them says they are.
        kpis.Add(RateKpi(
            "kd", "K/D", matches, m => m.Kills, m => m.Deaths, Better.Higher, Format.Ratio,
            "Total kills over total deaths. The arrow compares the last 25 matches with the 25 before them."));

        // Win rate is the one headline figure where the mean of per-match values IS the
        // aggregate rate, so Trends.Window is the right tool for the arrow and is used
        // directly. Only its delta is taken, though: Window's Current describes the recent
        // window alone, and a row where one tile means "last 25 games" while its neighbours
        // mean "all 120" is a row nobody can read.
        var winRate = Trends.Rate(matches, m => m.Won == true ? 1 : 0, m => m.Won is null ? 0 : 1);
        var (_, winDelta) = Trends.Window(matches, WonAsNumber, FormWindow);
        kpis.Add(new Kpi(
            "winRate",
            "Win rate",
            winRate,
            Format.Percent(winRate),
            Better.Higher,
            winDelta,
            winDelta is null ? null : Format.Signed(winDelta.Value * 100, 1) + " pts",
            "Share of DECIDED matches won: ties and games left early are excluded from both sides rather than counted as losses. This is deliberately not the same as Totals.WinRate, which the shared model defines as wins over all matches and which therefore scores a tie as a loss."));

        kpis.Add(RateKpi(
            "accuracy", "Accuracy", matches,
            m => Extra(m, HaloMetrics.ShotsHit), m => Extra(m, HaloMetrics.ShotsFired),
            Better.Higher, v => Format.Percent(v),
            "Shots hit over shots fired across every match, not the average of per-match accuracies."));

        kpis.Add(RateKpi(
            "damagePerMinute", "Damage / min", matches,
            m => Extra(m, HaloMetrics.DamageDealt), m => m.Duration.TotalMinutes,
            Better.Higher, Format.Integer,
            "Damage dealt per minute actually spent in a match, so joining late or leaving early does not flatter it."));

        if (csr is not null)
        {
            kpis.Add(CsrKpi(csr, matches));
        }
        else if (LatestCsr(matches) is { } latest)
        {
            kpis.Add(CsrKpi(
                new HaloCsr((int)latest, 0, null, 0, 0, null, 0, 0, 0),
                matches));
        }

        // The differentiator. The skill service publishes what it expected a player of this
        // rank to do in this match; the gap between that and what they did separates
        // "played well" from "drew easy opponents". No mainstream tracker shows it.
        if (matches.Any(m => m.Extra?.ContainsKey(HaloMetrics.KillsVsExpected) == true))
        {
            // Mean over every match that has skill data, for the same reason the win rate
            // above is: the headline row has to describe one consistent span.
            var mean = Trends.Rate(
                matches,
                m => Extra(m, HaloMetrics.KillsVsExpected),
                m => m.Extra?.ContainsKey(HaloMetrics.KillsVsExpected) == true ? 1 : 0);
            var (_, delta) = Trends.Window(matches, m => Optional(m, HaloMetrics.KillsVsExpected), FormWindow);
            kpis.Add(new Kpi(
                "killsVsExpected",
                "Kills vs expected",
                mean,
                Format.Signed(mean, 1),
                Better.Higher,
                delta,
                delta is null ? null : Format.Signed(delta.Value, 1),
                "Kills above what 343's skill service predicted for a player of this rank against these opponents. Positive means outperforming the matchmaker's expectation."));
        }

        var played = lifetime?.MatchmadeMatchesPlayedCount ?? matches.Count;
        kpis.Add(new Kpi(
            "matches",
            "Matches",
            played,
            Format.Integer(played),
            Better.Neutral,
            Note: lifetime is null
                ? "Matches analysed."
                : string.Create(CultureInfo.InvariantCulture, $"Matchmade games played. {matches.Count} most recent analysed in detail.")));

        var time = matches.Aggregate(TimeSpan.Zero, (total, m) => total + m.Duration);
        kpis.Add(new Kpi(
            "timePlayed",
            "Time analysed",
            time.TotalHours,
            Format.Hours(time),
            Better.Neutral,
            Note: string.Create(CultureInfo.InvariantCulture, $"Across {matches.Count} matches.")));

        return kpis;
    }

    private static Kpi CsrKpi(HaloCsr csr, IReadOnlyList<MatchSummary> matches)
    {
        // The honest delta for a rank is "how much has it moved", so this walks back a
        // window of matches rather than averaging -- a mean CSR is not a number anybody
        // wants.
        double? delta = null;
        var series = matches
            .Where(m => m.Extra?.ContainsKey(HaloMetrics.Csr) == true)
            .Select(m => m.Extra![HaloMetrics.Csr])
            .ToList();

        if (series.Count > 1)
        {
            var back = Math.Min(FormWindow, series.Count - 1);
            delta = series[0] - series[back];
        }

        var value = series.Count > 0 ? series[0] : csr.Value;
        return new Kpi(
            "csr",
            "Rank",
            value,
            csr.Tier is null ? Format.Integer(value) : HaloEnums.FormatRank(csr),
            Better.Higher,
            delta,
            delta is null ? null : Format.Signed(delta.Value, 0),
            "Competitive Skill Rating. Comes from the clearance-aware skill service, so it is absent rather than stale when clearance is unavailable.");
    }

    private static Kpi RateKpi(
        string key,
        string label,
        IReadOnlyList<MatchSummary> matches,
        Func<MatchSummary, double> numerator,
        Func<MatchSummary, double> denominator,
        Better better,
        Func<double, string> format,
        string note)
    {
        var career = Trends.Rate(matches, numerator, denominator);
        var delta = RateDelta(matches, numerator, denominator);

        return new Kpi(
            key,
            label,
            career,
            format(career),
            better,
            delta,
            delta is null ? null : SignedWith(delta.Value, format),
            note);
    }

    /// <summary>
    /// A delta with its sign, rendered by the metric's own formatter.
    /// </summary>
    /// <remarks>
    /// <see cref="Format.Signed"/> cannot be used here because each metric carries its own
    /// unit -- "45.0%", "799", "1.34" -- and the sign has to sit in front of whichever of
    /// those the caller chose. Getting this wrong is not cosmetic: a K/D that fell by 0.24
    /// rendered as "0.24" reads as a gain of 0.24, and the dashboard prints
    /// DeltaFormatted verbatim.
    /// </remarks>
    internal static string SignedWith(double delta, Func<double, string> format) =>
        (delta > 0 ? "+" : delta < 0 ? "-" : string.Empty) + format(Math.Abs(delta));

    /// <summary>
    /// Recent form against the immediately preceding stretch, computed as two aggregate
    /// rates rather than as a difference of means.
    /// </summary>
    /// <remarks>
    /// This is deliberately not <see cref="Trends.Window"/>. Window can only average a
    /// per-match selector, and for a ratio metric that is precisely the mistake
    /// <see cref="Trends.Rate"/> warns against -- so using it for the K/D arrow would put a
    /// delta on the headline that no arithmetic on the headline's own value reproduces.
    /// Window's guard rails are kept: the same window size, and the same refusal to claim a
    /// comparison against fewer than a third of a window.
    /// </remarks>
    internal static double? RateDelta(
        IReadOnlyList<MatchSummary> matchesNewestFirst,
        Func<MatchSummary, double> numerator,
        Func<MatchSummary, double> denominator,
        int window = FormWindow)
    {
        var recent = matchesNewestFirst.Take(window).ToList();
        var prior = matchesNewestFirst.Skip(window).Take(window).ToList();

        if (recent.Count == 0 || prior.Count < Math.Max(3, window / 3))
        {
            return null;
        }

        return Trends.Rate(recent, numerator, denominator) - Trends.Rate(prior, numerator, denominator);
    }

    // ---------------------------------------------------------------- trends

    /// <summary>
    /// The trend series.
    ///
    /// Every selector here returns a PER-MATCH value, which is the other half of the
    /// distinction Trends.cs draws: career figures are aggregate rates, trends are made of
    /// per-match observations that <see cref="Trends.ByDay"/> then collapses into
    /// sample-weighted daily points. Feeding an aggregate rate into a trend would produce a
    /// line that cannot move, because every point would be computed from the same totals.
    /// </summary>
    private static IReadOnlyList<TrendSeries> BuildTrends(IReadOnlyList<MatchSummary> matches)
    {
        var series = new List<TrendSeries>
        {
            Trends.Build("kd", "K/D", "ratio", Better.Higher, matches, m => m.Kd),
            Trends.Build("winRate", "Win rate", "fraction", Better.Higher, matches, WonAsNumber),
            Trends.Build("accuracy", "Accuracy", "fraction", Better.Higher, matches, m => m.Accuracy),
            Trends.Build("damagePerMinute", "Damage / min", "damage", Better.Higher, matches,
                m => Optional(m, HaloMetrics.DamagePerMinute)),
            Trends.Build("kda", "KDA", "ratio", Better.Higher, matches, m => m.Kda),
        };

        if (matches.Any(m => m.Extra?.ContainsKey(HaloMetrics.Csr) == true))
        {
            series.Add(Trends.Build("csr", "CSR", "rating", Better.Higher, matches,
                m => Optional(m, HaloMetrics.Csr)));
        }

        if (matches.Any(m => m.Extra?.ContainsKey(HaloMetrics.KillsVsExpected) == true))
        {
            series.Add(Trends.Build("killsVsExpected", "Kills vs expected", "kills", Better.Higher, matches,
                m => Optional(m, HaloMetrics.KillsVsExpected)));
        }

        // Drop anything with no data rather than shipping an empty chart to the dashboard.
        return series.Where(s => s.Points.Count > 0).ToList();
    }

    // ---------------------------------------------------------------- breakdowns

    private static IReadOnlyList<Breakdown> BuildBreakdowns(IReadOnlyList<MatchSummary> matches)
    {
        var breakdowns = new List<Breakdown>();
        var total = matches.Count;

        var byMap = matches
            .GroupBy(m => m.Map, StringComparer.Ordinal)
            .Where(g => g.Count() >= MinimumSamplesForBreakdown)
            .Select(g => new
            {
                Name = g.Key,
                Kd = Trends.Rate(g, m => m.Kills, m => m.Deaths),
                Count = g.Count(),
            })
            .OrderByDescending(x => x.Kd)
            .ToList();

        if (byMap.Count > 0)
        {
            breakdowns.Add(new Breakdown(
                "mapBest",
                "Strongest maps",
                "K/D",
                byMap.Take(6).Select(x => Row(x.Name, x.Kd, Format.Ratio(x.Kd), x.Count, total)).ToList()));

            breakdowns.Add(new Breakdown(
                "mapWorst",
                "Toughest maps",
                "K/D",
                byMap.AsEnumerable().Reverse().Take(6)
                    .Select(x => Row(x.Name, x.Kd, Format.Ratio(x.Kd), x.Count, total)).ToList()));
        }

        var byMode = matches
            .GroupBy(m => m.Mode, StringComparer.Ordinal)
            .Where(g => g.Count() >= MinimumSamplesForBreakdown)
            .Select(g =>
            {
                var decided = g.Count(m => m.Won is not null);
                var rate = decided == 0 ? 0 : (double)g.Count(m => m.Won == true) / decided;
                return new { Name = g.Key, Rate = rate, Count = g.Count() };
            })
            .OrderByDescending(x => x.Rate)
            .ToList();

        if (byMode.Count > 0)
        {
            breakdowns.Add(new Breakdown(
                "modeForm",
                "Form by mode",
                "Win rate",
                byMode.Select(x => Row(x.Name, x.Rate, Format.Percent(x.Rate, 0), x.Count, total)).ToList()));
        }

        // Time of day, which is the kind of thing a career page should tell you and none of
        // them do. UTC because that is the only clock the API gives us -- the match
        // timestamps carry no player-local offset, and inventing one would be worse than
        // labelling it honestly.
        var byHour = matches
            .GroupBy(m => m.PlayedAt.UtcDateTime.Hour / 4)
            .Where(g => g.Count() >= MinimumSamplesForBreakdown)
            .Select(g => new
            {
                Block = g.Key,
                Kd = Trends.Rate(g, m => m.Kills, m => m.Deaths),
                Count = g.Count(),
            })
            .OrderBy(x => x.Block)
            .ToList();

        if (byHour.Count > 1)
        {
            breakdowns.Add(new Breakdown(
                "timeOfDay",
                "Form by time of day (UTC)",
                "K/D",
                byHour.Select(x => Row(
                    string.Create(CultureInfo.InvariantCulture, $"{x.Block * 4:00}:00-{(x.Block * 4) + 3:00}:59"),
                    x.Kd,
                    Format.Ratio(x.Kd),
                    x.Count,
                    total)).ToList()));
        }

        return breakdowns;
    }

    private static BreakdownRow Row(string name, double value, string formatted, int samples, int total) =>
        new(name, value, formatted, samples, total == 0 ? null : (double)samples / total);

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A match as 1, 0 or nothing. Null for ties and abandoned games, so
    /// <see cref="Trends.ByDay"/> and <see cref="Trends.Window"/> both drop them instead of
    /// scoring them as losses.
    /// </summary>
    private static double? WonAsNumber(MatchSummary match) =>
        match.Won is null ? null : match.Won.Value ? 1 : 0;

    private static double Extra(MatchSummary match, string key) =>
        match.Extra is not null && match.Extra.TryGetValue(key, out var value) ? value : 0;

    private static double? Optional(MatchSummary match, string key) =>
        match.Extra is not null && match.Extra.TryGetValue(key, out var value) ? value : null;

    private static double? LatestCsr(IReadOnlyList<MatchSummary> matches) =>
        matches.Select(m => Optional(m, HaloMetrics.Csr)).FirstOrDefault(v => v is not null);

    private CareerSnapshot Empty(Player player, IReadOnlyList<string> warnings) =>
        new(
            player,
            GameId.HaloInfinite,
            DateTimeOffset.UtcNow,
            IsFixture,
            _client.SourceDescription,
            [],
            [],
            [],
            [],
            new CareerTotals(0, 0, 0, TimeSpan.Zero, 0, 0, 0),
            warnings);
}
