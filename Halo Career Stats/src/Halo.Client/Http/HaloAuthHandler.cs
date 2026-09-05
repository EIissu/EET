using Eet.Halo.Client.Endpoints;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Http;

/// <summary>
/// Supplies the <c>343-clearance</c> header value, which is a flight configuration id.
/// </summary>
public interface IHaloClearanceProvider
{
    /// <summary>
    /// The active clearance id, or null if none could be obtained. Null is not fatal: it
    /// only means the clearance-aware endpoints will fail, and everything served from
    /// halostats still works.
    /// </summary>
    Task<string?> GetClearanceAsync(CancellationToken ct = default);
}

/// <summary>
/// Attaches Halo's two authentication headers -- and, importantly, attaches the second one
/// only where it belongs.
///
/// Every request to these services carries <c>X-343-Authorization-Spartan</c>. Only some
/// carry <c>343-clearance</c>, and which ones is not a matter of taste: 343's settings
/// service publishes a <c>ClearanceAware</c> flag per endpoint and this handler reads it
/// straight off the resolved <see cref="HaloEndpoint"/>. Concretely, on the endpoints this
/// tracker uses:
///
///   halostats  Stats_GetMatchHistory, Stats_GetMatchCount, Stats_GetMatchStats
///              ClearanceAware = false -> Spartan token only.
///
///   skill      Skill_GetMatchResult, Skill_GetPlaylistCsr
///   discovery  HIUGC_Discovery_GetMap, HIUGC_Discovery_GetUgcGameVariant
///              ClearanceAware = true  -> Spartan token AND 343-clearance.
///
/// Sending clearance on everything is the usual shortcut and it is wrong in a way that
/// bites later: acquiring clearance is itself an authenticated round trip, so a tool that
/// needs it for match history cannot read match history until the flight lookup succeeds,
/// and the flight lookup is the part that goes stale when the game ships a new build.
/// </summary>
public sealed class HaloAuthHandler : DelegatingHandler
{
    /// <summary>The Spartan token header. Not a bearer token, not an Authorization header.</summary>
    public const string SpartanHeader = "X-343-Authorization-Spartan";

    /// <summary>The clearance (flight configuration) header. Only on clearance-aware endpoints.</summary>
    public const string ClearanceHeader = "343-clearance";

    private readonly IXboxAuth _auth;
    private readonly IHaloClearanceProvider? _clearance;

    public HaloAuthHandler(IXboxAuth auth, IHaloClearanceProvider? clearance = null)
    {
        _auth = auth;
        _clearance = clearance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var spartan = await GetSpartanTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Remove(SpartanHeader);
        request.Headers.TryAddWithoutValidation(SpartanHeader, spartan.Token);

        var endpoint = request.GetEndpoint();
        if (endpoint?.ClearanceAware == true)
        {
            var clearance = _clearance is null
                ? null
                : await _clearance.GetClearanceAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(clearance))
            {
                throw new TrackerException(
                    $"Endpoint '{endpoint.Id}' is clearance-aware but no 343-clearance value is available.",
                    "The flight-configuration lookup failed or is not configured. Rank and asset names will be missing; match history and match stats are unaffected because those endpoints are not clearance-aware.");
            }

            request.Headers.Remove(ClearanceHeader);
            request.Headers.TryAddWithoutValidation(ClearanceHeader, clearance);
        }

        // Not authentication, but 343's services are noticeably happier when asked for JSON
        // explicitly. Saying who we are is the other half of that and lives on the
        // HttpClient itself -- see HaloTrackerSetup.Identify -- so that it also covers the
        // Xbox profile lookup, which never passes through this handler.
        if (request.Headers.Accept.Count == 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct)
    {
        try
        {
            return await _auth.GetSpartanTokenAsync(ct).ConfigureAwait(false);
        }
        catch (TrackerException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TrackerException(
                "Could not obtain a Spartan token.",
                "Check the Xbox Live sign-in: the chain is Azure AD -> user.auth.xboxlive.com -> xsts.auth.xboxlive.com (relying party https://prod.xsts.halowaypoint.com/) -> the Halo token endpoint. Without credentials the tracker still runs against fixtures.",
                ex);
        }
    }
}
