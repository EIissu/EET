using Eet.Trackers.Core;

namespace Eet.Halo.Client.Endpoints;

/// <summary>
/// Turns an endpoint id into a <see cref="HaloEndpoint"/>.
///
/// Everything comes from the embedded manifest except the service record, which is layered
/// on top as a clearly-labelled synthetic entry. Keeping the layering here (rather than
/// letting each call site special-case it) means every downstream component -- URL
/// building, auth, retry, caching -- treats the weakly-sourced endpoint identically to the
/// well-sourced ones, and only this one file has to know the difference.
/// </summary>
public sealed class HaloEndpointResolver
{
    private readonly HaloEndpointManifest _manifest;
    private readonly HaloEndpoint _serviceRecord;

    public HaloEndpointResolver(HaloEndpointManifest? manifest = null)
    {
        _manifest = manifest ?? HaloEndpointManifest.Default;

        // The service record is served by the halostats host, which the manifest does
        // define, so at least the authority is real. Path, query and clearance-awareness
        // are inferred from traffic capture.
        //
        // Clearance-awareness is set to false to match every other halostats endpoint in
        // the manifest -- all eleven of them are ClearanceAware: false -- rather than
        // because capture proved it. If a live run ever gets a 401 from this endpoint
        // alone, flipping this to true is the first thing to try.
        var halostats = _manifest.Get(HaloEndpointIds.MatchHistory).Authority;
        _serviceRecord = new HaloEndpoint(
            HaloEndpointIds.ServiceRecord,
            halostats,
            HaloEndpointIds.ServiceRecordPathTemplate,
            QueryTemplate: string.Empty,
            ClearanceAware: false,
            Retry: _manifest.Get(HaloEndpointIds.MatchHistory).Retry);
    }

    public string ClearanceAudience => _manifest.ClearanceAudience;

    public HaloEndpoint Resolve(string endpointId) =>
        endpointId == HaloEndpointIds.ServiceRecord ? _serviceRecord : _manifest.Get(endpointId);

    /// <summary>
    /// Whether 343's settings service published this endpoint, as opposed to our having
    /// inferred it. Surfaces in snapshot warnings so a reader can tell how much to trust a
    /// number that came from it.
    /// </summary>
    public bool IsPublished(string endpointId) => _manifest.TryGet(endpointId, out _);

    /// <summary>
    /// A sanity check the tests pin: the endpoints we depend on must exist, and their
    /// clearance-awareness must be what the brief says it is. If 343 changes the manifest
    /// and someone re-captures it, this is what tells us.
    /// </summary>
    public void Validate()
    {
        (string Id, bool ExpectedClearance)[] expectations =
        [
            (HaloEndpointIds.MatchHistory, false),
            (HaloEndpointIds.MatchCount, false),
            (HaloEndpointIds.MatchStats, false),
            (HaloEndpointIds.MatchSkill, true),
            (HaloEndpointIds.PlaylistCsr, true),
            (HaloEndpointIds.UgcMap, true),
            (HaloEndpointIds.UgcGameVariant, true),
            (HaloEndpointIds.UgcPlaylist, true),
            (HaloEndpointIds.Clearance, false),
        ];

        foreach (var (id, expected) in expectations)
        {
            var endpoint = _manifest.Get(id);
            if (endpoint.ClearanceAware != expected)
            {
                throw new TrackerException(
                    $"Endpoint '{id}' is ClearanceAware={endpoint.ClearanceAware} in the manifest, but this client was written expecting {expected}.",
                    "shared/halo-endpoint-manifest.json has been re-captured and 343 changed this endpoint. Update HaloEndpointResolver.Validate and re-check which requests send the 343-clearance header.");
            }
        }
    }
}
