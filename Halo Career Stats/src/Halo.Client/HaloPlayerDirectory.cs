using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Eet.Halo.Client.Http;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;

namespace Eet.Halo.Client;

/// <summary>Turns whatever the user typed into a <see cref="Player"/>.</summary>
public interface IHaloPlayerDirectory
{
    Task<Player?> ResolveAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Shared logic for both directories: recognising an XUID, and explaining the failure mode
/// that makes gamertag lookup unreliable in the first place.
/// </summary>
public static class HaloPlayerQuery
{
    public const string Platform = "Xbox";

    /// <summary>
    /// True when the query is already an id -- bare digits, or the <c>xuid(...)</c> wrapper.
    /// XUIDs are 16-digit decimal numbers, so "12345678901234567" is an id and "Master1234"
    /// is not.
    /// </summary>
    public static bool IsXuid(string query, out string bare)
    {
        bare = Identity.BareXuid(query.Trim());
        return bare.Length >= 10
            && bare.Length <= 20
            && bare.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The remedy text for a gamertag that found nothing.
    ///
    /// This is the case <see cref="Identity"/> exists for. A tag containing a Cyrillic or
    /// Greek letter that renders as a Latin one cannot be produced from a keyboard, so
    /// searching for the typed version returns an empty result forever, with no error that
    /// says why. Most trackers stop at "player not found"; saying which character is the
    /// problem, or that one might be, is the difference between a dead end and a fix.
    /// </summary>
    public static string NotFoundRemedy(string query)
    {
        if (Identity.Explain(query) is { } explanation)
        {
            return explanation;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"No Xbox profile matches the gamertag \"{query}\". If you are sure it is right, the tag may contain a non-Latin letter that renders identically to one you typed -- a Cyrillic \u0415 looks exactly like a Latin E and cannot be typed on a normal keyboard. Look the player up by XUID instead; it is stable and unambiguous.");
    }
}

/// <summary>
/// Resolves gamertags against profile.xboxlive.com.
///
/// This deliberately uses the XboxLive relying party rather than the Halo one: the profile
/// service predates Halo Infinite and does not know what a Spartan token is. It is the same
/// first two steps of the token chain either way, which is the point
/// <see cref="IXboxAuth"/> makes in its own documentation.
/// </summary>
public sealed class XboxProfilePlayerDirectory : IHaloPlayerDirectory
{
    private const string ProfileHost = "https://profile.xboxlive.com";
    private const string WantedSettings = "Gamertag,GameDisplayPicRaw";

    private readonly HttpClient _http;
    private readonly IXboxAuth _auth;

    public XboxProfilePlayerDirectory(HttpClient http, IXboxAuth auth)
    {
        _http = http;
        _auth = auth;
    }

    public async Task<Player?> ResolveAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var selector = HaloPlayerQuery.IsXuid(query, out var bare)
            ? string.Create(CultureInfo.InvariantCulture, $"xuid({bare})")
            : string.Create(CultureInfo.InvariantCulture, $"gt({HaloCall.EscapeHaloValue(query.Trim())})");

        var token = await _auth.GetXstsTokenAsync(RelyingParty.XboxLive, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"{ProfileHost}/users/{selector}/profile/settings?settings={WantedSettings}"));
        request.Headers.TryAddWithoutValidation("Authorization", token.AuthorizationHeader);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // Null, not an exception. ICareerSource.ResolveAsync documents null as "nothing
            // matches", and a gamertag that does not exist is an ordinary answer to an
            // ordinary question -- it is a 404 to the caller, not a failure of the tracker.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException(
                $"The Xbox profile service returned HTTP {(int)response.StatusCode} for \"{query}\".",
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? "The XSTS token was rejected. Note this call needs the http://xboxlive.com relying party, not the Halo one."
                    : "Try again shortly; this is Microsoft's service, not 343's.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return FromProfileJson(body, query);
    }

    /// <summary>
    /// Shared with the fixture directory so both paths parse the same bytes the same way.
    /// </summary>
    internal static Player? FromProfileJson(string json, string query)
    {
        XboxProfileResponse? profile;
        try
        {
            profile = JsonSerializer.Deserialize<XboxProfileResponse>(json, HaloJson.Options);
        }
        catch (JsonException ex)
        {
            throw new TrackerException(
                "The Xbox profile response could not be read.",
                "profile.xboxlive.com returned something this client does not recognise.",
                ex);
        }

        var users = profile?.Users ?? [];
        if (users.Count == 0)
        {
            return null;
        }

        // With more than one profile in the payload -- which the fixture has, on purpose --
        // pick the one the query actually meant. Exact match first, then a homoglyph-folded
        // match, which is what makes a typed "Elissu" find a tag spelled with a Cyrillic U+0415.
        var user = HaloPlayerQuery.IsXuid(query, out var bare)
            ? users.FirstOrDefault(u => string.Equals(u.Id, bare, StringComparison.Ordinal))
            : users.FirstOrDefault(u => string.Equals(u.Setting("Gamertag"), query, StringComparison.OrdinalIgnoreCase))
              ?? users.FirstOrDefault(u => u.Setting("Gamertag") is { } tag && Identity.LooksTheSame(tag, query));

        if (user?.Id is null)
        {
            return null;
        }

        var gamertag = user.Setting("Gamertag");
        return new Player(
            Handle: string.IsNullOrWhiteSpace(gamertag) ? user.Id : gamertag,
            Id: user.Id,
            Platform: HaloPlayerQuery.Platform,
            IconUrl: user.Setting("GameDisplayPicRaw"));
    }
}

/// <summary>
/// Resolves against the recorded profile fixture, so search works with no credentials.
///
/// An XUID query always succeeds even when it is not in the fixture: the id is the thing
/// the API keys on, so there is nothing to look up. That keeps
/// <c>/api/career?player=2814...</c> working for any id, which is what the homoglyph
/// advice tells people to do.
/// </summary>
public sealed class FixturePlayerDirectory : IHaloPlayerDirectory
{
    private readonly FixtureHaloTransport _fixtures;

    public FixturePlayerDirectory(FixtureHaloTransport fixtures) => _fixtures = fixtures;

    public Task<Player?> ResolveAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        var json = _fixtures.TryGetProfileJson();
        var resolved = json is null ? null : XboxProfilePlayerDirectory.FromProfileJson(json, query.Trim());

        if (resolved is not null)
        {
            return Task.FromResult<Player?>(resolved);
        }

        if (HaloPlayerQuery.IsXuid(query, out var bare))
        {
            return Task.FromResult<Player?>(new Player(bare, bare, HaloPlayerQuery.Platform));
        }

        // Same contract as the live directory: nothing matched is null, not an exception.
        return Task.FromResult<Player?>(null);
    }
}
