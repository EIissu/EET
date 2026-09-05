using System.Globalization;
using System.Net;
using Eet.Trackers.Core;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client.Http;

/// <summary>
/// The live transport. Everything policy-shaped -- auth, clearance, retry, concurrency,
/// caching -- lives in delegating handlers, so this class is only responsible for turning
/// a status code into either a body or an error a human can act on.
/// </summary>
public sealed class HttpHaloTransport : IHaloTransport
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpHaloTransport> _logger;

    public HttpHaloTransport(HttpClient http, ILogger<HttpHaloTransport>? logger = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpHaloTransport>.Instance;
    }

    public bool IsFixture => false;

    public string Description => "live halowaypoint services";

    public async Task<string> GetJsonAsync(HaloCall call, CancellationToken ct = default)
    {
        var body = await SendAsync(call, allowNotFound: false, ct).ConfigureAwait(false);
        return body!;
    }

    public Task<string?> TryGetJsonAsync(HaloCall call, CancellationToken ct = default) =>
        SendAsync(call, allowNotFound: true, ct);

    private async Task<string?> SendAsync(HaloCall call, bool allowNotFound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        using var request = call.ToRequest();
        using var response = await SendCoreAsync(request, call, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
        {
            _logger.LogDebug("Halo {Endpoint} returned 404; treating as absent.", call.Endpoint.Id);
            return null;
        }

        throw Describe(call, response);
    }

    /// <summary>
    /// Send, and turn the two failures that never reach a status code into something the
    /// API layer can classify.
    /// </summary>
    /// <remarks>
    /// Without this both escape as framework exceptions and are misreported. A transport
    /// failure -- DNS, TLS, connection refused, the service being down -- arrives as
    /// <see cref="HttpRequestException"/>, which is not a <c>TrackerException</c> and so
    /// becomes a 500 reading "this is a bug rather than a configuration problem"; it is
    /// neither. Worse, HttpClient signals its OWN timeout as a
    /// <see cref="TaskCanceledException"/>, indistinguishable by type from the caller
    /// hanging up, so a request the tracker gave up on is reported as "the client went away
    /// before the answer was ready. Nothing to fix." The caller's token is the thing that
    /// tells the two apart, so it is checked here where it is still in scope.
    /// </remarks>
    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        HaloCall call,
        CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Not a cancellation: HttpClient.Timeout elapsed, or a per-attempt timeout did.
            throw new TrackerException(
                $"Halo request to {Where(call)} timed out before the service answered.",
                "The whole call, retries and backoff included, is bounded by the HttpClient timeout. If this happens repeatedly, either 343 is slow right now or the retry budget is larger than the timeout -- lower Halo:MaxRetries or Halo:MaxRetryDelay so the attempts fit inside it.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TrackerException(
                $"Halo request to {Where(call)} could not be sent: {ex.Message}",
                "This is a network or TLS failure rather than an API rejection -- nothing was returned to reject. Check connectivity and DNS for the host named above; the tracker also runs entirely offline against fixtures if you only need it working now.",
                ex);
        }
    }

    private static string Where(HaloCall call) => string.Create(
        CultureInfo.InvariantCulture,
        $"{call.Endpoint.Id} ({call.Endpoint.Authority.Hostname}{call.PathAndQuery})");

    /// <summary>
    /// Turn a failed response into something with a remedy attached. These four cases cover
    /// essentially every failure a person running this tool will actually hit, and each has
    /// a genuinely different fix.
    /// </summary>
    private static TrackerException Describe(HaloCall call, HttpResponseMessage response)
    {
        var where = Where(call);
        var status = (int)response.StatusCode;
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => call.Endpoint.ClearanceAware
                ? "The Spartan token or the 343-clearance value was rejected. This endpoint is clearance-aware, so a stale flight-configuration id is the likelier of the two; the build number used for the clearance lookup goes out of date every time the game ships."
                : "The Spartan token was rejected or has expired. Sign in again; the token is short-lived by design.",

            HttpStatusCode.Forbidden =>
                "Xbox Live accepted the token but refused the data. The usual cause is the target player's match history privacy setting, which they control and you cannot override.",

            HttpStatusCode.NotFound =>
                "Nothing there. If this is a match that just ended, its stats document may not have been written yet -- 343's own client retries 404s on this endpoint for the same reason.",

            HttpStatusCode.TooManyRequests =>
                "Rate limited even after backing off. Reduce Halo:MaxConcurrentRequests, or wait; this is an undocumented API and the limits are not published.",

            _ when status >= 500 =>
                "The service is having a bad time. This is not something the tracker can fix; try again shortly.",

            _ => "Unexpected response. The endpoint shape may have changed since the manifest was captured.",
        };

        return new TrackerException($"Halo request failed with HTTP {status} at {where}.", reason);
    }
}
