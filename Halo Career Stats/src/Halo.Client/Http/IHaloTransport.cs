using System.Net;
using System.Text.Json;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Http;

/// <summary>
/// The one seam between "where the JSON came from" and "what the JSON means".
///
/// It is deliberately drawn at raw JSON rather than at typed responses. If the fixture
/// implementation returned <c>CareerSnapshot</c>, or even typed DTOs, then running against
/// fixtures would prove nothing about the code that runs against 343 -- the deserialiser,
/// the enum handling, the ISO-8601 durations and the mapper would all be skipped. Drawing
/// the seam here means the no-credentials path exercises every line of parsing and mapping
/// that the credentialed path does, and the only thing that differs is the source of the
/// bytes.
/// </summary>
public interface IHaloTransport
{
    /// <summary>True when this is serving recorded fixtures rather than talking to 343.</summary>
    bool IsFixture { get; }

    /// <summary>Human-readable provenance, shown on the dashboard so nobody mistakes one for the other.</summary>
    string Description { get; }

    /// <summary>Fetch, or throw a <see cref="TrackerException"/> the caller can show a user.</summary>
    Task<string> GetJsonAsync(HaloCall call, CancellationToken ct = default);

    /// <summary>
    /// Fetch, or return null when the resource genuinely is not there (404, or a fixture
    /// that was not recorded). Anything else still throws.
    /// </summary>
    Task<string?> TryGetJsonAsync(HaloCall call, CancellationToken ct = default);
}

/// <summary>Deserialisation helpers shared by both transports, so both fail identically.</summary>
public static class HaloTransportExtensions
{
    public static async Task<T> GetAsync<T>(this IHaloTransport transport, HaloCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(call);

        var json = await transport.GetJsonAsync(call, ct).ConfigureAwait(false);
        return Parse<T>(json, call);
    }

    public static async Task<T?> TryGetAsync<T>(this IHaloTransport transport, HaloCall call, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(call);

        var json = await transport.TryGetJsonAsync(call, ct).ConfigureAwait(false);
        return json is null ? null : Parse<T>(json, call);
    }

    private static T Parse<T>(string json, HaloCall call)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, HaloJson.Options)
                ?? throw new TrackerException(
                    $"{call.Endpoint.Id} returned JSON null.",
                    "The service answered, but with nothing in it. Retrying later usually helps; if it persists the endpoint shape may have changed.");
        }
        catch (JsonException ex)
        {
            throw new TrackerException(
                $"Could not read the response from {call.Endpoint.Id}: {ex.Message}",
                $"The shape of {call.Endpoint.Authority.Hostname}{call.PathAndQuery} is not what this client expects. If this is a fixture, check it is raw API-shaped JSON; if it is live, 343 may have changed the response.",
                ex);
        }
    }
}

/// <summary>Thrown for an HTTP status the caller may want to distinguish.</summary>
public sealed class HaloHttpException : Exception
{
    public HaloHttpException(HttpStatusCode status, string endpointId, string message, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        EndpointId = endpointId;
    }

    public HttpStatusCode Status { get; }

    public string EndpointId { get; }
}
