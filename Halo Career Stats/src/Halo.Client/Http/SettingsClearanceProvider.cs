using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Halo.Client.Endpoints;
using Eet.Trackers.Core;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client.Http;

/// <summary>
/// Fetches the flight-configuration id that clearance-aware endpoints want in their
/// <c>343-clearance</c> header.
///
/// Note the shape of the dependency: this reaches 343 through its own transport, NOT
/// through the one that has <see cref="HaloAuthHandler"/> asking for clearance -- that
/// would be a cycle. The clearance endpoint is itself <c>ClearanceAware: false</c> in the
/// manifest, which is the manifest telling us the same thing.
///
/// The value is cached, but on a clock rather than for the process lifetime, and a SUCCESS
/// and a FAILURE are cached for deliberately different lengths of time.
///
/// Both halves of that matter. A flight id is mutable -- it changes when 343 reconfigures a
/// build -- so a value pinned for the life of a long-running API process goes stale and
/// every clearance-aware request starts 401ing with nothing to re-read it. And caching a
/// FAILURE for the process lifetime is worse still: one transient 500 or timeout on the
/// very first flight lookup would permanently disable rank and asset names for that
/// process, with a restart as the only cure. So a success is held for
/// <see cref="HaloOptions.ClearanceLifetime"/> and a failure only for
/// <see cref="HaloOptions.ClearanceRetryDelay"/>, which is long enough not to hammer the
/// settings service and short enough that the next dashboard refresh gets rank back.
/// </summary>
public sealed class SettingsClearanceProvider : IHaloClearanceProvider
{
    private readonly IHaloTransport _transport;
    private readonly HaloEndpointResolver _endpoints;
    private readonly HaloOptions _options;
    private readonly ILogger<SettingsClearanceProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cached;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;

    public SettingsClearanceProvider(
        IHaloTransport transport,
        HaloEndpointResolver endpoints,
        HaloOptions options,
        ILogger<SettingsClearanceProvider>? logger = null,
        TimeProvider? time = null)
    {
        _transport = transport;
        _endpoints = endpoints;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsClearanceProvider>.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many times the settings service has actually been asked. Test-visible.</summary>
    public int Fetches => _fetches;

    private int _fetches;

    public async Task<string?> GetClearanceAsync(CancellationToken ct = default)
    {
        if (_time.GetUtcNow() < _validUntil)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (now < _validUntil)
            {
                return _cached;
            }

            Interlocked.Increment(ref _fetches);
            _cached = await FetchAsync(ct).ConfigureAwait(false);

            // A failure is remembered only briefly. Anything longer turns one bad minute
            // into a process that has no rank data until it is restarted.
            _validUntil = now + (_cached is null
                ? Positive(_options.ClearanceRetryDelay, TimeSpan.FromMinutes(1))
                : Positive(_options.ClearanceLifetime, TimeSpan.FromHours(1)));

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan Positive(TimeSpan configured, TimeSpan fallback) =>
        configured > TimeSpan.Zero ? configured : fallback;

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.Clearance),
            HaloCachePolicy.Short,
            pathArgs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // The audience comes from the manifest's own Settings block rather than a
                // guess: ClearanceAudience is "RETAIL" there. The manifest also supplies
                // the query template (?sandbox=&build=&release=1.3); only the values in it
                // are ours, and the build number is the part that goes stale.
                ["audience"] = _endpoints.ClearanceAudience,
                ["sandbox"] = _options.ClearanceSandbox,
                ["buildNumber"] = _options.ClearanceBuildNumber,
            });

        try
        {
            var json = await _transport.TryGetJsonAsync(call, ct).ConfigureAwait(false);
            if (json is null)
            {
                _logger.LogWarning("Clearance lookup returned nothing; clearance-aware endpoints will be skipped.");
                return null;
            }

            var response = JsonSerializer.Deserialize<ClearanceResponse>(json, HaloJsonForClearance);
            if (string.IsNullOrEmpty(response?.FlightConfigurationId))
            {
                _logger.LogWarning("Clearance lookup returned no FlightConfigurationId.");
                return null;
            }

            return response.FlightConfigurationId;
        }
        catch (Exception ex) when (ex is TrackerException or JsonException)
        {
            // Losing clearance costs rank and asset names. It must not cost match history,
            // which is served by endpoints that never wanted clearance in the first place.
            _logger.LogWarning(
                ex,
                "Clearance lookup failed; continuing without it. Rank and asset names will be unavailable.");
            return null;
        }
    }

    private static readonly JsonSerializerOptions HaloJsonForClearance = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record ClearanceResponse(string? FlightConfigurationId);
}

/// <summary>
/// Used when clearance cannot be obtained at all, so that the clearance-aware endpoints
/// fail loudly and locally instead of being silently sent without their header.
/// </summary>
public sealed class NoClearanceProvider : IHaloClearanceProvider
{
    public Task<string?> GetClearanceAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>A fixed clearance value, for tests and for operators who already have one.</summary>
public sealed class StaticClearanceProvider : IHaloClearanceProvider
{
    private readonly string? _value;

    public StaticClearanceProvider(string? value) => _value = value;

    public Task<string?> GetClearanceAsync(CancellationToken ct = default) => Task.FromResult(_value);
}
