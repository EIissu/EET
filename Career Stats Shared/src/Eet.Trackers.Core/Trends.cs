namespace Eet.Trackers.Core;

/// <summary>
/// Turning a pile of matches into a defensible statement about whether someone is getting
/// better.
///
/// The bar most trackers clear is "plot the raw numbers and draw a straight line". That is
/// misleading in two specific ways, and both are fixed here:
///
///   * A day with two matches is plotted the same size as a day with forty, so a single
///     lucky game swings the trend line. Every aggregate below is weighted by how many
///     matches produced it.
///
///   * A slope is reported as "improving" regardless of whether it is distinguishable from
///     noise. <see cref="Fit"/> returns the standard error alongside the slope, and
///     <see cref="Describe"/> refuses to call a direction it cannot support.
/// </summary>
public static class Trends
{
    /// <summary>Smoothing factor for the displayed line. Roughly a two-week half-life.</summary>
    private const double DefaultAlpha = 0.25;

    /// <summary>
    /// How many standard errors a slope must clear before it is called a direction rather
    /// than noise. Two is the usual rule of thumb and corresponds to about 95% confidence.
    /// </summary>
    private const double SignificanceT = 2.0;

    /// <summary>Collapse matches into one weighted point per day, oldest first.</summary>
    public static IReadOnlyList<TrendPoint> ByDay(
        IEnumerable<MatchSummary> matches,
        Func<MatchSummary, double?> selector)
    {
        var buckets = new SortedDictionary<DateOnly, (double Sum, int Count)>();
        foreach (var match in matches)
        {
            var value = selector(match);
            if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                continue;
            }

            var day = DateOnly.FromDateTime(match.PlayedAt.UtcDateTime);
            var current = buckets.TryGetValue(day, out var existing) ? existing : (0d, 0);
            buckets[day] = (current.Item1 + value.Value, current.Item2 + 1);
        }

        return buckets
            .Select(b => new TrendPoint(b.Key, b.Value.Sum / b.Value.Count, b.Value.Count))
            .ToList();
    }

    /// <summary>
    /// Exponentially weighted moving average over the daily points. Days with more matches
    /// pull harder, which is what stops a two-game Tuesday from bending the line.
    /// </summary>
    public static IReadOnlyList<double> Smooth(
        IReadOnlyList<TrendPoint> points,
        double alpha = DefaultAlpha)
    {
        if (points.Count == 0)
        {
            return Array.Empty<double>();
        }

        var output = new double[points.Count];
        var level = points[0].Value;
        output[0] = level;

        for (var i = 1; i < points.Count; i++)
        {
            // Scale the responsiveness by sample count, capped so a huge day cannot make
            // the average jump straight to it and throw away all history.
            var weight = Math.Min(1.0, alpha * Math.Sqrt(points[i].Samples));
            level = weight * points[i].Value + (1 - weight) * level;
            output[i] = level;
        }

        return output;
    }

    /// <summary>The result of a weighted least-squares fit through the daily points.</summary>
    /// <param name="Slope">Change in the metric per day.</param>
    /// <param name="StandardError">
    /// Uncertainty in that slope. NaN when there are too few distinct days to estimate it.
    /// </param>
    public readonly record struct Fit(double Slope, double Intercept, double StandardError)
    {
        /// <summary>Slope in standard errors. The larger, the less likely it is noise.</summary>
        /// <remarks>
        /// Three cases, and conflating the last two is a bug worth naming. A NaN standard
        /// error means the uncertainty could not be estimated at all (fewer than three
        /// distinct days), so nothing can be claimed and t is zero. A standard error of
        /// exactly zero is the opposite situation: the points sit perfectly on the line,
        /// so the slope is certain and t is infinite. Only a positive standard error is
        /// the ordinary case.
        /// </remarks>
        public double T => double.IsNaN(StandardError)
            ? 0
            : StandardError > 0
                ? Slope / StandardError
                : Slope == 0 ? 0 : double.PositiveInfinity * Math.Sign(Slope);

        public bool IsSignificant => Math.Abs(T) >= SignificanceT;
    }

    /// <summary>
    /// Weighted least squares of value against day index, weighting each day by how many
    /// matches it contains.
    /// </summary>
    public static Fit FitLine(IReadOnlyList<TrendPoint> points)
    {
        if (points.Count < 2)
        {
            return new Fit(0, points.Count == 1 ? points[0].Value : 0, double.NaN);
        }

        var origin = points[0].Date;
        double sw = 0, swx = 0, swy = 0, swxy = 0, swxx = 0;

        foreach (var p in points)
        {
            double w = p.Samples;
            double x = (p.Date.ToDateTime(TimeOnly.MinValue) - origin.ToDateTime(TimeOnly.MinValue)).TotalDays;
            double y = p.Value;
            sw += w;
            swx += w * x;
            swy += w * y;
            swxy += w * x * y;
            swxx += w * x * x;
        }

        var denominator = sw * swxx - swx * swx;
        if (Math.Abs(denominator) < 1e-12)
        {
            // Every match happened on the same day; there is no time axis to fit.
            return new Fit(0, swy / sw, double.NaN);
        }

        var slope = (sw * swxy - swx * swy) / denominator;
        var intercept = (swy - slope * swx) / sw;

        // Residual spread, then the standard error of the slope itself.
        double residual = 0;
        foreach (var p in points)
        {
            double w = p.Samples;
            double x = (p.Date.ToDateTime(TimeOnly.MinValue) - origin.ToDateTime(TimeOnly.MinValue)).TotalDays;
            var predicted = intercept + slope * x;
            residual += w * Math.Pow(p.Value - predicted, 2);
        }

        var degreesOfFreedom = points.Count - 2;
        if (degreesOfFreedom <= 0)
        {
            // Two points define a line exactly; there is no spare information left to
            // estimate how wrong it might be.
            return new Fit(slope, intercept, double.NaN);
        }

        if (residual <= 0)
        {
            // Every point is on the line. That is not "unknown uncertainty", it is none.
            return new Fit(slope, intercept, 0);
        }

        var variance = residual / degreesOfFreedom;
        var standardError = Math.Sqrt(variance * sw / denominator);
        return new Fit(slope, intercept, standardError);
    }

    /// <summary>
    /// Put a word to the slope, and refuse to overclaim. A slope that is not significant is
    /// "steady", however much it looks like a line on a chart.
    /// </summary>
    public static string Describe(Fit fit, Better better)
    {
        if (fit.Slope == 0 || !fit.IsSignificant)
        {
            return "steady";
        }

        if (better == Better.Neutral)
        {
            return fit.Slope > 0 ? "rising" : "falling";
        }

        var good = better == Better.Higher ? fit.Slope > 0 : fit.Slope < 0;
        return good ? "improving" : "declining";
    }

    /// <summary>Build a complete series from matches in one call.</summary>
    public static TrendSeries Build(
        string key,
        string label,
        string unit,
        Better better,
        IEnumerable<MatchSummary> matches,
        Func<MatchSummary, double?> selector,
        double alpha = DefaultAlpha)
    {
        var points = ByDay(matches, selector);
        var fit = FitLine(points);
        return new TrendSeries(
            key,
            label,
            unit,
            better,
            points,
            Smooth(points, alpha),
            fit.Slope,
            fit.Slope * 7,
            Describe(fit, better));
    }

    /// <summary>
    /// Compare the most recent <paramref name="window"/> matches against the
    /// <paramref name="window"/> before them. This is what the arrows on the headline
    /// numbers mean: recent form against the immediately preceding stretch, not against a
    /// lifetime average that no amount of recent play can move.
    /// </summary>
    /// <returns>Current mean, and the delta against the prior window, or null if there is no prior window.</returns>
    public static (double Current, double? Delta) Window(
        IReadOnlyList<MatchSummary> matchesNewestFirst,
        Func<MatchSummary, double?> selector,
        int window = 25)
    {
        var values = matchesNewestFirst
            .Select(selector)
            .Where(v => v is not null && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
            .Select(v => v!.Value)
            .ToList();

        if (values.Count == 0)
        {
            return (0, null);
        }

        var recent = values.Take(window).ToList();
        var current = recent.Average();

        var prior = values.Skip(window).Take(window).ToList();
        // Require at least a third of a window before claiming a comparison; two matches is
        // not a baseline.
        if (prior.Count < Math.Max(3, window / 3))
        {
            return (current, null);
        }

        return (current, current - prior.Average());
    }

    /// <summary>
    /// Aggregate rate over a set of matches: total numerator over total denominator, not
    /// the mean of per-match ratios.
    /// </summary>
    /// <remarks>
    /// The distinction matters. Averaging per-match K/D gives a 5-0 game the same weight as
    /// a 30-25 game, which is why a tracker can show a "K/D" that no amount of arithmetic
    /// on the totals reproduces. Career figures should use this; per-match trends should
    /// not.
    /// </remarks>
    public static double Rate(
        IEnumerable<MatchSummary> matches,
        Func<MatchSummary, double> numerator,
        Func<MatchSummary, double> denominator)
    {
        double top = 0, bottom = 0;
        foreach (var match in matches)
        {
            top += numerator(match);
            bottom += denominator(match);
        }

        return bottom == 0 ? top : top / bottom;
    }
}
