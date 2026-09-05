using Eet.Trackers.Core;
using Xunit;

namespace Eet.Trackers.Core.Tests;

/// <summary>
/// The trend maths is the thing this tracker claims to do better than the others, so it
/// gets tested against known answers rather than against itself.
/// </summary>
public class TrendsTests
{
    private static readonly DateTimeOffset Day0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static MatchSummary Match(int dayOffset, int kills, int deaths, string id = "m") =>
        new(
            Id: $"{id}{dayOffset}-{kills}-{deaths}",
            Game: GameId.HaloInfinite,
            PlayedAt: Day0.AddDays(dayOffset),
            Duration: TimeSpan.FromMinutes(10),
            Mode: "Slayer",
            Map: "Streets",
            Playlist: "Quick Play",
            Won: kills >= deaths,
            Kills: kills,
            Deaths: deaths,
            Assists: 0,
            Accuracy: null,
            Score: null,
            Kda: null);

    // --- daily aggregation --------------------------------------------------------------

    [Fact]
    public void ByDay_averages_within_a_day_and_counts_samples()
    {
        var matches = new[] { Match(0, 10, 5), Match(0, 20, 5), Match(1, 6, 6) };

        var points = Trends.ByDay(matches, m => m.Kd);

        Assert.Equal(2, points.Count);
        Assert.Equal(2, points[0].Samples);
        Assert.Equal(3.0, points[0].Value, 6);   // mean of 2.0 and 4.0
        Assert.Equal(1, points[1].Samples);
        Assert.Equal(1.0, points[1].Value, 6);
    }

    [Fact]
    public void ByDay_is_ordered_oldest_first_regardless_of_input_order()
    {
        var points = Trends.ByDay(new[] { Match(5, 1, 1), Match(0, 1, 1), Match(3, 1, 1) }, m => m.Kd);
        Assert.Equal(new[] { 0, 3, 5 }.Select(d => DateOnly.FromDateTime(Day0.AddDays(d).UtcDateTime)),
                     points.Select(p => p.Date));
    }

    [Fact]
    public void ByDay_drops_values_that_are_not_finite()
    {
        // A selector can legitimately produce NaN (0/0 accuracy on a match with no shots).
        var points = Trends.ByDay(new[] { Match(0, 1, 1), Match(1, 1, 1) },
                                  m => m.Kills == 1 && m.PlayedAt == Day0 ? double.NaN : 2.0);
        Assert.Single(points);
        Assert.Equal(2.0, points[0].Value, 6);
    }

    // --- line fitting -------------------------------------------------------------------

    [Fact]
    public void FitLine_recovers_a_known_slope()
    {
        // y = 1.0 + 0.1x, one sample a day, no noise.
        var points = Enumerable.Range(0, 30)
            .Select(i => new TrendPoint(DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime), 1.0 + 0.1 * i, 1))
            .ToList();

        var fit = Trends.FitLine(points);

        Assert.Equal(0.1, fit.Slope, 6);
        Assert.Equal(1.0, fit.Intercept, 6);
    }

    [Fact]
    public void FitLine_is_weighted_by_sample_count()
    {
        // Two days say the value is 1; one day, backed by 100 matches, says it is 2.
        // An unweighted fit would follow the two lonely days. A weighted one must not.
        var points = new List<TrendPoint>
        {
            new(DateOnly.FromDateTime(Day0.UtcDateTime), 1.0, 1),
            new(DateOnly.FromDateTime(Day0.AddDays(1).UtcDateTime), 2.0, 100),
            new(DateOnly.FromDateTime(Day0.AddDays(2).UtcDateTime), 1.0, 1),
        };

        var fit = Trends.FitLine(points);

        // The heavy middle day dominates, so the fitted value near it is close to 2.
        var middle = fit.Intercept + fit.Slope * 1;
        Assert.True(middle > 1.7, $"weighted fit ignored sample counts; middle was {middle}");
    }

    [Fact]
    public void FitLine_reports_no_slope_when_everything_happened_on_one_day()
    {
        var day = DateOnly.FromDateTime(Day0.UtcDateTime);
        var fit = Trends.FitLine(new List<TrendPoint> { new(day, 1.0, 3), new(day, 2.0, 3) });
        Assert.Equal(0, fit.Slope);
    }

    [Fact]
    public void FitLine_handles_too_few_points_without_throwing()
    {
        Assert.Equal(0, Trends.FitLine(Array.Empty<TrendPoint>()).Slope);
        var one = Trends.FitLine(new[] { new TrendPoint(DateOnly.FromDateTime(Day0.UtcDateTime), 5, 1) });
        Assert.Equal(0, one.Slope);
        Assert.Equal(5, one.Intercept);
    }

    // --- significance -------------------------------------------------------------------

    [Fact]
    public void A_clean_upward_trend_is_significant()
    {
        var points = Enumerable.Range(0, 40)
            .Select(i => new TrendPoint(DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime), 1.0 + 0.02 * i, 5))
            .ToList();

        var fit = Trends.FitLine(points);
        Assert.True(fit.IsSignificant, $"t was {fit.T}");
        Assert.Equal("improving", Trends.Describe(fit, Better.Higher));
    }

    [Fact]
    public void Pure_noise_is_called_steady_not_a_trend()
    {
        // This is the property that separates an honest tracker from a horoscope: random
        // data must not produce a confident verdict.
        var random = new Random(20260905);
        var points = Enumerable.Range(0, 40)
            .Select(i => new TrendPoint(
                DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime),
                1.0 + (random.NextDouble() - 0.5) * 0.8,
                5))
            .ToList();

        var fit = Trends.FitLine(points);
        Assert.False(fit.IsSignificant, $"noise was reported as a trend, t={fit.T}");
        Assert.Equal("steady", Trends.Describe(fit, Better.Higher));
    }

    [Fact]
    public void Describe_respects_which_direction_is_good()
    {
        var rising = Trends.FitLine(Enumerable.Range(0, 30)
            .Select(i => new TrendPoint(DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime), 1.0 + 0.05 * i, 5))
            .ToList());

        Assert.Equal("improving", Trends.Describe(rising, Better.Higher));
        Assert.Equal("declining", Trends.Describe(rising, Better.Lower));
        Assert.Equal("rising", Trends.Describe(rising, Better.Neutral));
    }

    // --- smoothing ----------------------------------------------------------------------

    [Fact]
    public void Smooth_starts_at_the_first_value_and_tracks_the_series()
    {
        var points = Enumerable.Range(0, 20)
            .Select(i => new TrendPoint(DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime), i < 10 ? 1.0 : 2.0, 5))
            .ToList();

        var smoothed = Trends.Smooth(points);

        Assert.Equal(points.Count, smoothed.Count);
        Assert.Equal(1.0, smoothed[0], 6);
        Assert.True(smoothed[^1] > 1.5, "smoothed line never caught up to the step");
        Assert.True(smoothed[^1] <= 2.0, "smoothed line overshot the data");
    }

    [Fact]
    public void Smooth_never_leaves_the_range_of_the_data()
    {
        var random = new Random(7);
        var points = Enumerable.Range(0, 50)
            .Select(i => new TrendPoint(
                DateOnly.FromDateTime(Day0.AddDays(i).UtcDateTime),
                random.NextDouble() * 3,
                random.Next(1, 40)))
            .ToList();

        var smoothed = Trends.Smooth(points);
        var min = points.Min(p => p.Value);
        var max = points.Max(p => p.Value);

        Assert.All(smoothed, v => Assert.InRange(v, min - 1e-9, max + 1e-9));
    }

    // --- windows ------------------------------------------------------------------------

    [Fact]
    public void Window_reports_no_delta_when_there_is_no_prior_window()
    {
        var matches = Enumerable.Range(0, 5).Select(i => Match(-i, 10, 5)).ToList();
        var (current, delta) = Trends.Window(matches, m => m.Kd, window: 25);

        Assert.Equal(2.0, current, 6);
        Assert.Null(delta);   // "no baseline" must be distinguishable from "no change"
    }

    [Fact]
    public void Window_compares_recent_against_the_stretch_before_it()
    {
        // 10 recent matches at K/D 3, then 10 older at K/D 1. Newest first.
        var matches = Enumerable.Range(0, 10).Select(i => Match(-i, 30, 10, "new"))
            .Concat(Enumerable.Range(0, 10).Select(i => Match(-10 - i, 10, 10, "old")))
            .ToList();

        var (current, delta) = Trends.Window(matches, m => m.Kd, window: 10);

        Assert.Equal(3.0, current, 6);
        Assert.Equal(2.0, delta!.Value, 6);
    }

    // --- rates vs means -----------------------------------------------------------------

    [Fact]
    public void Rate_totals_the_parts_rather_than_averaging_the_ratios()
    {
        // A 5-0 game and a 10-20 game. Mean of per-match K/D is (5 + 0.5) / 2 = 2.75,
        // which is nonsense as a career figure. The true rate is 15/20 = 0.75.
        var matches = new[] { Match(0, 5, 0), Match(1, 10, 20) };

        var rate = Trends.Rate(matches, m => m.Kills, m => m.Deaths);
        var meanOfRatios = matches.Average(m => m.Kd);

        Assert.Equal(0.75, rate, 6);
        Assert.Equal(2.75, meanOfRatios, 6);
        Assert.NotEqual(rate, meanOfRatios, 3);
    }

    [Fact]
    public void Rate_does_not_divide_by_zero()
    {
        Assert.Equal(12, Trends.Rate(new[] { Match(0, 12, 0) }, m => m.Kills, m => m.Deaths), 6);
    }

    [Fact]
    public void Kd_treats_a_flawless_match_as_its_kill_count()
    {
        Assert.Equal(7, Match(0, 7, 0).Kd, 6);
        Assert.Equal(0, Match(0, 0, 0).Kd, 6);
    }
}
