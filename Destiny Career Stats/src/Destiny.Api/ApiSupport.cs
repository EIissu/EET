using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Trackers.Core;

namespace Eet.Destiny.Api;

/// <summary>
/// Writes a <see cref="TimeSpan"/> as a number of seconds, and reads either seconds or the
/// constant format back.
///
/// This is not a style preference. Both trackers share one career model, and
/// <c>CareerTotals.TimePlayed</c> and <c>MatchSummary.Duration</c> are TimeSpans in it. The
/// framework default writes those as "24.22:30:00", which is a string; the site's typed
/// contract, the Halo tracker, and this API's own documented conventions all say a duration
/// is a number of seconds. Two APIs behind one game switcher that disagree about that would
/// make every duration on the Destiny half render as NaN, or worse, as something plausible.
/// </summary>
public sealed class TimeSpanSecondsConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? TimeSpan.FromSeconds(reader.GetDouble())
            : TimeSpan.TryParse(reader.GetString(), CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : TimeSpan.Zero;

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(Math.Round(value.TotalSeconds, 3));
    }
}

/// <summary>Which of the two front ends is being served.</summary>
public enum WebAssetKind
{
    /// <summary>The built React single-page app from <c>Career Stats Web/dist</c>.</summary>
    Spa,

    /// <summary>The dependency-free dashboard from <c>Career Stats Shared/web</c>.</summary>
    Vanilla,
}

/// <summary>A resolved front end: where it lives, which one it is, and its entry document.</summary>
public sealed record WebAssetChoice(string Root, WebAssetKind Kind, string IndexPath)
{
    /// <summary>Wording for the startup log, so an operator knows which UI they got.</summary>
    public string Label => Kind == WebAssetKind.Spa
        ? "built React app"
        : "no-build vanilla dashboard";
}

/// <summary>
/// Picking a front end.
///
/// Ordered, not preferential: the built React app in <c>Career Stats Web/dist</c> is the
/// product, and the dependency-free dashboard in <c>Career Stats Shared/web</c> is what
/// answers when nobody has run npm. The vanilla one is not legacy; it is the zero-build
/// path and it has to keep working.
///
/// "Present" means an index.html, not merely a directory. An empty <c>dist</c> left behind
/// by a half-finished build would otherwise outrank a working dashboard and serve nothing.
/// </summary>
public static class WebAssets
{
    public const string SpaDirectory = "Career Stats Web/dist";
    public const string VanillaDirectory = "Career Stats Shared/web";

    public static WebAssetChoice? Choose(string? start = null)
    {
        return Try(SpaDirectory, WebAssetKind.Spa) ?? Try(VanillaDirectory, WebAssetKind.Vanilla);

        WebAssetChoice? Try(string relative, WebAssetKind kind)
        {
            var root = SharedWeb.Locate(relative, start);
            if (root is null)
            {
                return null;
            }

            var index = Path.Combine(root, "index.html");
            return File.Exists(index) ? new WebAssetChoice(root, kind, index) : null;
        }
    }
}

/// <summary>
/// The one line of routing policy that the single-page fallback must not get wrong.
///
/// Serving index.html for an unknown path is what makes a deep link work. Serving it for an
/// unknown <c>/api</c> path is a bug that costs an afternoon: fetch() gets 200 and a lump of
/// HTML, JSON.parse dies on "Unexpected token &lt;", and nothing in that sentence mentions
/// the URL being wrong. Unknown API routes stay JSON 404s.
/// </summary>
public static class ApiRoutes
{
    public const string Prefix = "/api";

    /// <summary>
    /// Segment-aware on purpose: <c>/api/nope</c> and <c>/api</c> are API paths,
    /// <c>/apiary</c> is a page in the app and must still deep-link.
    /// </summary>
    public static bool IsApi(PathString path) =>
        path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Cross-origin access for the Vite dev server, and for nothing else.
///
/// In development the React app is served from :5173 by Vite while this API runs on :5231.
/// The dev proxy in vite.config.ts normally makes that same-origin, but it is one config
/// change away from being bypassed, and a developer who bypasses it should get a working app
/// rather than an opaque browser error.
///
/// In production there is no CORS at all: the API serves the built app itself, so every
/// request is same-origin. AllowAnyOrigin would let any site on the internet run searches
/// through this operator's Bungie key, which is why it is not here.
/// </summary>
public static class DevCors
{
    public const string PolicyName = "career-stats-dev";

    public static IReadOnlyList<string> Origins { get; } =
    [
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    ];

    /// <summary>Read-only API, so a preflight never needs more than this.</summary>
    public static IReadOnlyList<string> Methods { get; } = ["GET", "HEAD", "OPTIONS"];

    public static IReadOnlyList<string> Headers { get; } = ["Accept", "Content-Type"];
}

/// <summary>
/// Cleaning up what a person actually typed, or pasted, into a search box.
///
/// None of this changes what matches: it removes the characters that ride along invisibly
/// with a copy and paste and that no identifier ever contains. A Bungie name copied out of a
/// web page arrives wrapped in whitespace and, often enough, a zero-width space; searched
/// verbatim it matches nothing, and the failure looks exactly like the player not existing.
///
/// The <c>xuid(...)</c> wrapper is unwrapped too. It belongs to the Halo half of the site,
/// but the site has one search box and a game switcher, so a Halo id pasted into the Destiny
/// box is an ordinary Tuesday. Unwrapped it fails as "no such membership id", which is
/// something a person can act on; left wrapped it fails as "that is not a Bungie name",
/// which sounds like the format was wrong rather than the game.
/// </summary>
public static class SearchQuery
{
    private static readonly char[] Invisible =
    [
        '\u200B', // zero width space
        '\u200C', // zero width non-joiner
        '\u200D', // zero width joiner
        '\u2060', // word joiner
        '\uFEFF', // zero width no-break space / BOM
        '\u200E', // left-to-right mark
        '\u200F', // right-to-left mark
    ];

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = raw;
        if (text.AsSpan().IndexOfAny(Invisible) >= 0)
        {
            text = string.Concat(text.Where(ch => Array.IndexOf(Invisible, ch) < 0));
        }

        text = text.Trim();

        // Identity.BareXuid owns the wrapper's shape; this only decides whether unwrapping
        // was the right call. A display name that happens to read "xuid(hello)" is a display
        // name, and mangling it into "hello" would be worse than leaving it alone.
        var bare = Identity.BareXuid(text).Trim();
        return bare.Length > 0 && bare.Length != text.Length && bare.All(char.IsAsciiDigit)
            ? bare
            : text;
    }
}
