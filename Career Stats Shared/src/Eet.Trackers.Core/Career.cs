using System.Globalization;

namespace Eet.Trackers.Core;

/// <summary>Which game a piece of career data came from.</summary>
public enum GameId
{
    HaloInfinite,
    Destiny2,
}

/// <summary>Whether a rising number is good news, bad news, or neither.</summary>
public enum Better
{
    Higher,
    Lower,
    Neutral,
}

/// <summary>
/// A player, in whichever namespace their game uses.
/// </summary>
/// <param name="Handle">What to print. A gamertag, or a Bungie name including its code.</param>
/// <param name="Id">
/// The stable identifier the API actually keys on: an XUID for Xbox, a Destiny membership
/// id for Bungie. Always prefer this over <paramref name="Handle"/> when calling an API --
/// display names are mutable and, as the Cyrillic-homoglyph case in
/// <see cref="Identity.LooksLikeHomoglyph"/> shows, not always typeable.
/// </param>
public sealed record Player(string Handle, string Id, string Platform, string? IconUrl = null);

/// <summary>
/// One headline number, of the kind that belongs in the top row of a dashboard.
/// </summary>
/// <param name="Delta">
/// Change against the comparison window, in the same unit as <paramref name="Value"/>.
/// Null when there is no earlier window to compare against, which is different from zero
/// and must render differently.
/// </param>
public sealed record Kpi(
    string Key,
    string Label,
    double Value,
    string Formatted,
    Better Better,
    double? Delta = null,
    string? DeltaFormatted = null,
    string? Note = null)
{
    /// <summary>
    /// Did this move in the direction the player wants? Null when there is no delta, or
    /// when the metric has no good direction (playtime is neither good nor bad).
    /// </summary>
    public bool? Improved => Delta is null || Better == Better.Neutral
        ? null
        : Better == Better.Higher ? Delta > 0 : Delta < 0;
}

/// <summary>One point on a trend line: a value, and how many matches produced it.</summary>
/// <param name="Samples">
/// Carried so the UI can de-emphasise a point computed from two matches. A K/D of 4.0 over
/// one game is noise; most trackers plot it identically to a K/D of 4.0 over fifty.
/// </param>
public sealed record TrendPoint(DateOnly Date, double Value, int Samples);

/// <summary>A metric over time, plus what it is doing.</summary>
public sealed record TrendSeries(
    string Key,
    string Label,
    string Unit,
    Better Better,
    IReadOnlyList<TrendPoint> Points,
    IReadOnlyList<double> Smoothed,
    double Slope,
    double SlopePerWeek,
    string Direction);

/// <summary>One match, normalised across both games.</summary>
public sealed record MatchSummary(
    string Id,
    GameId Game,
    DateTimeOffset PlayedAt,
    TimeSpan Duration,
    string Mode,
    string Map,
    string? Playlist,
    bool? Won,
    int Kills,
    int Deaths,
    int Assists,
    double? Accuracy,
    int? Score,
    double? Kda,
    IReadOnlyDictionary<string, double>? Extra = null)
{
    /// <summary>
    /// Kills over deaths, with a zero-death game counting as its kill total rather than
    /// dividing by zero. Both games' own UIs do the same thing.
    /// </summary>
    public double Kd => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

/// <summary>Everything the dashboard needs for one game, in one payload.</summary>
public sealed record CareerSnapshot(
    Player Player,
    GameId Game,
    DateTimeOffset GeneratedAt,
    bool IsFixture,
    string Source,
    IReadOnlyList<Kpi> Headline,
    IReadOnlyList<TrendSeries> Trends,
    IReadOnlyList<MatchSummary> Recent,
    IReadOnlyList<Breakdown> Breakdowns,
    CareerTotals Totals,
    IReadOnlyList<string> Warnings);

/// <summary>Lifetime numbers, the ones a service record shows.</summary>
public sealed record CareerTotals(
    int Matches,
    int Wins,
    int Losses,
    TimeSpan TimePlayed,
    int Kills,
    int Deaths,
    int Assists)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
    public double Kd => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

/// <summary>A ranked cut of the data -- best maps, most-used weapons, per-mode form.</summary>
public sealed record Breakdown(
    string Key,
    string Label,
    string ValueLabel,
    IReadOnlyList<BreakdownRow> Rows);

public sealed record BreakdownRow(
    string Name,
    double Value,
    string Formatted,
    int Samples,
    double? Share = null,
    string? IconUrl = null);

/// <summary>Formatting helpers, all culture-invariant on purpose.</summary>
public static class Format
{
    public static string Ratio(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Percent(double fraction, int decimals = 1) =>
        (fraction * 100).ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture) + "%";

    public static string Integer(double v) =>
        ((long)Math.Round(v)).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"142h 30m" -- the unit a career page actually wants.</summary>
    public static string Hours(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{t.Minutes}m";

    public static string Signed(double v, int decimals = 2)
    {
        var s = Math.Abs(v).ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture);
        return v > 0 ? "+" + s : v < 0 ? "-" + s : s;
    }
}
