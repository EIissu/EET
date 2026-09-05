using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Eet.Trackers.Core;
using Eet.Xbox.Wire;

namespace Eet.Xbox;

/// <summary>
/// The live Xbox Live token chain: Azure AD, then Xbox user token, then XSTS, then -- for
/// Halo only -- a Spartan token.
///
/// Everything is cached in memory for the life of the instance and renewed two minutes
/// early (the margin baked into <c>XstsToken.IsExpired</c> and <c>SpartanToken.IsExpired</c>
/// in the shared contract). The expensive part of the chain is step 1: steps 2 and 3 are
/// two fast POSTs, but step 1 can mean a human typing a code into a browser, so the user
/// token is cached and reused across relying parties. Asking for the achievements XSTS
/// token after the Halo one therefore costs exactly one request.
///
/// Thread safety: one semaphore guards the whole chain. These are cheap, infrequent calls,
/// and the alternative -- two concurrent dashboard loads each starting their own device
/// code sign-in -- is a genuinely bad user experience.
/// </summary>
public sealed class XboxAuth : IXboxAuth, IDisposable
{
    private readonly HttpClient _http;
    private readonly XboxOptions _options;
    private readonly MicrosoftIdentityClient _identity;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, XstsToken> _xsts = new(StringComparer.Ordinal);

    private MicrosoftAccessToken? _accessToken;
    private CachedUserToken? _userToken;
    private SpartanToken? _spartan;

    public XboxAuth(
        HttpClient http,
        XboxOptions options,
        IRefreshTokenStore? store = null,
        IDeviceCodePrompt? prompt = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        _identity = new MicrosoftIdentityClient(http, options, store, prompt, _clock);
    }

    /// <summary>False here by definition -- see <see cref="FixtureXboxAuth"/> for the other case.</summary>
    public bool IsFixture => false;

    public async Task<XstsToken> GetXstsTokenAsync(string relyingParty, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingParty);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_xsts.TryGetValue(relyingParty, out var cached) && !IsExpired(cached.ExpiresAt))
            {
                return cached;
            }

            var userToken = await GetUserTokenAsync(ct).ConfigureAwait(false);
            var token = await AuthorizeAsync(userToken, relyingParty, ct).ConfigureAwait(false);
            _xsts[relyingParty] = token;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct = default)
    {
        // Taken before the gate: GetXstsTokenAsync takes the same one, and a semaphore that
        // is not reentrant would deadlock on the nested acquisition.
        var xsts = await GetXstsTokenAsync(RelyingParty.Halo, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_spartan is not null && !IsExpired(_spartan.ExpiresAt))
            {
                return _spartan;
            }

            _spartan = await ExchangeForSpartanTokenAsync(xsts, ct).ConfigureAwait(false);
            return _spartan;
        }
        finally
        {
            _gate.Release();
        }
    }

    // -----------------------------------------------------------------------------------
    // Step 2: user.auth.xboxlive.com/user/authenticate
    // -----------------------------------------------------------------------------------

    private async Task<string> GetUserTokenAsync(CancellationToken ct)
    {
        if (_userToken is not null && !IsExpired(_userToken.ExpiresAt))
        {
            return _userToken.Token;
        }

        if (_accessToken is null || _accessToken.IsExpired(_clock.GetUtcNow()))
        {
            _accessToken = await _identity.AcquireAsync(ct).ConfigureAwait(false);
        }

        var request = new XblUserAuthRequest
        {
            Properties = new XblUserAuthProperties
            {
                // The "d=" prefix marks this as a delegated Azure AD token rather than a
                // legacy MSA ticket. Without it the endpoint answers 400 with an empty body,
                // which reads exactly like a malformed request and sends people hunting in
                // the wrong place. It is the single most common way this step is got wrong.
                RpsTicket = "d=" + _accessToken.AccessToken,
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, XboxEndpoints.UserAuthenticate)
        {
            Content = JsonBody(request),
        };
        message.Headers.Add("x-xbl-contract-version", "1");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        await XstsErrors.EnsureAuthorizedAsync(response, "Xbox user authentication", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = Parse<XblTokenResponse>(body, "Xbox user authentication");

        if (string.IsNullOrEmpty(parsed.Token))
        {
            throw new TrackerException(
                "Xbox user authentication succeeded but returned no token.",
                "Retry once. If it repeats, the Azure AD access token is probably missing the " +
                "XboxLive.signin scope.");
        }

        var expiresAt = XboxJson.ParseTimestamp(parsed.NotAfter) ?? _clock.GetUtcNow().AddHours(1);
        _userToken = new CachedUserToken(parsed.Token, expiresAt);
        return parsed.Token;
    }

    // -----------------------------------------------------------------------------------
    // Step 3: xsts.auth.xboxlive.com/xsts/authorize
    // -----------------------------------------------------------------------------------

    private async Task<XstsToken> AuthorizeAsync(string userToken, string relyingParty, CancellationToken ct)
    {
        var request = new XstsAuthorizeRequest
        {
            Properties = new XstsAuthorizeProperties
            {
                SandboxId = _options.SandboxId,
                UserTokens = new[] { userToken },
            },
            RelyingParty = relyingParty,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, XboxEndpoints.XstsAuthorize)
        {
            Content = JsonBody(request),
        };
        message.Headers.Add("x-xbl-contract-version", "1");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);

        // This is where the account-level refusals land: no Xbox profile, child account,
        // unsupported country, ban. All of them are 401s with an XErr body.
        await XstsErrors.EnsureAuthorizedAsync(response, "XSTS authorization", ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = Parse<XblTokenResponse>(body, "XSTS authorization");

        var claim = parsed.DisplayClaims?.Xui?.FirstOrDefault();

        if (string.IsNullOrEmpty(parsed.Token) || string.IsNullOrEmpty(claim?.Uhs))
        {
            throw new TrackerException(
                "XSTS returned a response without both a token and a user hash.",
                "Both halves are required: the Authorization header is \"XBL3.0 x={uhs};{token}\" " +
                "and Xbox rejects a request carrying only the token.");
        }

        return new XstsToken(
            parsed.Token,
            claim.Uhs,
            XboxJson.ParseTimestamp(parsed.NotAfter) ?? _clock.GetUtcNow().AddHours(1),
            string.IsNullOrEmpty(claim.Xid) ? null : claim.Xid);
    }

    // -----------------------------------------------------------------------------------
    // Step 4: settings.svc.halowaypoint.com/spartan-token
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Trade a Halo-audience XSTS token for a Spartan token.
    ///
    /// Note this endpoint does NOT take an Authorization header -- the manifest lists it
    /// under the "settings_noauth" authority, and the XSTS token travels inside the body as
    /// a proof instead. Sending it as a header as well is harmless but pointless.
    /// </summary>
    private async Task<SpartanToken> ExchangeForSpartanTokenAsync(XstsToken xsts, CancellationToken ct)
    {
        var request = new SpartanTokenRequest
        {
            Proof = new[] { new SpartanTokenProof { Token = xsts.Token } },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, XboxEndpoints.SpartanToken)
        {
            Content = JsonBody(request),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var failure = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw XstsErrors.Translate(response.StatusCode, failure, "Spartan token exchange");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = Parse<SpartanTokenResponse>(body, "Spartan token exchange");

        if (string.IsNullOrEmpty(parsed.SpartanToken))
        {
            throw new TrackerException(
                "The Halo token endpoint returned no Spartan token.",
                "Confirm the XSTS token was issued for the relying party " +
                "\"https://prod.xsts.halowaypoint.com/\". An XSTS token for http://xboxlive.com " +
                "is accepted at this endpoint and then silently produces nothing useful.");
        }

        return new SpartanToken(parsed.SpartanToken, ResolveSpartanExpiry(parsed, _clock.GetUtcNow()));
    }

    /// <summary>
    /// Work out when a Spartan token dies, from whichever of the two fields the service
    /// filled in. ExpiresUtc is an object wrapping an ISO 8601 string rather than a bare
    /// string, and TokenDuration is a .NET-formatted TimeSpan; if neither parses, assume a
    /// conservative hour so the client renews too often rather than serving a dead token.
    /// </summary>
    internal static DateTimeOffset ResolveSpartanExpiry(SpartanTokenResponse response, DateTimeOffset now)
    {
        var explicitExpiry = XboxJson.ParseTimestamp(response.ExpiresUtc?.Value);
        if (explicitExpiry is not null)
        {
            return explicitExpiry.Value;
        }

        if (TimeSpan.TryParse(response.TokenDuration, CultureInfo.InvariantCulture, out var duration)
            && duration > TimeSpan.Zero)
        {
            return now.Add(duration);
        }

        return now.AddHours(1);
    }

    // -----------------------------------------------------------------------------------

    private bool IsExpired(DateTimeOffset expiresAt) =>
        _clock.GetUtcNow() >= expiresAt - TimeSpan.FromMinutes(2);

    private static StringContent JsonBody<T>(T value) =>
        new(JsonSerializer.Serialize(value, XboxJson.Write), Encoding.UTF8, "application/json");

    private static T Parse<T>(string body, string stage)
    {
        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(body, XboxJson.Read);
        }
        catch (JsonException ex)
        {
            throw new TrackerException(
                string.Create(CultureInfo.InvariantCulture, $"{stage}: the response was not valid JSON."),
                "This usually means a proxy or captive portal answered instead of the service.",
                ex);
        }

        return parsed ?? throw new TrackerException(
            string.Create(CultureInfo.InvariantCulture, $"{stage}: the response was empty."),
            "Retry once; an empty body from these endpoints is normally transient.");
    }

    public void Dispose() => _gate.Dispose();

    private sealed record CachedUserToken(string Token, DateTimeOffset ExpiresAt);
}
