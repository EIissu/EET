using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Endpoints;

/// <summary>
/// One authority from the manifest: the host that actually serves a family of endpoints.
/// </summary>
/// <param name="Scheme">
/// 343's own numeric scheme code. 2 is HTTPS, which is every authority we call. 8 is AMQP
/// and 9 is an internal alias; both are rejected rather than guessed at.
/// </param>
public sealed record HaloAuthority(string Id, int Scheme, string Hostname, int? Port)
{
    private const int SchemeHttps = 2;

    public bool IsHttps => Scheme == SchemeHttps;

    public Uri BaseUri => IsHttps
        ? new UriBuilder("https", Hostname, Port ?? 443).Uri
        : throw new TrackerException(
            $"Authority '{Id}' uses transport scheme {Scheme}, which is not HTTPS.",
            "This endpoint is not reachable over plain HTTP. Pick an endpoint whose authority has Scheme 2.");
}

/// <summary>
/// The retry shape 343's own client uses for an endpoint. We do not copy their aggression
/// (see linearretry404retry, which retries fourteen times), but the timeout and the
/// "is a 404 worth retrying" flag are genuinely useful signals.
/// </summary>
/// <param name="RetryIfNotFound">
/// True for the two endpoints that read a single match. It is not a mistake: match stats
/// are written asynchronously after a game ends, so a 404 straight after a match means
/// "not yet", not "never".
/// </param>
public sealed record HaloRetryPolicy(
    string Id,
    TimeSpan Timeout,
    int MaxRetryCount,
    TimeSpan RetryDelay,
    double RetryGrowth,
    TimeSpan RetryJitter,
    bool RetryIfNotFound);

/// <summary>
/// One endpoint, exactly as 343's settings service describes it.
/// </summary>
/// <param name="ClearanceAware">
/// Whether the request additionally needs a <c>343-clearance</c> header. This is the whole
/// reason the manifest is embedded rather than transcribed: the stats endpoints are not
/// clearance-aware, the skill and economy endpoints are, and sending clearance everywhere
/// (or nowhere) is the easiest way for a fan tool to get itself 401'd.
/// </param>
public sealed record HaloEndpoint(
    string Id,
    HaloAuthority Authority,
    string PathTemplate,
    string QueryTemplate,
    bool ClearanceAware,
    HaloRetryPolicy Retry);

/// <summary>
/// The parsed contents of <c>shared/halo-endpoint-manifest.json</c>, a live capture of
/// 343's unauthenticated settings service.
///
/// The manifest is embedded in this assembly at build time, so endpoint resolution needs
/// neither file system nor network, and the fixture path resolves endpoints in exactly the
/// way the live path does.
/// </summary>
public sealed class HaloEndpointManifest
{
    private const string ResourceName = "Eet.Halo.Client.halo-endpoint-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly HaloRetryPolicy FallbackPolicy = new(
        "default",
        TimeSpan.FromSeconds(10),
        MaxRetryCount: 1,
        TimeSpan.FromMilliseconds(50),
        RetryGrowth: 1.0,
        TimeSpan.FromMilliseconds(10),
        RetryIfNotFound: false);

    private readonly FrozenDictionary<string, HaloEndpoint> _endpoints;

    private HaloEndpointManifest(FrozenDictionary<string, HaloEndpoint> endpoints, string? clearanceAudience)
    {
        _endpoints = endpoints;
        ClearanceAudience = string.IsNullOrWhiteSpace(clearanceAudience) ? "RETAIL" : clearanceAudience;
    }

    /// <summary>The one instance anybody needs; parsing is pure and the result immutable.</summary>
    public static HaloEndpointManifest Default { get; } = Load();

    /// <summary>
    /// The audience name the clearance (flight) lookup takes, read out of the manifest's
    /// own Settings block rather than assumed.
    /// </summary>
    public string ClearanceAudience { get; }

    public IReadOnlyCollection<string> EndpointIds => _endpoints.Keys;

    public int Count => _endpoints.Count;

    public HaloEndpoint this[string endpointId] => Get(endpointId);

    public HaloEndpoint Get(string endpointId) =>
        _endpoints.TryGetValue(endpointId, out var endpoint)
            ? endpoint
            : throw new TrackerException(
                $"Endpoint '{endpointId}' is not in the Halo endpoint manifest.",
                "Check the spelling against shared/halo-endpoint-manifest.json; that file is the source of truth for endpoint ids.");

    public bool TryGet(string endpointId, out HaloEndpoint? endpoint) =>
        _endpoints.TryGetValue(endpointId, out endpoint);

    private static HaloEndpointManifest Load()
    {
        using var stream = typeof(HaloEndpointManifest).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new TrackerException(
                $"Embedded resource '{ResourceName}' is missing from Halo.Client.",
                "The build should embed shared/halo-endpoint-manifest.json. Check the EmbeddedResource item in Halo.Client.csproj.");

        var raw = JsonSerializer.Deserialize<RawManifest>(stream, ManifestJsonOptions)
            ?? throw new TrackerException(
                "The Halo endpoint manifest deserialised to null.",
                "shared/halo-endpoint-manifest.json is empty or not valid JSON.");

        var authorities = raw.Authorities.ToDictionary(
            kv => kv.Key,
            kv => new HaloAuthority(kv.Key, kv.Value.Scheme, kv.Value.Hostname ?? kv.Key, kv.Value.Port),
            StringComparer.Ordinal);

        var policies = raw.RetryPolicies.ToDictionary(
            kv => kv.Key,
            kv => ToPolicy(kv.Key, kv.Value),
            StringComparer.Ordinal);

        var endpoints = new Dictionary<string, HaloEndpoint>(StringComparer.Ordinal);
        foreach (var (id, endpoint) in raw.Endpoints)
        {
            if (endpoint.AuthorityId is null || !authorities.TryGetValue(endpoint.AuthorityId, out var authority))
            {
                // An endpoint naming an authority the manifest does not define is not
                // callable. Skip it rather than fail the whole load: there are 177
                // endpoints in here and we call six of them.
                continue;
            }

            endpoints[id] = new HaloEndpoint(
                id,
                authority,
                endpoint.Path ?? string.Empty,
                endpoint.QueryString ?? string.Empty,
                endpoint.ClearanceAware,
                endpoint.RetryPolicyId is not null && policies.TryGetValue(endpoint.RetryPolicyId, out var policy)
                    ? policy
                    : FallbackPolicy);
        }

        raw.Settings.TryGetValue("ClearanceAudience", out var audience);
        return new HaloEndpointManifest(endpoints.ToFrozenDictionary(StringComparer.Ordinal), audience);
    }

    private static HaloRetryPolicy ToPolicy(string id, RawRetryPolicy raw)
    {
        var options = raw.RetryOptions;
        return new HaloRetryPolicy(
            id,
            TimeSpan.FromMilliseconds(raw.TimeoutMs <= 0 ? 10_000 : raw.TimeoutMs),
            options?.MaxRetryCount ?? 0,
            TimeSpan.FromMilliseconds(options?.RetryDelayMs ?? 0),
            options?.RetryGrowth ?? 1.0,
            TimeSpan.FromMilliseconds(options?.RetryJitterMs ?? 0),
            options?.RetryIfNotFound ?? false);
    }

    private sealed class RawManifest
    {
        public Dictionary<string, RawAuthority> Authorities { get; init; } = [];

        public Dictionary<string, RawRetryPolicy> RetryPolicies { get; init; } = [];

        public Dictionary<string, string> Settings { get; init; } = [];

        public Dictionary<string, RawEndpoint> Endpoints { get; init; } = [];
    }

    private sealed record RawAuthority(string? AuthorityId, int Scheme, string? Hostname, int? Port);

    private sealed record RawRetryPolicy(string? RetryPolicyId, int TimeoutMs, RawRetryOptions? RetryOptions);

    private sealed record RawRetryOptions(
        int MaxRetryCount,
        int RetryDelayMs,
        double RetryGrowth,
        int RetryJitterMs,
        bool RetryIfNotFound);

    private sealed record RawEndpoint(
        string? AuthorityId,
        string? Path,
        string? QueryString,
        string? RetryPolicyId,
        bool ClearanceAware);
}

/// <summary>
/// The manifest endpoint ids this tracker actually calls, plus the one Waypoint endpoint
/// that is not in the manifest at all.
/// </summary>
public static class HaloEndpointIds
{
    // --- halostats: NOT clearance-aware. Spartan token only. ---
    public const string MatchHistory = "Stats_GetMatchHistory";
    public const string MatchCount = "Stats_GetMatchCount";
    public const string MatchStats = "Stats_GetMatchStats";

    // --- skill: clearance-aware. Spartan token AND 343-clearance. ---
    public const string MatchSkill = "Skill_GetMatchResult";
    public const string PlaylistCsr = "Skill_GetPlaylistCsr";

    // --- discovery: clearance-aware. Used only to put names on asset GUIDs. ---
    public const string UgcMap = "HIUGC_Discovery_GetMap";
    public const string UgcGameVariant = "HIUGC_Discovery_GetUgcGameVariant";

    /// <summary>
    /// Deliberately the version-less variant. The versioned playlist endpoint carries a
    /// mandatory ?clearanceId= query parameter in the manifest, which would mean threading
    /// the clearance value through URL building as well as through the auth header. This
    /// one takes the asset id alone and answers the only question being asked: what is this
    /// playlist called.
    /// </summary>
    public const string UgcPlaylist = "HIUGC_Discovery_GetPlaylistWithoutVersion";

    // --- settings: how clearance itself is obtained. Not clearance-aware, obviously. ---
    public const string Clearance = "Settings_GetClearance";

    /// <summary>
    /// The service record, kept deliberately outside the manifest ids above.
    ///
    /// Provenance warning: everything else in this class comes from 343's live settings
    /// service, which publishes it. This one does not appear in the manifest at all. It is
    /// a Waypoint web endpoint known only from traffic capture, so its path, its query
    /// string and its response shape are all weaker evidence than the rest of this file.
    /// Callers must treat a failure from it as expected rather than exceptional and fall
    /// back to aggregating match history, which is derived entirely from manifest
    /// endpoints. <see cref="HaloClient.GetServiceRecordAsync"/> does exactly that.
    /// </summary>
    public const string ServiceRecord = "Waypoint_ServiceRecord";

    /// <summary>Path template for <see cref="ServiceRecord"/>. Traffic capture, not manifest.</summary>
    public const string ServiceRecordPathTemplate = "/hi/players/{player}/Matchmade/servicerecord";

    /// <summary>
    /// The authority the service record appears to be served from. Also capture-derived,
    /// but at least the host itself is a manifest authority.
    /// </summary>
    public const string ServiceRecordAuthorityId = "halostats";

    /// <summary>True when an id is published by 343 rather than inferred from capture.</summary>
    public static bool IsFromManifest(string endpointId) =>
        HaloEndpointManifest.Default.TryGet(endpointId, out _);
}
