using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// The typed surface this tracker uses. Small on purpose: everything the dashboard needs is
/// five endpoints plus the manifest.
/// </summary>
public interface IBungieApi
{
    /// <summary>True when the responses are coming from recorded fixtures.</summary>
    bool IsFixture { get; }

    Task<IReadOnlyList<UserInfoCard>> SearchByBungieNameAsync(
        string displayName, short displayNameCode, CancellationToken ct = default);

    Task<UserMembershipData?> GetMembershipsByIdAsync(
        string membershipId, int membershipType, CancellationToken ct = default);

    Task<DestinyProfileResponse> GetProfileAsync(
        int membershipType, string membershipId, string components, CancellationToken ct = default);

    /// <summary>
    /// All-time historical stats. <paramref name="characterId"/> of <c>0</c> aggregates
    /// across every character on the profile, which is documented for this endpoint (and
    /// notably is not documented for activity history).
    /// </summary>
    Task<IReadOnlyDictionary<string, HistoricalStatsByPeriod>> GetHistoricalStatsAsync(
        int membershipType,
        string membershipId,
        string characterId,
        string groups,
        string modes,
        CancellationToken ct = default);

    /// <summary>
    /// One page of activity history for one character. Returns an empty list at the end of
    /// the history: Bungie answers ErrorCode 1 with no Response at all, which is a success,
    /// not a failure.
    /// </summary>
    Task<IReadOnlyList<HistoricalStatsPeriodGroup>> GetActivityHistoryAsync(
        int membershipType,
        string membershipId,
        string characterId,
        int mode,
        int count,
        int page,
        CancellationToken ct = default);

    Task<PostGameCarnageReport?> GetPostGameCarnageReportAsync(
        string activityId, CancellationToken ct = default);

    Task<DestinyManifest> GetManifestAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch one definition table by its site-root relative path from
    /// <c>jsonWorldComponentContentPaths</c>. These files are plain JSON on the CDN: no
    /// platform envelope, and no API key required.
    /// </summary>
    Task<Stream> GetDefinitionTableAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>
/// The live Bungie.net client.
///
/// Two things here are not obvious and both are load-bearing:
///
///   * Success is <c>ErrorCode == 1</c>, not <c>HTTP 200</c>. Bungie answers 200 for an
///     invalid API key, a private profile, and a rate limit alike. Every read goes through
///     <see cref="BungieResponse"/> so there is no path that checks the status code and
///     stops there.
///
///   * Throttles arrive the same way, carrying <c>ThrottleSeconds</c>. Waiting exactly that
///     long and retrying is the documented behaviour, so it happens here rather than being
///     left to every caller.
/// </summary>
public sealed class BungieApiClient : IBungieApi
{
    private readonly HttpClient _http;
    private readonly BungieOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public BungieApiClient(HttpClient http, BungieOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _options = options;
        // Injected so a throttle test does not have to actually sleep.
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.PlatformBaseUrl, UriKind.Absolute);
        }
    }

    /// <summary>Live client, so never a fixture.</summary>
    public bool IsFixture => false;

    public async Task<IReadOnlyList<UserInfoCard>> SearchByBungieNameAsync(
        string displayName, short displayNameCode, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(
            new ExactSearchRequest { DisplayName = displayName, DisplayNameCode = displayNameCode },
            BungieResponse.Json);

        // membershipType All (-1) is the documented value here and the only one that finds a
        // player without already knowing their platform.
        var cards = await PostAsync<List<UserInfoCard>>(
                $"Destiny2/SearchDestinyPlayerByBungieName/{BungieMembershipType.All}/",
                body,
                $"the search for {Redact(displayName)}#{displayNameCode:0000}",
                optional: true,
                ct)
            .ConfigureAwait(false);

        return cards ?? [];
    }

    public Task<UserMembershipData?> GetMembershipsByIdAsync(
        string membershipId, int membershipType, CancellationToken ct = default) =>
        GetAsync<UserMembershipData>(
            $"User/GetMembershipsById/{Uri.EscapeDataString(membershipId)}/{membershipType.ToString(CultureInfo.InvariantCulture)}/",
            $"the memberships for {membershipId}",
            optional: true,
            ct);

    public async Task<DestinyProfileResponse> GetProfileAsync(
        int membershipType, string membershipId, string components, CancellationToken ct = default)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"Destiny2/{membershipType}/Profile/{Uri.EscapeDataString(membershipId)}/?components={Uri.EscapeDataString(components)}");

        return await GetRequiredAsync<DestinyProfileResponse>(
                path, $"the profile for {membershipId}", ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, HistoricalStatsByPeriod>> GetHistoricalStatsAsync(
        int membershipType,
        string membershipId,
        string characterId,
        string groups,
        string modes,
        CancellationToken ct = default)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"Destiny2/{membershipType}/Account/{Uri.EscapeDataString(membershipId)}/Character/{Uri.EscapeDataString(characterId)}/Stats/"
            + $"?groups={Uri.EscapeDataString(groups)}&modes={Uri.EscapeDataString(modes)}&periodType=2");

        var result = await GetAsync<Dictionary<string, HistoricalStatsByPeriod>>(
                path, $"the lifetime stats for {membershipId}", optional: true, ct)
            .ConfigureAwait(false);

        return result ?? new Dictionary<string, HistoricalStatsByPeriod>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<HistoricalStatsPeriodGroup>> GetActivityHistoryAsync(
        int membershipType,
        string membershipId,
        string characterId,
        int mode,
        int count,
        int page,
        CancellationToken ct = default)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"Destiny2/{membershipType}/Account/{Uri.EscapeDataString(membershipId)}/Character/{Uri.EscapeDataString(characterId)}/Stats/Activities/"
            + $"?count={count}&page={page}&mode={mode}");

        var result = await GetAsync<ActivityHistoryResults>(
                path,
                string.Create(CultureInfo.InvariantCulture, $"activity history page {page} for character {characterId}"),
                optional: true,
                ct)
            .ConfigureAwait(false);

        return result?.Activities ?? [];
    }

    public Task<PostGameCarnageReport?> GetPostGameCarnageReportAsync(
        string activityId, CancellationToken ct = default) =>
        GetAsync<PostGameCarnageReport>(
            $"Destiny2/Stats/PostGameCarnageReport/{Uri.EscapeDataString(activityId)}/",
            $"the carnage report for activity {activityId}",
            optional: true,
            ct);

    public Task<DestinyManifest> GetManifestAsync(CancellationToken ct = default) =>
        GetRequiredAsync<DestinyManifest>("Destiny2/Manifest/", "the Destiny manifest", ct);

    public async Task<Stream> GetDefinitionTableAsync(string relativePath, CancellationToken ct = default)
    {
        var url = new Uri(new Uri(_options.ContentBaseUrl, UriKind.Absolute), relativePath);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // No X-API-Key: these are static CDN files, and sending the key would put it in
        // front of a cache that has no business seeing it.
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            var failure = new TrackerException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Bungie's content CDN returned HTTP {(int)status} for the definition table {relativePath}."),
                "Definition paths come from jsonWorldComponentContentPaths and expire when the "
                + "manifest version changes. Re-read /Destiny2/Manifest/ rather than reusing a "
                + "stored path.");

            // Same reasoning as TransportFailure: a CDN that 404s is not a bad request.
            failure.Data["httpStatus"] = UpstreamStatus(status);
            failure.Data["upstreamStatus"] = (int)status;
            throw failure;
        }

        // The caller owns the stream; the response is kept alive by it.
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------

    private async Task<T> GetRequiredAsync<T>(string path, string context, CancellationToken ct)
    {
        var value = await GetAsync<T>(path, context, optional: false, ct).ConfigureAwait(false);
        return value!;
    }

    private Task<T?> GetAsync<T>(string path, string context, bool optional, CancellationToken ct) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, path), context, optional, ct);

    private Task<T?> PostAsync<T>(string path, string json, string context, bool optional, CancellationToken ct) =>
        SendAsync<T>(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            context,
            optional,
            ct);

    /// <summary>
    /// One request, with the throttle loop around it. The request is built by a factory
    /// because an <see cref="HttpRequestMessage"/> cannot be sent twice.
    /// </summary>
    private async Task<T?> SendAsync<T>(
        Func<HttpRequestMessage> build, string context, bool optional, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            if (_options.HasApiKey)
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Bungie does use real status codes for infrastructure failures -- a 503 during
            // maintenance carries an HTML body, not an envelope -- so those are caught here
            // before anything tries to parse JSON. Everything else is decided by ErrorCode.
            if (!response.IsSuccessStatusCode && !LooksLikeEnvelope(body))
            {
                throw TransportFailure(response.StatusCode, context, body);
            }

            var throttle = ReadThrottle(body);
            if (throttle is not null && attempt < _options.ThrottleRetries)
            {
                var wait = throttle.Value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : throttle.Value;
                if (wait > _options.MaxThrottleWait)
                {
                    wait = _options.MaxThrottleWait;
                }

                await _delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            return optional
                ? BungieResponse.UnwrapOptional<T>(body, context)
                : BungieResponse.Unwrap<T>(body, context);
        }
    }

    /// <summary>
    /// Peek at ErrorCode without committing to a payload shape. Returns the wait when the
    /// response is a throttle, and null otherwise.
    /// </summary>
    private static TimeSpan? ReadThrottle(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("ErrorCode", out var code)
                || !code.TryGetInt32(out var errorCode)
                || !BungiePlatformError.IsThrottle(errorCode))
            {
                return null;
            }

            var seconds = document.RootElement.TryGetProperty("ThrottleSeconds", out var t)
                && t.TryGetInt32(out var value)
                    ? value
                    : 0;

            return TimeSpan.FromSeconds(seconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeEnvelope(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ErrorCode", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TrackerException TransportFailure(HttpStatusCode status, string context, string body)
    {
        var remedy = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "An HTTP 401 or 403 from bungie.net -- as opposed to ErrorCode 2101 -- normally means "
                + "the request never reached the platform API. Check for a proxy, and check that "
                + "PlatformBaseUrl still ends in /Platform/.",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                "Bungie.net is down or in maintenance, which happens at every weekly reset. Retry later.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited at the edge, before the platform API saw the request. Slow down: lower "
                + "ActivityPageSize or MaxActivityPages.",
            _ => "Not a platform error -- the response was not a Bungie envelope at all. Treat it as a "
                + "network or proxy problem rather than an API one.",
        };

        var snippet = body.Length > 200 ? body[..200] + "..." : body;
        var exception = new TrackerException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP {(int)status} from Bungie for {context}, with no platform envelope in the body: {snippet}"),
            remedy);

        // Say what this is on the way out. A transport failure carries no ErrorCode, and the
        // HTTP boundary defaults a codeless TrackerException to 400 Bad Request -- so without
        // this, bungie.net being in its weekly maintenance window is reported to the
        // dashboard as the caller having typed something wrong.
        exception.Data["httpStatus"] = UpstreamStatus(status);
        exception.Data["upstreamStatus"] = (int)status;
        return exception;
    }

    /// <summary>
    /// What to answer our own caller with when bungie.net failed before the platform API
    /// ever saw the request.
    /// </summary>
    /// <remarks>
    /// A rate limit stays a rate limit so a caller's backoff still fires; maintenance stays
    /// unavailable so a retry is obviously the right move; a timeout stays a timeout.
    /// Everything else is a bad gateway. The one thing none of them is, is a bad request.
    /// </remarks>
    internal static int UpstreamStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => 429,
        HttpStatusCode.ServiceUnavailable => 503,
        HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout => 504,
        _ => 502,
    };

    /// <summary>
    /// Display names can contain anything, including things worth not pasting into a log
    /// line verbatim. Long ones are truncated; the code after the hash is never secret.
    /// </summary>
    private static string Redact(string displayName) =>
        displayName.Length <= 32 ? displayName : displayName[..32] + "...";
}
