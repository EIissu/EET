using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Mvc;

namespace Eet.Halo.Api;

/// <summary>
/// JSON conventions for everything this API emits.
///
/// Two deliberate departures from the defaults, both aimed at the dashboard that has to
/// consume this:
///
///   * TimeSpan is written as a number of seconds. The framework default is
///     "00:12:34.5670000", which every JavaScript consumer then has to parse by hand and
///     half of them get wrong.
///
///   * Enums are written as names. "Higher" tells a chart which way is good;
///     <c>0</c> does not.
///
/// Everything numeric is already culture-invariant -- System.Text.Json always writes
/// invariant numbers -- and every pre-formatted string in the payload went through
/// <see cref="Format"/>, which pins InvariantCulture explicitly. A K/D is "1.42" for every
/// reader on earth.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TimeSpanSecondsConverter());
        return options;
    }
}

/// <summary>Writes a <see cref="TimeSpan"/> as seconds, and reads either seconds or the constant format.</summary>
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

/// <summary>
/// RFC 7807 responses.
///
/// The contract with the caller is that <c>detail</c> always says what to do about it. A
/// <see cref="TrackerException"/> already carries exactly that in its Remedy, which is the
/// whole reason the shared model has the property, so mapping one to the other is the point
/// of this class. The message goes in <c>title</c> and the remedy is repeated in a
/// <c>remedy</c> extension so a UI can style it separately from the failure itself.
/// </summary>
public static class ApiProblems
{
    public const string RemedyExtension = "remedy";

    /// <summary>
    /// The RFC 9110 section that defines each status. That is what `type` is for -- a
    /// pointer to what the status means, not a link to the RFC that defines the envelope.
    /// </summary>
    private static readonly Dictionary<int, string> StatusTypes = new()
    {
        [StatusCodes.Status400BadRequest] = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        [StatusCodes.Status404NotFound] = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        [StatusCodes.Status500InternalServerError] = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        [StatusCodes.Status502BadGateway] = "https://tools.ietf.org/html/rfc9110#section-15.6.3",
    };

    public static ProblemDetails From(Exception? error, string instance)
    {
        if (error is TrackerException tracker)
        {
            return Build(
                StatusCodes.Status502BadGateway,
                tracker.Message,
                tracker.Remedy ?? "No specific remedy is known for this failure.",
                instance);
        }

        if (error is OperationCanceledException)
        {
            return Build(
                StatusCodes.Status499ClientClosedRequest,
                "The request was cancelled.",
                "The client went away before the answer was ready. Nothing to fix.",
                instance);
        }

        return Build(
            StatusCodes.Status500InternalServerError,
            "The tracker hit an unexpected error.",
            "This is a bug rather than a configuration problem. The server log has the stack trace.",
            instance);
    }

    public static IResult BadRequest(string title, string remedy) =>
        Results.Problem(Build(StatusCodes.Status400BadRequest, title, remedy, instance: null));

    public static IResult NotFound(string title, string remedy) =>
        Results.Problem(Build(StatusCodes.Status404NotFound, title, remedy, instance: null));

    /// <summary>
    /// The 404 for a path under /api that no route claims.
    ///
    /// It exists because the single-page fallback would otherwise answer this with
    /// index.html and HTTP 200. A caller that asked for JSON must get JSON, and a status
    /// that means what it says, however wrong the path was.
    /// </summary>
    public static ProblemDetails UnknownApiRoute(string path) => Build(
        StatusCodes.Status404NotFound,
        $"No API route matches \"{path}\".",
        "This tracker serves GET /api/health, /api/player?q=, /api/career?player= and "
        + "/api/matches?player=. Everything outside /api is answered with the single-page "
        + "app instead, which is why this is JSON rather than HTML.",
        path);

    private static ProblemDetails Build(int status, string title, string remedy, string? instance)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = remedy,
            Instance = instance,
            Type = StatusTypes.GetValueOrDefault(status),
        };
        problem.Extensions[RemedyExtension] = remedy;
        return problem;
    }
}

/// <summary>
/// Finds the dashboard directory another agent is building, without requiring it to exist.
/// </summary>
public static class StaticAssets
{
    public static string? Locate(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured))
        {
            return Directory.Exists(configured) ? configured : null;
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

/// <summary>
/// Reports whether anything that looks like Xbox Live configuration is present, without
/// ever reading a secret's value.
///
/// This exists to close a specific gap: the tracker silently serving fixtures to somebody
/// who has gone to the trouble of configuring credentials, and who then spends an afternoon
/// wondering why their K/D is not theirs. If configuration is present but no
/// <see cref="IXboxAuth"/> is registered, say so in plain words at /api/health.
/// </summary>
public static class CredentialHints
{
    private static readonly string[] Keys =
    [
        "Xbox:ClientId",
        "Xbox:ClientSecret",
        "Xbox:RefreshToken",
        "XBOX_CLIENT_ID",
        "XBOX_CLIENT_SECRET",
        "XBOX_REFRESH_TOKEN",
        // The names Eet.Xbox's XboxOptions.FromEnvironment actually reads. Without these
        // this whole class watched for variables nobody sets, and reported "no configuration
        // found" to somebody who had configured everything correctly -- the precise
        // confusion it exists to prevent.
        "EET_XBOX_CLIENT_ID",
        "EET_XBOX_TENANT",
        "EET_XBOX_TOKEN_CACHE",
    ];

    public static object Detect(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Only ever the names, never the values. Nothing in this method can leak a secret
        // into a log or an HTTP response.
        var present = Keys.Where(k => !string.IsNullOrWhiteSpace(configuration[k])).ToArray();

        return new
        {
            configuredKeys = present,
            note = present.Length == 0
                ? "No Xbox Live configuration found, so the tracker is serving fixtures. That is the supported zero-credential mode, not a failure."
                : "Xbox Live configuration is present. It will only be used once an IXboxAuth implementation is registered in Program.cs -- see the comment there. Until then the tracker still serves fixtures.",
        };
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
/// There are two, and the choice is not a preference: the React app in
/// <c>Career Stats Web/dist</c> is the product, and the vanilla dashboard in
/// <c>Career Stats Shared/web</c> is the fallback that works when nobody has run npm. So
/// the built app wins when it is present and the vanilla one answers when it is not.
///
/// "Present" means an index.html, not merely a directory. A <c>dist</c> that exists because
/// Vite once wrote a sourcemap into it, or because someone made the folder by hand, would
/// otherwise take precedence over a working dashboard and serve nothing but 404s.
/// </summary>
public static class WebAssets
{
    public const string SpaDirectory = "Career Stats Web/dist";
    public const string VanillaDirectory = "Career Stats Shared/web";

    public static WebAssetChoice? Choose(string? spa, string? vanilla, string contentRoot)
    {
        return Try(spa, WebAssetKind.Spa) ?? Try(vanilla, WebAssetKind.Vanilla);

        WebAssetChoice? Try(string? configured, WebAssetKind kind)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            var root = StaticAssets.Locate(configured, contentRoot);
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
/// HTML, JSON.parse throws "Unexpected token &lt;", and nothing in that sentence mentions
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
/// In development the React app is served from :5173 by Vite while the API runs on its own
/// port. The dev proxy in vite.config.ts normally makes that same-origin, but it is one
/// line of config away from being bypassed, and a developer who does bypass it should get a
/// working app rather than an opaque CORS failure.
///
/// In production there is no CORS at all: the API serves the built app itself, so every
/// request is same-origin and any cross-origin caller is either a mistake or somebody
/// else's site spending this operator's Xbox credentials. AllowAnyOrigin would hand that
/// out to anyone who asked.
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
/// with a copy and paste and that no identifier ever contains. A gamertag copied out of a
/// web page arrives wrapped in whitespace and, often enough, a zero-width space or a
/// left-to-right mark; searched verbatim it matches nothing and the failure looks like the
/// player not existing.
///
/// The <c>xuid(...)</c> wrapper is unwrapped for the same reason. It is the form the Halo
/// services use and the form this API prints back at you, so it is exactly what somebody
/// copies out of one response and pastes into the next box.
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
        // was the right call. "xuid(2814669301245176)" is an id. A gamertag that happens to
        // read "xuid(hello)" is a gamertag, and mangling it into "hello" would be worse than
        // leaving it alone.
        var bare = Identity.BareXuid(text).Trim();
        return bare.Length > 0 && bare.Length != text.Length && bare.All(char.IsAsciiDigit)
            ? bare
            : text;
    }
}
