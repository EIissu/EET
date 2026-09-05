using System.Globalization;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client;

/// <summary>Which UGC discovery endpoint names a given asset.</summary>
public enum HaloAssetKind
{
    Map,
    GameVariant,
    Playlist,
}

/// <summary>
/// The typed surface over the manifest endpoints and the one capture-derived one.
///
/// Everything here is transport-agnostic on purpose: give it the fixture transport and it
/// answers from disk, give it the HTTP one and it answers from 343, and the code in between
/// -- paging, cache policy, the decision to fetch skill data at all -- is identical either
/// way.
/// </summary>
public sealed class HaloClient
{
    private readonly IHaloTransport _transport;
    private readonly HaloEndpointResolver _endpoints;
    private readonly HaloOptions _options;
    private readonly ILogger<HaloClient> _logger;

    public HaloClient(
        IHaloTransport transport,
        HaloEndpointResolver endpoints,
        HaloOptions options,
        ILogger<HaloClient>? logger = null)
    {
        _transport = transport;
        _endpoints = endpoints;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HaloClient>.Instance;
    }

    public bool IsFixture => _transport.IsFixture;

    public string SourceDescription => _transport.Description;

    /// <summary>
    /// One page of match history. Newest first.
    /// </summary>
    /// <remarks>
    /// The manifest gives this endpoint an empty QueryString, so <c>start</c>, <c>count</c>
    /// and <c>type</c> are supplied by us from traffic knowledge rather than read from
    /// 343's own description of the endpoint. That is a narrower gap than the service
    /// record's -- the path and host are published, only the parameters are not -- but it
    /// is a gap, and it is why an unexpected response here degrades rather than throws.
    /// </remarks>
    public Task<HaloMatchHistoryResponse> GetMatchHistoryAsync(
        string xuid,
        int start,
        int count,
        string matchType = HaloEnums.MatchType.Matchmade,
        CancellationToken ct = default)
    {
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.MatchHistory),
            HaloCachePolicy.Short,
            PlayerArg(xuid),
            [
                new("start", start.ToString(CultureInfo.InvariantCulture)),
                new("count", count.ToString(CultureInfo.InvariantCulture)),
                new("type", matchType),
            ]);

        return _transport.GetAsync<HaloMatchHistoryResponse>(call, ct);
    }

    /// <summary>Lifetime match counts. One cheap request instead of paging a career.</summary>
    public Task<HaloMatchCountResponse?> GetMatchCountAsync(string xuid, CancellationToken ct = default)
    {
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.MatchCount),
            HaloCachePolicy.Short,
            PlayerArg(xuid));

        return _transport.TryGetAsync<HaloMatchCountResponse>(call, ct);
    }

    /// <summary>
    /// Full stats for one finished match. Cached forever, because a finished match is
    /// finished: nobody is going to score again in it.
    /// </summary>
    public Task<HaloMatchStatsResponse?> GetMatchStatsAsync(string matchId, CancellationToken ct = default)
    {
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.MatchStats),
            HaloCachePolicy.Forever,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["matchId"] = matchId });

        return _transport.TryGetAsync<HaloMatchStatsResponse>(call, ct);
    }

    /// <summary>
    /// Per-match CSR and MMR. Clearance-aware, so this is the first thing to disappear
    /// when the flight id goes stale -- hence a null return rather than a throw.
    /// </summary>
    public async Task<HaloMatchSkillResult?> GetMatchSkillAsync(
        string matchId,
        string xuid,
        CancellationToken ct = default)
    {
        var playerRef = Identity.XuidRef(xuid);
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.MatchSkill),
            HaloCachePolicy.Forever,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["matchId"] = matchId },
            [new("players", playerRef)]);

        var response = await _transport
            .TryGetAsync<HaloSkillResponse<HaloMatchSkillResult>>(call, ct)
            .ConfigureAwait(false);

        return response?.For(playerRef);
    }

    /// <summary>Current rank in a playlist. Clearance-aware; null when unranked or unavailable.</summary>
    public async Task<HaloPlaylistCsrResult?> GetPlaylistCsrAsync(
        string playlistId,
        string xuid,
        CancellationToken ct = default)
    {
        var playerRef = Identity.XuidRef(xuid);
        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.PlaylistCsr),
            HaloCachePolicy.Short,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["playlistId"] = playlistId },
            [new("players", playerRef)]);

        var response = await _transport
            .TryGetAsync<HaloSkillResponse<HaloPlaylistCsrResult>>(call, ct)
            .ConfigureAwait(false);

        return response?.For(playerRef);
    }

    /// <summary>
    /// The Waypoint service record, or null.
    ///
    /// This is the endpoint whose provenance is weakest: it is not in 343's published
    /// manifest at all, only in traffic capture. It is therefore treated as an optimisation
    /// rather than a dependency -- any failure returns null and the caller aggregates match
    /// history instead, which is derived entirely from manifest endpoints. The lifetime
    /// totals are slightly different in the two cases (the service record counts a whole
    /// career, the aggregate counts what we paged) and the snapshot says which it used.
    /// </summary>
    public async Task<HaloServiceRecordResponse?> GetServiceRecordAsync(
        string xuid,
        string? seasonId = null,
        CancellationToken ct = default)
    {
        var query = seasonId is null
            ? Array.Empty<KeyValuePair<string, string>>()
            : [new KeyValuePair<string, string>("seasonId", seasonId)];

        var call = HaloCall.Create(
            _endpoints.Resolve(HaloEndpointIds.ServiceRecord),
            HaloCachePolicy.Short,
            PlayerArg(xuid),
            query);

        try
        {
            return await _transport.TryGetAsync<HaloServiceRecordResponse>(call, ct).ConfigureAwait(false);
        }
        catch (TrackerException ex)
        {
            _logger.LogInformation(
                ex,
                "Service record unavailable; falling back to aggregating match history. This endpoint is not in the manifest, so failing is an expected outcome rather than a fault.");
            return null;
        }
    }

    /// <summary>
    /// A UGC asset's public name. Clearance-aware, cached forever (a published asset's
    /// name does not change), and null-tolerant, because a missing name costs a breakdown
    /// row a label and nothing else.
    /// </summary>
    public async Task<string?> GetAssetNameAsync(
        HaloAssetRef? asset,
        HaloAssetKind kind,
        CancellationToken ct = default)
    {
        // The playlist endpoint used here is the version-less one, so a playlist reference
        // without a VersionId is still resolvable; maps and game variants are not.
        if (asset?.AssetId is null || (asset.VersionId is null && kind != HaloAssetKind.Playlist))
        {
            return null;
        }

        var endpointId = kind switch
        {
            HaloAssetKind.Map => HaloEndpointIds.UgcMap,
            HaloAssetKind.Playlist => HaloEndpointIds.UgcPlaylist,
            _ => HaloEndpointIds.UgcGameVariant,
        };

        var pathArgs = new Dictionary<string, string>(StringComparer.Ordinal) { ["assetId"] = asset.AssetId };
        if (asset.VersionId is not null)
        {
            pathArgs["versionId"] = asset.VersionId;
        }

        var call = HaloCall.Create(
            _endpoints.Resolve(endpointId),
            HaloCachePolicy.Forever,
            pathArgs);

        try
        {
            var result = await _transport.TryGetAsync<HaloUgcAsset>(call, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(result?.PublicName) ? null : result.PublicName;
        }
        catch (TrackerException ex)
        {
            _logger.LogDebug(ex, "Asset name lookup failed for {AssetId}.", asset.AssetId);
            return null;
        }
    }

    /// <summary>
    /// Page match history until we have <paramref name="wanted"/> matches or the service
    /// runs out.
    /// </summary>
    /// <remarks>
    /// Three separate things stop this loop, and all three are needed.
    ///
    ///   * An empty page, and a SHORT page. A service that returns fewer rows than asked
    ///     for has no more rows, and continuing to ask is how a paging loop turns into an
    ///     accidental denial of service against somebody else's servers.
    ///
    ///   * A page that adds nothing new. The cursor is a start/count offset, so a service
    ///     (or a proxy in front of one) that ignores <c>start</c> answers every request
    ///     with the same page: full-length, entirely duplicate, forever. Counting only new
    ///     matches against <paramref name="wanted"/> means such a loop would never
    ///     terminate on its own -- it would just keep asking 343 for page one until the
    ///     process was killed.
    ///
    /// The cursor advances by the number of rows the service actually returned rather than
    /// by the page size, because the last page is deliberately asked for short: advancing
    /// by the page size there skips the matches between the two.
    /// </remarks>
    public async Task<IReadOnlyList<HaloMatchHistoryResult>> GetRecentMatchesAsync(
        string xuid,
        int wanted,
        CancellationToken ct = default)
    {
        var collected = new List<HaloMatchHistoryResult>(Math.Max(0, wanted));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSize = Math.Clamp(_options.HistoryPageSize, 1, 25);

        var start = 0;
        while (collected.Count < wanted)
        {
            var take = Math.Min(pageSize, wanted - collected.Count);
            var page = await GetMatchHistoryAsync(xuid, start, take, ct: ct).ConfigureAwait(false);
            var results = page.Matches;
            if (results.Count == 0)
            {
                break;
            }

            var added = 0;
            foreach (var match in results)
            {
                // A career that is still being played shifts under a start/count cursor:
                // finish a game between page 1 and page 2 and the boundary match is
                // returned twice. Dedupe rather than double-count it into the trend.
                if (!string.IsNullOrEmpty(match.MatchId) && seen.Add(match.MatchId))
                {
                    collected.Add(match);
                    added++;
                }
            }

            if (results.Count < take)
            {
                break;
            }

            if (added == 0)
            {
                // A whole page we have already seen means the cursor did not move. Asking
                // again with a larger offset is the same request; stop instead of looping.
                _logger.LogWarning(
                    "Match history page at start={Start} for this player contained no new matches; stopping rather than paging further.",
                    start);
                break;
            }

            start += results.Count;
        }

        return collected;
    }

    private static Dictionary<string, string> PlayerArg(string xuid) =>
        new(StringComparer.Ordinal) { ["player"] = Identity.XuidRef(xuid) };
}
