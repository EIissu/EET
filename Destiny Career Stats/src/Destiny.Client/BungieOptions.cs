using System.Globalization;

namespace Eet.Destiny.Client;

/// <summary>
/// Everything the Bungie client needs to know, and nothing it can leak.
///
/// <see cref="ApiKey"/> is the only secret here. It is never logged, never written to a
/// cache file, and never round-tripped into a response -- <see cref="Describe"/> exists so
/// diagnostics can say whether a key is present without saying what it is.
/// </summary>
public sealed class BungieOptions
{
    /// <summary>The Bungie.net platform root. Every documented endpoint hangs off this.</summary>
    public string PlatformBaseUrl { get; set; } = "https://www.bungie.net/Platform/";

    /// <summary>
    /// Where manifest definition tables and icons live. Deliberately separate from
    /// <see cref="PlatformBaseUrl"/>: <c>jsonWorldComponentContentPaths</c> gives site-root
    /// relative paths such as <c>/common/destiny2_content/json/en/...</c>, which are served
    /// off the CDN and need no API key.
    /// </summary>
    public string ContentBaseUrl { get; set; } = "https://www.bungie.net/";

    /// <summary>
    /// The free key from https://www.bungie.net/en/Application. Public career data needs
    /// only this -- no OAuth. Null or empty means fixture mode.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Manifest locale. Definition tables are published per locale.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>
    /// Directory for the manifest definition cache. Keyed by manifest version underneath,
    /// so a version bump lands in a new folder and the old one is pruned.
    /// </summary>
    public string CacheDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eet-trackers",
            "destiny");

    /// <summary>Where the recorded fixtures live. Only consulted in fixture mode.</summary>
    public string? FixtureDirectory { get; set; }

    /// <summary>
    /// Rows per activity-history request. Bungie caps this at 250; going higher is silently
    /// clamped server-side, which makes the "did I reach the end" test unreliable.
    /// </summary>
    public int ActivityPageSize { get; set; } = 200;

    /// <summary>
    /// How many pages of history to pull per character before stopping. Three pages of 200
    /// is roughly a year of heavy play, which is more than any trend chart needs.
    /// </summary>
    public int MaxActivityPages { get; set; } = 3;

    /// <summary>Cap on matches carried into the snapshot, newest first.</summary>
    public int MaxMatches { get; set; } = 400;

    /// <summary>
    /// Profile components requested alongside the career data. Records and Metrics are
    /// requested because the brief names them; nothing maps them yet, because rendering
    /// either one needs definition tables this client deliberately does not download.
    /// </summary>
    public string ProfileComponents { get; set; } = "Profiles,Characters,Records,Metrics";

    /// <summary>
    /// How many times to wait out a throttle response before giving up. Bungie answers
    /// HTTP 200 with a throttle ErrorCode and a ThrottleSeconds hint; this honours it.
    /// </summary>
    public int ThrottleRetries { get; set; } = 2;

    /// <summary>Ceiling on an honoured ThrottleSeconds, so a bad hint cannot hang a request.</summary>
    public TimeSpan MaxThrottleWait { get; set; } = TimeSpan.FromSeconds(30);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>A one-line, secret-free summary safe to log or return from /api/health.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"mode={(HasApiKey ? "live" : "fixture")} locale={Locale} pageSize={ActivityPageSize} maxPages={MaxActivityPages}");
}
