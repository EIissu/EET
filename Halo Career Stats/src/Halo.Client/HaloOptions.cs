namespace Eet.Halo.Client;

/// <summary>
/// Everything configurable about the Halo side of the tracker.
///
/// Note what is not here: no keys, no tokens, no secrets of any kind. Credentials reach
/// this client only through <see cref="Eet.Trackers.Core.IXboxAuth"/>, whose implementation
/// sources them from the environment. If a value in this class ever looks like a secret,
/// something has gone wrong.
/// </summary>
public sealed class HaloOptions
{
    public const string SectionName = "Halo";

    /// <summary>
    /// Where the raw-JSON fixtures live. Relative paths are probed against the content
    /// root and then walked up towards the repository root, so the app runs from anywhere.
    /// </summary>
    public string FixtureDirectory { get; set; } = "Career Stats Shared/fixtures";

    /// <summary>
    /// Force fixtures on even when credentials exist. Useful for demos and for developing
    /// the dashboard without burning somebody else's rate limit.
    /// </summary>
    public bool ForceFixtures { get; set; }

    /// <summary>
    /// On-disk response cache location. Null means a per-user default under
    /// LocalApplicationData. Set to empty to disable disk caching entirely.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>How long a match-history page stays fresh. Finished-match stats are cached forever.</summary>
    public TimeSpan HistoryCacheLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many requests may be in flight against 343 at once.
    ///
    /// This is an undocumented API and we are a fan tool on it. Four is enough to make
    /// pulling 120 match-stat documents pleasant and small enough that nobody notices us.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Attempts after the first. Backoff is exponential with jitter, and Retry-After always wins.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>First backoff step; doubles each attempt.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Ceiling on any single wait, including one a server-sent Retry-After asks for. A
    /// Retry-After of ten minutes should fail the request, not hang the dashboard.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How many recent matches to pull stats for. 120 is roughly a season of evenings.</summary>
    public int MatchesToAnalyse { get; set; } = 120;

    /// <summary>Page size for match history. 25 is what the game itself asks for.</summary>
    public int HistoryPageSize { get; set; } = 25;

    /// <summary>
    /// The sandbox and build the clearance (flight) lookup is made against.
    ///
    /// Provenance: the query template comes from the manifest
    /// (<c>?sandbox={sandbox}&amp;build={buildNumber}&amp;release=1.3</c>) but the values do
    /// not -- the manifest does not say what to put in them. "UNUSED" for sandbox is what
    /// retail clients send; the build number tracks the shipped game and goes stale. Both
    /// are overridable for exactly that reason.
    /// </summary>
    public string ClearanceSandbox { get; set; } = "UNUSED";

    /// <summary>See <see cref="ClearanceSandbox"/>. Expect to have to update this.</summary>
    public string ClearanceBuildNumber { get; set; } = "222249.22.06.08.1730-0";

    /// <summary>
    /// How long a successfully fetched flight-configuration id is reused before it is
    /// looked up again. The value is mutable -- 343 changes it when they reconfigure a
    /// build -- so a process that runs for days has to re-read it or it will start 401ing
    /// every clearance-aware request with no way to recover short of a restart.
    /// </summary>
    public TimeSpan ClearanceLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a FAILED flight lookup is remembered before trying again. Deliberately much
    /// shorter than <see cref="ClearanceLifetime"/>: caching the failure for as long as the
    /// value would turn one transient error into a process with no rank data at all.
    /// </summary>
    public TimeSpan ClearanceRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What this tool calls itself on every request to 343 and to Xbox Live.
    ///
    /// These are undocumented services and we are a guest on them, so identifying the
    /// client honestly is both good manners and the thing that lets 343 block this tool
    /// specifically rather than the whole IP range it shares. HttpClient sends no
    /// User-Agent at all by default, which is also the shape a great many bots have.
    ///
    /// Kept to a single product token on purpose. HttpClient parses User-Agent into
    /// products and comments, so anything richer arrives at the far end reassembled rather
    /// than verbatim; a name and a version say enough.
    /// </summary>
    public string UserAgent { get; set; } = "eet-halo-career-stats/1.0";

    /// <summary>
    /// The ranked playlist whose CSR is shown as a headline number. Defaults to the
    /// long-lived Ranked Arena playlist id.
    /// </summary>
    public string? RankedPlaylistId { get; set; } = "edfef3ac-9cbe-4fa2-b949-8f29deafd483";

    /// <summary>
    /// Ask the service record endpoint first and fall back to aggregating match history.
    /// Off by default: the endpoint is not in the manifest, so the safe default is the
    /// path whose provenance is strong.
    /// </summary>
    public bool UseServiceRecord { get; set; }

    /// <summary>
    /// Resolve map and game-variant asset GUIDs to human names via the UGC discovery
    /// service. Costs two extra clearance-aware requests per distinct asset, all cached
    /// forever.
    /// </summary>
    public bool ResolveAssetNames { get; set; } = true;
}
