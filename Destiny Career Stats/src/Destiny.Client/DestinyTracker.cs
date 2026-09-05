using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// Wires the client together and decides, once, whether this process is talking to
/// bungie.net or to the recorded fixtures.
///
/// The rule is deliberately blunt: an API key means live, no API key means fixtures. There
/// is no third state and no configuration flag to get wrong, because the thing that must
/// always work is starting with nothing configured at all.
/// </summary>
public sealed class DestinyTracker : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    private DestinyTracker(
        HttpClient http,
        bool ownsHttpClient,
        IBungieApi api,
        DestinyManifestCache definitions,
        DestinyCareerSource source,
        BungieOptions options,
        string? fixtureDirectory)
    {
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        Api = api;
        Definitions = definitions;
        Career = source;
        Options = options;
        FixtureDirectory = fixtureDirectory;
    }

    public IBungieApi Api { get; }

    public DestinyManifestCache Definitions { get; }

    public DestinyCareerSource Career { get; }

    public BungieOptions Options { get; }

    /// <summary>Where fixtures are being read from, or null in live mode.</summary>
    public string? FixtureDirectory { get; }

    public bool IsFixture => FixtureDirectory is not null;

    /// <summary>
    /// Build a tracker from options. Falls back to fixtures when there is no API key, and
    /// throws only when there is neither a key nor a fixture directory -- at which point
    /// there is genuinely nothing to serve.
    /// </summary>
    public static DestinyTracker Create(BungieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.HasApiKey)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(options.PlatformBaseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(60),
            };

            // Bungie asks applications to identify themselves; an anonymous client is the
            // first thing throttled when they are under load.
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "eet-destiny-career-stats/1.0 (+https://github.com/)");

            return Build(http, ownsHttpClient: true, options, fixtureDirectory: null);
        }

        var fixtures = options.FixtureDirectory ?? FixtureLocator.Find();
        if (fixtures is null)
        {
            throw new TrackerException(
                "No Bungie API key and no fixtures to fall back on.",
                "Either set BUNGIE_API_KEY (free, from https://www.bungie.net/en/Application) or "
                + "point Bungie:FixtureDirectory at Career Stats Shared/fixtures. The tracker is meant "
                + "to run with no credentials at all, so the fixture path is the normal one.");
        }

        var fixtureClient = new HttpClient(new FixtureMessageHandler(fixtures))
        {
            BaseAddress = new Uri(options.PlatformBaseUrl, UriKind.Absolute),
        };

        // A fixture manifest version is not a real one, and DestinyManifestCache prunes every
        // version directory but the one it just wrote. Without this line a fixture run and a
        // live run would take turns deleting each other's definition cache.
        options.CacheDirectory = Path.Combine(options.CacheDirectory, "fixtures");

        return Build(fixtureClient, ownsHttpClient: true, options, fixtures);
    }

    /// <summary>
    /// Build over a caller-supplied <see cref="HttpClient"/>. This is the seam tests use to
    /// hang a stub handler underneath the real client.
    /// </summary>
    public static DestinyTracker Create(HttpClient http, BungieOptions options, bool fixtureMode = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        return Build(http, ownsHttpClient: false, options, fixtureMode ? options.FixtureDirectory ?? "(stub)" : null);
    }

    private static DestinyTracker Build(
        HttpClient http, bool ownsHttpClient, BungieOptions options, string? fixtureDirectory)
    {
        IBungieApi api = fixtureDirectory is null
            ? new BungieApiClient(http, options)
            : new FixtureBungieApi(new BungieApiClient(http, options));

        var definitions = new DestinyManifestCache(api, options);
        var source = new DestinyCareerSource(api, definitions, options);
        return new DestinyTracker(http, ownsHttpClient, api, definitions, source, options, fixtureDirectory);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>
/// The live client, with one bit flipped.
///
/// It exists only so <see cref="ICareerSource.IsFixture"/> can be true while every request,
/// every envelope check and every line of mapping still runs through
/// <see cref="BungieApiClient"/>. Nothing else is overridden, and that is the point.
/// </summary>
internal sealed class FixtureBungieApi : IBungieApi
{
    private readonly IBungieApi _inner;

    public FixtureBungieApi(IBungieApi inner) => _inner = inner;

    public bool IsFixture => true;

    public Task<IReadOnlyList<UserInfoCard>> SearchByBungieNameAsync(
        string displayName, short displayNameCode, CancellationToken ct = default) =>
        _inner.SearchByBungieNameAsync(displayName, displayNameCode, ct);

    public Task<UserMembershipData?> GetMembershipsByIdAsync(
        string membershipId, int membershipType, CancellationToken ct = default) =>
        _inner.GetMembershipsByIdAsync(membershipId, membershipType, ct);

    public Task<DestinyProfileResponse> GetProfileAsync(
        int membershipType, string membershipId, string components, CancellationToken ct = default) =>
        _inner.GetProfileAsync(membershipType, membershipId, components, ct);

    public Task<IReadOnlyDictionary<string, HistoricalStatsByPeriod>> GetHistoricalStatsAsync(
        int membershipType, string membershipId, string characterId, string groups, string modes,
        CancellationToken ct = default) =>
        _inner.GetHistoricalStatsAsync(membershipType, membershipId, characterId, groups, modes, ct);

    public Task<IReadOnlyList<HistoricalStatsPeriodGroup>> GetActivityHistoryAsync(
        int membershipType, string membershipId, string characterId, int mode, int count, int page,
        CancellationToken ct = default) =>
        _inner.GetActivityHistoryAsync(membershipType, membershipId, characterId, mode, count, page, ct);

    public Task<PostGameCarnageReport?> GetPostGameCarnageReportAsync(
        string activityId, CancellationToken ct = default) =>
        _inner.GetPostGameCarnageReportAsync(activityId, ct);

    public Task<DestinyManifest> GetManifestAsync(CancellationToken ct = default) =>
        _inner.GetManifestAsync(ct);

    public Task<Stream> GetDefinitionTableAsync(string relativePath, CancellationToken ct = default) =>
        _inner.GetDefinitionTableAsync(relativePath, ct);
}
