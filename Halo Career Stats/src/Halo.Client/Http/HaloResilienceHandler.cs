using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client.Http;

/// <summary>
/// Retry with backoff for an undocumented API we are guests on.
///
/// The rules, in the order they are applied:
///
///   1. A <c>Retry-After</c> header always wins. Both forms are honoured -- delta-seconds
///      and an HTTP date -- because 429s from this service have been seen using each. If
///      the server names a wait, waiting less than it is the one behaviour guaranteed to
///      make things worse.
///   2. Otherwise back off exponentially from <see cref="HaloOptions.BaseRetryDelay"/>,
///      with full jitter, so a burst of parallel match-stat fetches that all get throttled
///      does not retry in lockstep.
///   3. Every wait is capped by <see cref="HaloOptions.MaxRetryDelay"/>, including one the
///      server asked for. A ten-minute Retry-After should fail fast, not hang a dashboard.
///   4. 404 is retried only where 343's own manifest says it should be
///      (<c>RetryIfNotFound</c>, which is true for the single-match endpoints because match
///      documents are written asynchronously after the game ends). Anywhere else a 404 is
///      an answer, not a failure.
///
/// 4xx other than 429 and the manifest-sanctioned 404 are never retried: repeating a
/// request the server has already rejected is how a fan tool gets its whole user base
/// blocked.
/// </summary>
public sealed class HaloResilienceHandler : DelegatingHandler
{
    private readonly HaloOptions _options;
    private readonly ILogger<HaloResilienceHandler> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _random;

    /// <param name="delay">
    /// Overridable so tests can assert the backoff schedule without actually sleeping
    /// through it.
    /// </param>
    /// <param name="random">Overridable for the same reason: jitter has to be pinned to be tested.</param>
    public HaloResilienceHandler(
        HaloOptions options,
        ILogger<HaloResilienceHandler>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? random = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HaloResilienceHandler>.Instance;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>
    /// Every wait this handler performed, newest last. Test-visible; cheap enough to always
    /// keep, but it has to be guarded: the concurrency cap sits INSIDE this handler by
    /// design, so nothing serialises the requests passing through here and two throttled
    /// calls can record a wait at the same instant. An unguarded List would corrupt or
    /// throw, in production, to keep a diagnostic.
    /// </summary>
    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_waitsGate)
            {
                return _waits.ToArray();
            }
        }
    }

    private readonly List<TimeSpan> _waits = [];
    private readonly Lock _waitsGate = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = request.GetEndpoint();
        var retryNotFound = endpoint?.Retry.RetryIfNotFound ?? false;
        var maxAttempts = Math.Max(1, _options.MaxRetries + 1);

        HttpResponseMessage? response = null;
        for (var attempt = 1; ; attempt++)
        {
            response?.Dispose();
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!ShouldRetry(response.StatusCode, retryNotFound) || attempt >= maxAttempts)
            {
                return response;
            }

            var wait = ComputeDelay(response, attempt);
            lock (_waitsGate)
            {
                _waits.Add(wait);
            }

            _logger.LogWarning(
                "Halo {Endpoint} returned {Status}; retrying in {DelayMs}ms (attempt {Attempt} of {MaxAttempts}).",
                endpoint?.Id ?? request.RequestUri?.AbsolutePath ?? "?",
                (int)response.StatusCode,
                (int)wait.TotalMilliseconds,
                attempt,
                maxAttempts);

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool ShouldRetry(HttpStatusCode status, bool retryNotFound) => status switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.NotFound => retryNotFound,
        >= HttpStatusCode.InternalServerError => true,
        _ => false,
    };

    private TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
    {
        var serverAsked = ReadRetryAfter(response);
        if (serverAsked is { } asked)
        {
            return Clamp(asked);
        }

        // Full jitter: uniform over [0, exponential]. Halves the expected wait compared
        // with a fixed exponential, and de-correlates callers that were throttled together.
        var exponential = _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return Clamp(TimeSpan.FromMilliseconds(exponential * _random()));
    }

    private TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _options.MaxRetryDelay ? _options.MaxRetryDelay : wait;

    /// <summary>
    /// Both legal spellings of Retry-After. HttpClient parses the header for us but only
    /// populates whichever of Delta/Date matches the form the server used.
    /// </summary>
    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            return date - (now ?? DateTimeOffset.UtcNow);
        }

        // A malformed value still tells us the server wants us to wait; fall back to
        // parsing it as seconds rather than ignoring it.
        var raw = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
