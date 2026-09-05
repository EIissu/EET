using System.Globalization;
using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>Definitions with no manifest behind them, so mapping can be tested in isolation.</summary>
internal sealed class FakeDefinitions : IDestinyDefinitions
{
    public Dictionary<uint, (string Name, bool IsPvp)> Activities { get; } = new()
    {
        [100] = ("Rusted Lands", true),
        [101] = ("The Inverted Spire", false),
        [102] = ("Quickplay", true),
    };

    public Dictionary<int, (string Name, int Category)> Modes { get; } = new()
    {
        [10] = ("Control", 2),
        [3] = ("Strike", 1),
        [63] = ("Gambit", 3),
        [5] = ("All PvP", 2),
    };

    public string? Version => "fake";

    public bool IsLoaded => true;

    public string? ActivityName(uint hash) =>
        Activities.TryGetValue(hash, out var row) ? row.Name : null;

    public string? ActivityIconUrl(uint hash) => null;

    public bool? ActivityIsPvp(uint hash) =>
        Activities.TryGetValue(hash, out var row) ? row.IsPvp : null;

    public string ModeName(int modeType) =>
        Modes.TryGetValue(modeType, out var row) ? row.Name : DestinyActivityMode.Label(modeType);

    public int? ModeCategory(int modeType) =>
        Modes.TryGetValue(modeType, out var row) ? row.Category : DestinyActivityMode.Category(modeType);

    public bool IsAggregateMode(int modeType) => modeType is 5 or 7;
}

public sealed class MappingTests
{
    private static HistoricalStatsValue V(double value) => new()
    {
        Basic = new HistoricalStatsValuePair { Value = value, DisplayValue = value.ToString(CultureInfo.InvariantCulture) },
    };

    private static HistoricalStatsPeriodGroup Activity(
        uint reference = 100,
        int mode = 10,
        double kills = 12,
        double deaths = 8,
        double assists = 4,
        double? standing = 0,
        double duration = 600,
        string instance = "12000000001",
        DateTimeOffset? period = null,
        uint director = 102,
        int[]? modes = null)
    {
        var values = new Dictionary<string, HistoricalStatsValue>
        {
            ["kills"] = V(kills),
            ["deaths"] = V(deaths),
            ["assists"] = V(assists),
            ["activityDurationSeconds"] = V(duration),
            ["score"] = V(kills * 100),
            ["completed"] = V(1),
        };

        if (standing is not null)
        {
            values["standing"] = V(standing.Value);
        }

        return new HistoricalStatsPeriodGroup
        {
            Period = period ?? new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            ActivityDetails = new HistoricalStatsActivity
            {
                ReferenceId = reference,
                DirectorActivityHash = director,
                InstanceId = instance,
                Mode = mode,
                Modes = modes ?? [5, mode],
                MembershipType = 3,
            },
            Values = values,
        };
    }

    [Fact]
    public void A_crucible_activity_maps_to_a_match_with_a_result()
    {
        var match = DestinyMapper.ToMatch(Activity(), new FakeDefinitions());

        Assert.NotNull(match);
        Assert.Equal("12000000001", match.Id);
        Assert.Equal(GameId.Destiny2, match.Game);
        Assert.Equal("Rusted Lands", match.Map);
        Assert.Equal("Control", match.Mode);
        Assert.Equal("Quickplay", match.Playlist);
        Assert.Equal(TimeSpan.FromMinutes(10), match.Duration);
        Assert.True(match.Won);
        Assert.Equal(12, match.Kills);
        Assert.Equal(1.5, match.Kd);
    }

    [Fact]
    public void Standing_one_is_a_loss()
    {
        var match = DestinyMapper.ToMatch(Activity(standing: 1), new FakeDefinitions());

        Assert.False(match!.Won);
    }

    [Fact]
    public void A_pve_activity_has_no_winner()
    {
        // A strike is completed or abandoned, never lost. Recording a loss here is what turns
        // a career win rate into nonsense.
        var match = DestinyMapper.ToMatch(
            Activity(reference: 101, mode: 3, standing: 0), new FakeDefinitions());

        Assert.NotNull(match);
        Assert.Null(match.Won);
    }

    [Fact]
    public void Gambit_counts_as_decided_even_though_it_is_not_crucible()
    {
        var definitions = new FakeDefinitions();
        definitions.Activities[200] = ("Gambit", false);

        var match = DestinyMapper.ToMatch(
            Activity(reference: 200, mode: 63, standing: 1), definitions);

        Assert.False(match!.Won);
    }

    [Fact]
    public void The_most_specific_mode_wins_over_the_umbrella_one()
    {
        // activityDetails.mode is the umbrella 5 ("All PvP") and modes carries the specific
        // 10 ("Control"). "All PvP" tells a player nothing they did not already know.
        var match = DestinyMapper.ToMatch(Activity(mode: 5, modes: [5, 10]), new FakeDefinitions());

        Assert.Equal("Control", match!.Mode);
    }

    [Fact]
    public void A_deathless_game_counts_its_kills_rather_than_dividing_by_zero()
    {
        var match = DestinyMapper.ToMatch(Activity(kills: 15, deaths: 0), new FakeDefinitions());

        Assert.Equal(15, match!.Kd);
    }

    [Fact]
    public void An_unknown_activity_hash_still_produces_a_usable_match()
    {
        var match = DestinyMapper.ToMatch(Activity(reference: 999), new FakeDefinitions());

        Assert.NotNull(match);
        Assert.Contains("999", match.Map, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_kill_or_death_stats_is_dropped()
    {
        var group = Activity();
        group.Values!.Remove("kills");
        group.Values.Remove("deaths");

        Assert.Null(DestinyMapper.ToMatch(group, new FakeDefinitions()));
    }

    [Fact]
    public void Stat_lookup_survives_a_change_of_casing()
    {
        var values = new Dictionary<string, HistoricalStatsValue> { ["Kills"] = V(7) };

        Assert.Equal(7, DestinyMapper.Stat(values, "kills"));
    }

    [Fact]
    public void An_absurd_duration_is_dropped_rather_than_taking_down_the_career_page()
    {
        // TimeSpan.FromSeconds throws OverflowException rather than saturating, so a single
        // corrupt activityDurationSeconds used to fail the whole /api/career request with an
        // unhandled exception and an empty 500 body.
        var group = Activity();
        group.Values!["activityDurationSeconds"] = V(1e30);

        var match = DestinyMapper.ToMatch(group, new FakeDefinitions());

        Assert.NotNull(match);
        Assert.Equal(TimeSpan.Zero, match.Duration);
        // Everything else about the row still maps.
        Assert.Equal(12, match.Kills);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-60)]
    [InlineData(1e30)]
    public void A_seconds_stat_outside_any_plausible_range_is_no_duration_at_all(double seconds)
    {
        Assert.Equal(TimeSpan.Zero, DestinyMapper.Duration(seconds));
    }

    [Fact]
    public void A_plausible_seconds_stat_still_becomes_a_duration()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), DestinyMapper.Duration(600));
        // A veteran's lifetime: four thousand hours, and still a real number.
        Assert.Equal(TimeSpan.FromHours(4000), DestinyMapper.Duration(4000 * 3600));
    }

    [Fact]
    public void An_absurd_count_is_clamped_rather_than_wrapping_to_int_min_value()
    {
        // (int)1e30 is unchecked in C#: it lands on int.MinValue, and one of those in a match
        // list makes Enumerable.Sum -- which is checked -- throw far from the row that caused
        // it.
        Assert.Equal(int.MaxValue, DestinyMapper.Count(1e30));
        Assert.Equal(0, DestinyMapper.Count(-5));
        Assert.Equal(0, DestinyMapper.Count(double.NaN));
        Assert.Equal(0, DestinyMapper.Count(null));
        Assert.Equal(19, DestinyMapper.Count(19));
    }

    [Fact]
    public void Lifetime_totals_survive_a_corrupt_seconds_played()
    {
        var stats = new Dictionary<string, HistoricalStatsByPeriod>(StringComparer.OrdinalIgnoreCase)
        {
            ["allPvP"] = new()
            {
                AllTime = new Dictionary<string, HistoricalStatsValue>
                {
                    ["activitiesEntered"] = V(10),
                    ["activitiesWon"] = V(5),
                    ["secondsPlayed"] = V(1e30),
                    ["kills"] = V(100),
                    ["deaths"] = V(50),
                },
            },
        };

        var lifetime = DestinyMapper.ToLifetime(stats, []);

        Assert.Equal(TimeSpan.Zero, lifetime.Competitive.TimePlayed);
        Assert.Equal(10, lifetime.Competitive.Matches);
        Assert.Equal(2.0, lifetime.Competitive.Kd);
    }

    [Fact]
    public void Accuracy_is_never_invented_out_of_precision_kills()
    {
        // Destiny publishes no shots-fired or shots-hit stat anywhere. MatchSummary.Accuracy
        // is the column the Halo tracker fills with real accuracy, and the dashboard prints
        // both games into the same "Acc" heading, so a precision-kill share must not go
        // there. The raw count still travels, labelled as itself.
        var group = Activity();
        group.Values!["precisionKills"] = V(5);

        var match = DestinyMapper.ToMatch(group, new FakeDefinitions());

        Assert.NotNull(match);
        Assert.Null(match.Accuracy);
        Assert.Equal(5, match.Extra!["precisionKills"]);
    }

    // ------------------------------------------------------------------------------------

    [Fact]
    public void Lifetime_totals_keep_pve_out_of_the_win_rate()
    {
        // The bug this prevents: 1,544 wins over 5,125 activities reads as a 30% win rate for
        // a player who wins half their Crucible games, because 1,907 of those activities were
        // strikes with no winner.
        var stats = new Dictionary<string, HistoricalStatsByPeriod>(StringComparer.OrdinalIgnoreCase)
        {
            ["allPvP"] = new()
            {
                AllTime = new Dictionary<string, HistoricalStatsValue>
                {
                    ["activitiesEntered"] = V(3218),
                    ["activitiesWon"] = V(1544),
                    ["kills"] = V(41980),
                    ["deaths"] = V(37115),
                    ["assists"] = V(13402),
                    ["secondsPlayed"] = V(2154600),
                    ["precisionKills"] = V(13930),
                },
            },
            ["allPvE"] = new()
            {
                AllTime = new Dictionary<string, HistoricalStatsValue>
                {
                    ["activitiesEntered"] = V(1907),
                    ["kills"] = V(214880),
                    ["deaths"] = V(4188),
                    ["assists"] = V(9903),
                    ["secondsPlayed"] = V(3402900),
                    ["precisionKills"] = V(71110),
                },
            },
        };

        var lifetime = DestinyMapper.ToLifetime(stats, []);

        Assert.Equal(3218, lifetime.Competitive.Matches);
        Assert.Equal(1544, lifetime.Competitive.Wins);
        Assert.Equal(1674, lifetime.Competitive.Losses);
        Assert.Equal(0.48, lifetime.Competitive.WinRate, 2);
        Assert.Equal(1.13, lifetime.Competitive.Kd, 2);

        // PvE is kept, just kept separate.
        Assert.Equal(1907, lifetime.Pve.Matches);
        Assert.Equal(0, lifetime.Pve.Wins);
        Assert.Equal(5125, lifetime.TotalActivities);

        Assert.Equal(0.331, DestinyMapper.LifetimePrecisionRate(stats)!.Value, 3);
    }

    [Fact]
    public void With_no_all_time_stats_the_totals_fall_back_to_the_fetched_matches()
    {
        var matches = new List<MatchSummary>
        {
            DestinyMapper.ToMatch(Activity(instance: "1", standing: 0), new FakeDefinitions())!,
            DestinyMapper.ToMatch(Activity(instance: "2", standing: 1), new FakeDefinitions())!,
            DestinyMapper.ToMatch(Activity(instance: "3", reference: 101, mode: 3, standing: null), new FakeDefinitions())!,
        };

        var lifetime = DestinyMapper.ToLifetime(null, matches);

        Assert.Equal(2, lifetime.Competitive.Matches);
        Assert.Equal(1, lifetime.Competitive.Wins);
        Assert.Equal(1, lifetime.Pve.Matches);
    }

    // ------------------------------------------------------------------------------------

    [Fact]
    public void The_headline_kd_is_a_rate_not_the_mean_of_per_match_ratios()
    {
        // Trends.cs calls this out explicitly. A 5-0 game and a 30-25 game average to a K/D
        // of 3.10 if you mean the ratios, and to 1.40 if you divide the totals. Only the
        // second can be reproduced from the numbers on the page.
        var definitions = new FakeDefinitions();
        var matches = new List<MatchSummary>
        {
            DestinyMapper.ToMatch(Activity(instance: "1", kills: 5, deaths: 0), definitions)!,
            DestinyMapper.ToMatch(Activity(instance: "2", kills: 30, deaths: 25), definitions)!,
        };

        var lifetime = DestinyMapper.ToLifetime(null, matches);
        var kd = DestinyMapper.Headline(matches, lifetime, null).Single(k => k.Key == "kd");

        Assert.Equal(1.40, kd.Value, 2);
        Assert.Equal("1.40", kd.Formatted);
    }

    [Fact]
    public void The_rated_basis_drops_pve_when_there_is_enough_crucible_to_stand_on()
    {
        var definitions = new FakeDefinitions();
        var matches = Enumerable.Range(0, 8)
            .Select(i => DestinyMapper.ToMatch(
                Activity(instance: i.ToString(CultureInfo.InvariantCulture)), definitions)!)
            .Concat(Enumerable.Range(100, 3).Select(i => DestinyMapper.ToMatch(
                Activity(
                    instance: i.ToString(CultureInfo.InvariantCulture),
                    reference: 101,
                    mode: 3,
                    kills: 180,
                    deaths: 2,
                    standing: null),
                definitions)!))
            .ToList();

        var (basis, filtered) = DestinyMapper.RatedBasis(matches);

        Assert.True(filtered);
        Assert.Equal(8, basis.Count);
        Assert.All(basis, m => Assert.NotNull(m.Won));
    }

    [Fact]
    public void Below_five_decided_matches_nothing_is_filtered_out()
    {
        var definitions = new FakeDefinitions();
        var matches = new List<MatchSummary>
        {
            DestinyMapper.ToMatch(Activity(instance: "1"), definitions)!,
            DestinyMapper.ToMatch(Activity(instance: "2", reference: 101, mode: 3, standing: null), definitions)!,
        };

        var (basis, filtered) = DestinyMapper.RatedBasis(matches);

        Assert.False(filtered);
        Assert.Equal(2, basis.Count);
    }

    /// <summary>A repeating, zero-trend wobble, so the "flat" series has honest variance.</summary>
    private static readonly int[] Jitter = [3, -3, 0, 1, -1, 2, -2];

    [Fact]
    public void A_real_improvement_is_called_improving_and_a_flat_run_is_called_steady()
    {
        var definitions = new FakeDefinitions();
        var start = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);

        var rising = new List<MatchSummary>();
        var flat = new List<MatchSummary>();
        for (var day = 0; day < 60; day++)
        {
            for (var game = 0; game < 3; game++)
            {
                var instance = (day * 10 + game).ToString(CultureInfo.InvariantCulture);
                var period = start.AddDays(day).AddMinutes(game * 20);

                rising.Add(DestinyMapper.ToMatch(
                    Activity(instance: instance, period: period, kills: 8 + day * 0.2, deaths: 10),
                    definitions)!);

                // Real day-to-day noise, not a perfectly repeating pattern. A series whose
                // daily means are bit-for-bit identical produces a residual of about 1e-30,
                // and Trends.FitLine then reports a slope of 1e-16 as six standard errors --
                // "improving" from pure floating-point dust. See the report note on FitLine.
                flat.Add(DestinyMapper.ToMatch(
                    Activity(
                        instance: instance,
                        period: period,
                        kills: 10 + Jitter[(day * 3 + game) % Jitter.Length],
                        deaths: 10),
                    definitions)!);
            }
        }

        var risingKd = DestinyMapper.BuildTrends(rising).Single(t => t.Key == "kd");
        var flatKd = DestinyMapper.BuildTrends(flat).Single(t => t.Key == "kd");

        Assert.Equal("improving", risingKd.Direction);
        Assert.True(risingKd.SlopePerWeek > 0);
        // Trends.Describe refuses to call a direction it cannot support.
        Assert.Equal("steady", flatKd.Direction);
    }

    [Fact]
    public void Trend_labels_say_when_the_basis_was_filtered()
    {
        var definitions = new FakeDefinitions();
        var matches = new List<MatchSummary>
        {
            DestinyMapper.ToMatch(Activity(instance: "1"), definitions)!,
        };

        Assert.Contains(
            DestinyMapper.BuildTrends(matches, competitiveOnly: true),
            t => t.Key == "kd" && t.Label.Contains("competitive", StringComparison.Ordinal));

        Assert.Contains(
            DestinyMapper.BuildTrends(matches).Where(t => t.Key == "kd"),
            t => t.Label == "K/D");
    }

    [Fact]
    public void Breakdowns_rank_kd_over_the_competitive_matches_only()
    {
        // Otherwise "Best Modes" reads Dungeon 207.00, Nightfall 51.50, Trials 1.79, which is
        // a ranking of how many adds a mode spawns.
        var definitions = new FakeDefinitions();
        var crucible = Enumerable.Range(0, 6).Select(i => DestinyMapper.ToMatch(
            Activity(instance: "c" + i.ToString(CultureInfo.InvariantCulture)), definitions)!);
        var strikes = Enumerable.Range(0, 4).Select(i => DestinyMapper.ToMatch(
            Activity(
                instance: "s" + i.ToString(CultureInfo.InvariantCulture),
                reference: 101,
                mode: 3,
                kills: 180,
                deaths: 2,
                standing: null),
            definitions)!);

        var all = crucible.Concat(strikes).ToList();
        var (basis, _) = DestinyMapper.RatedBasis(all);

        var breakdowns = DestinyMapper.BuildBreakdowns(all, basis);

        var modes = breakdowns.Single(b => b.Key == "modes");
        Assert.DoesNotContain(modes.Rows, r => r.Name == "Strike");

        // Most Played still covers everything the player actually did.
        var activities = breakdowns.Single(b => b.Key == "activities");
        Assert.Contains(activities.Rows, r => r.Name == "The Inverted Spire");
    }

    [Fact]
    public void Every_formatted_number_uses_a_dot_for_the_decimal_point()
    {
        // "1,42" on a chart axis breaks the axis. The build pins InvariantGlobalization, and
        // this asserts the formatting does not rely on that alone.
        var definitions = new FakeDefinitions();
        var matches = Enumerable.Range(0, 6)
            .Select(i => DestinyMapper.ToMatch(
                Activity(instance: i.ToString(CultureInfo.InvariantCulture), kills: 14, deaths: 10),
                definitions)!)
            .ToList();

        var lifetime = DestinyMapper.ToLifetime(null, matches);
        var kpis = DestinyMapper.Headline(matches, lifetime, 0.331);

        Assert.Equal("1.40", kpis.Single(k => k.Key == "kd").Formatted);
        Assert.Equal("33.1%", kpis.Single(k => k.Key == "precision").Formatted);

        // A comma may only ever appear as a thousands separator, never as a decimal point.
        foreach (var kpi in kpis)
        {
            var comma = kpi.Formatted.IndexOf(',', StringComparison.Ordinal);
            if (comma >= 0)
            {
                var following = kpi.Formatted[(comma + 1)..];
                Assert.True(
                    following.Length >= 3 && following[..3].All(char.IsAsciiDigit),
                    $"'{kpi.Formatted}' looks like a comma decimal point, which breaks every chart axis.");
            }
        }

        Assert.Equal("1,234", Format.Integer(1234));
        Assert.Equal("1.42", Format.Ratio(1.42));
    }
}
