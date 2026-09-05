using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// The live achievements client.
///
/// Everything here rides on one XSTS token for <c>http://xboxlive.com</c>, which is the
/// same first three steps of the chain Halo needs -- only the relying party at step 3
/// differs. That is why achievements cost almost nothing once the Halo path works.
///
/// Three details that are easy to get wrong and expensive to debug:
///
///   * The Authorization header is "XBL3.0 x={uhs};{token}". A request carrying only the
///     token is rejected with a bare 401.
///   * The contract version differs per host: 2 for achievements and the title hub, 3 for
///     profile. Sending the wrong one produces a 400 that says nothing.
///   * The achievements endpoint pages, and its default page is smaller than most titles.
///     Not following "pagingInfo.continuationToken" silently truncates a player's history,
///     which is the worst kind of bug: it looks like data.
/// </summary>
public sealed class XboxAchievementsClient : IXboxAchievements
{
    /// <summary>
    /// A stop on the pagination loop. Xbox has never returned a cycle, but a client that
    /// trusts a server-supplied continuation token unconditionally can be made to loop
    /// forever by one.
    /// </summary>
    private const int MaxPages = 40;

    /// <summary>The settings the dashboard needs. Asking for more is a slower request.</summary>
    private const string ProfileSettings = "Gamertag,GameDisplayPicRaw,Gamerscore";

    /// <summary>
    /// One of the four values this endpoint accepts for <c>orderBy</c> -- the others are
    /// <c>Unordered</c> (the default), <c>Title</c> and <c>EndingSoon</c>.
    /// </summary>
    private const string UnlockTimeOrder = "UnlockTime";

    /// <summary>
    /// How long title metadata is trusted for.
    ///
    /// The title hub carries "last played" and the player's running gamerscore, which are
    /// exactly the values a career tracker exists to watch change. Cached for the life of
    /// the client -- which, in the API host, is the life of the process -- they would be
    /// read once at startup and never again, so the dashboard would show a last-played date
    /// that stops advancing while the player is still playing. Short enough that a session
    /// is never stale for long, long enough that one dashboard load does not fetch it twice.
    /// </summary>
    private static readonly TimeSpan TitleCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IXboxAuth _auth;
    private readonly XboxOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Title metadata, cached per player. Concurrent because a dashboard loading Halo and
    /// Destiny panels at once shares one client, and a plain Dictionary written from two
    /// async continuations can corrupt rather than merely race.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedTitles> _titleCache =
        new(StringComparer.Ordinal);

    public XboxAchievementsClient(
        HttpClient http,
        IXboxAuth auth,
        XboxOptions? options = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _options = options ?? new XboxOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsFixture => false;

    public async Task<XboxProfile?> ResolveGamertagAsync(string gamertag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamertag);

        // Escaped, not interpolated raw: gamertags legitimately contain spaces and non-ASCII
        // letters. Xbox then matches what arrives byte for byte, which is why a homoglyph tag
        // returns a 404 for the version a person can actually type -- see Identity in
        // Eet.Trackers.Core, and prefer looking such a player up by XUID.
        var tag = Uri.EscapeDataString(gamertag);

        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{XboxEndpoints.Profile}/users/gt({tag})/profile/settings?settings={ProfileSettings}"));

        using var response = await SendAsync(uri, contractVersion: "3", acceptLanguage: false, ct)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureUsableAsync(response, "Xbox profile lookup", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return AchievementMapper.MapProfile(body);
    }

    public async Task<TitleAchievements> GetTitleAchievementsAsync(
        string xuid,
        string titleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);

        var bare = Identity.BareXuid(xuid);
        var achievements = new List<Achievement>();
        string? continuation = null;
        var page = 0;

        do
        {
            var uri = AchievementsUri(bare, titleId, continuation);
            var body = await GetStringAsync(uri, "2", acceptLanguage: false, "Xbox achievements", ct)
                .ConfigureAwait(false);

            achievements.AddRange(AchievementMapper.MapAchievements(body, titleId));
            continuation = AchievementMapper.ContinuationToken(body);
            page++;
        }
        while (continuation is not null && page < MaxPages);

        var metadata = await TryGetTitleMetadataAsync(bare, titleId, ct).ConfigureAwait(false);
        return AchievementMapper.Summarise(titleId, achievements, metadata);
    }

    public async Task<IReadOnlyList<Achievement>> GetRecentAchievementsAsync(
        string xuid,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);

        var bare = Identity.BareXuid(xuid);
        var results = new List<Achievement>(maxItems);
        string? continuation = null;
        var page = 0;

        do
        {
            // orderBy and unlockedOnly are what make this a RECENT feed rather than the
            // first page of an arbitrary list. The service defaults to orderBy=Unordered
            // and unlockedOnly=false, so asking for neither returns the player's whole
            // achievement catalogue -- locked entries included -- in no particular order,
            // and truncating that to maxItems produces a "what have I done lately" panel
            // full of things they have never done.
            var uri = AchievementsUri(
                bare,
                titleId: null,
                continuation,
                unlockedOnly: true,
                orderBy: UnlockTimeOrder,
                maxItems: maxItems);

            var body = await GetStringAsync(uri, "2", acceptLanguage: false, "Xbox recent achievements", ct)
                .ConfigureAwait(false);

            results.AddRange(AchievementMapper.MapAchievements(body));
            continuation = AchievementMapper.ContinuationToken(body);
            page++;
        }
        while (continuation is not null && results.Count < maxItems && page < MaxPages);

        // Sorted here as well as requested there. The REST reference documents the allowed
        // orderBy values but not their direction, and the interface promises newest first,
        // so the promise is kept locally rather than delegated. It also keeps this method
        // and FixtureXboxAchievements -- which sorts the same way -- returning the same
        // order for the same data, which is the only reason fixture output is evidence
        // about live output at all.
        results.Sort(static (left, right) =>
            (right.UnlockedAt ?? DateTimeOffset.MinValue).CompareTo(left.UnlockedAt ?? DateTimeOffset.MinValue));

        return results.Count > maxItems ? results.GetRange(0, maxItems) : results;
    }

    public async Task<IReadOnlyList<TitleMetadata>> GetTitleHistoryAsync(
        string xuid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);

        var titles = await GetTitlesAsync(Identity.BareXuid(xuid), ct).ConfigureAwait(false);
        return titles.Values.ToList();
    }

    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The title hub is a nice-to-have, not a dependency: it supplies the game's name and
    /// when it was last played, and both have sensible fallbacks. So a failure here
    /// degrades the answer instead of failing the call -- a private profile should still
    /// show you your own achievements.
    /// </summary>
    private async Task<TitleMetadata?> TryGetTitleMetadataAsync(string xuid, string titleId, CancellationToken ct)
    {
        try
        {
            var titles = await GetTitlesAsync(xuid, ct).ConfigureAwait(false);
            return titles.TryGetValue(titleId, out var metadata) ? metadata : null;
        }
        catch (TrackerException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, TitleMetadata>> GetTitlesAsync(
        string xuid,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        if (_titleCache.TryGetValue(xuid, out var cached) && now - cached.FetchedAt < TitleCacheLifetime)
        {
            return cached.Titles;
        }

        // The "decoration/achievement,scid" segment is what makes the achievement totals
        // non-null. Without it every title comes back with zero gamerscore, which looks
        // like a player who has never unlocked anything.
        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{XboxEndpoints.TitleHub}/users/xuid({xuid})/titles/titlehistory/decoration/achievement,scid"));

        var body = await GetStringAsync(uri, "2", acceptLanguage: true, "Xbox title history", ct)
            .ConfigureAwait(false);

        var titles = AchievementMapper.MapTitles(body);
        _titleCache[xuid] = new CachedTitles(titles, _clock.GetUtcNow());
        return titles;
    }

    /// <summary>Title metadata plus when it was read, so it can be allowed to go stale.</summary>
    private sealed record CachedTitles(
        IReadOnlyDictionary<string, TitleMetadata> Titles,
        DateTimeOffset FetchedAt);

    /// <summary>
    /// Build an achievements URL.
    ///
    /// Parameter names and the allowed <c>orderBy</c> values come from the Xbox Live REST
    /// reference for this URI at contract version 2, not from guesswork: <c>skipItems</c>,
    /// <c>continuationToken</c>, <c>maxItems</c>, <c>titleId</c>, <c>unlockedOnly</c>,
    /// <c>types</c>, <c>orderBy</c>. Both defaults matter -- <c>orderBy</c> defaults to
    /// <c>Unordered</c> and <c>unlockedOnly</c> to <c>false</c> -- so anything wanting a
    /// time-ordered list of unlocks has to say so.
    /// </summary>
    private static Uri AchievementsUri(
        string xuid,
        string? titleId,
        string? continuationToken,
        bool unlockedOnly = false,
        string? orderBy = null,
        int? maxItems = null)
    {
        var query = new List<string>(5);

        if (!string.IsNullOrEmpty(titleId))
        {
            query.Add("titleId=" + Uri.EscapeDataString(titleId));
        }

        if (unlockedOnly)
        {
            query.Add("unlockedOnly=true");
        }

        if (!string.IsNullOrEmpty(orderBy))
        {
            query.Add("orderBy=" + Uri.EscapeDataString(orderBy));
        }

        if (maxItems is > 0)
        {
            query.Add(string.Create(CultureInfo.InvariantCulture, $"maxItems={maxItems.Value}"));
        }

        if (!string.IsNullOrEmpty(continuationToken))
        {
            query.Add("continuationToken=" + Uri.EscapeDataString(continuationToken));
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);

        return new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{XboxEndpoints.Achievements}/users/xuid({xuid})/achievements{suffix}"));
    }

    private async Task<string> GetStringAsync(
        Uri uri,
        string contractVersion,
        bool acceptLanguage,
        string stage,
        CancellationToken ct)
    {
        using var response = await SendAsync(uri, contractVersion, acceptLanguage, ct).ConfigureAwait(false);
        await EnsureUsableAsync(response, stage, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        string contractVersion,
        bool acceptLanguage,
        CancellationToken ct)
    {
        var xsts = await _auth.GetXstsTokenAsync(RelyingParty.XboxLive, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", xsts.AuthorizationHeader);
        request.Headers.Add("x-xbl-contract-version", contractVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (acceptLanguage)
        {
            // The title hub localises game names by this header and returns them in a
            // machine-dependent language without it, which makes fixtures unreproducible.
            request.Headers.TryAddWithoutValidation("Accept-Language", _options.AcceptLanguage);
        }

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Xbox's data services answer 403 for a private profile, which is a privacy setting
    /// rather than an error, and deserves saying so.
    /// </summary>
    private static async Task EnsureUsableAsync(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new TrackerException(
                string.Create(CultureInfo.InvariantCulture, $"{stage}: Xbox refused to share this player's data."),
                "The player's Xbox privacy settings hide their achievements, or the signed-in " +
                "account is not permitted to see them. Change \"Others can see your game and app " +
                "history\" at https://account.xbox.com/settings, or look up your own account.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw XstsErrors.Translate(response.StatusCode, body, stage);
    }
}
