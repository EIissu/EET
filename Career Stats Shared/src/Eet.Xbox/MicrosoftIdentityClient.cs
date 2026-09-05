using System.Globalization;
using System.Text.Json;
using Eet.Trackers.Core;
using Eet.Xbox.Wire;

namespace Eet.Xbox;

/// <summary>What the user has to do, and where, to finish signing in.</summary>
/// <param name="UserCode">The short code the user types on the verification page.</param>
/// <param name="VerificationUri">Where they type it.</param>
/// <param name="Message">Microsoft's own wording, if it sent any.</param>
public sealed record DeviceCodeChallenge(
    string UserCode,
    string VerificationUri,
    TimeSpan ExpiresIn,
    string? Message)
{
    /// <summary>One line the caller can print without composing anything itself.</summary>
    public string Instruction => Message ?? string.Create(
        CultureInfo.InvariantCulture,
        $"To sign in, open {VerificationUri} and enter the code {UserCode}.");
}

/// <summary>
/// How the user is told to go and authenticate. A console app prints it, a web app renders
/// it, a test records it -- the token chain does not care which.
/// </summary>
public interface IDeviceCodePrompt
{
    Task PresentAsync(DeviceCodeChallenge challenge, CancellationToken ct = default);
}

/// <summary>Prints the challenge to standard output. The default for a local tool.</summary>
public sealed class ConsoleDeviceCodePrompt : IDeviceCodePrompt
{
    public Task PresentAsync(DeviceCodeChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        Console.WriteLine(challenge.Instruction);
        return Task.CompletedTask;
    }
}

/// <summary>An Azure AD access token and when it stops working.</summary>
public sealed record MicrosoftAccessToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Two minutes of slack, matching the rest of the token types in this chain.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt - TimeSpan.FromMinutes(2);
}

/// <summary>
/// Step 1 of the chain: the OAuth 2.0 device authorization grant against the Microsoft
/// identity platform.
///
/// Device code, not authorization code, and the reason is structural rather than stylistic:
/// the authorization code flow needs a redirect URI, which means either hosting an HTTP
/// listener on localhost or registering a custom URI scheme. Both are more moving parts
/// than a stat tracker should own, and neither works over SSH. The device code grant asks
/// the user to type a short code into a page on any device and needs no callback at all.
///
/// The other half of step 1 is not signing in again: XboxLive.offline_access buys a refresh
/// token, which is exchanged silently on every later run.
/// </summary>
public sealed class MicrosoftIdentityClient
{
    /// <summary>
    /// How many consecutive unreadable answers the poll loop rides out before giving up.
    /// </summary>
    private const int MaxUnreadablePolls = 5;

    private readonly HttpClient _http;
    private readonly XboxOptions _options;
    private readonly IRefreshTokenStore _store;
    private readonly IDeviceCodePrompt _prompt;
    private readonly TimeProvider _clock;

    public MicrosoftIdentityClient(
        HttpClient http,
        XboxOptions options,
        IRefreshTokenStore? store = null,
        IDeviceCodePrompt? prompt = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? new RefreshTokenStore();
        _prompt = prompt ?? new ConsoleDeviceCodePrompt();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Get an access token: silently from the cached refresh token if one works, otherwise
    /// by asking the user to complete a device code sign-in.
    /// </summary>
    public async Task<MicrosoftAccessToken> AcquireAsync(CancellationToken ct = default)
    {
        var clientId = _options.RequireClientId();

        var cached = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (cached is not null && string.Equals(cached.ClientId, clientId, StringComparison.Ordinal))
        {
            var refreshed = await TryRefreshAsync(clientId, cached.RefreshToken, ct).ConfigureAwait(false);

            if (refreshed.Token is not null)
            {
                return refreshed.Token;
            }

            if (!refreshed.RefreshTokenIsDead)
            {
                // We could not tell whether the refresh token is any good -- the token
                // endpoint answered with something that was not a token response at all.
                // Deleting a working credential because a proxy returned an error page,
                // or because Azure AD was briefly down, logs the owner out of their own
                // tool permanently: the file is gone, and only an interactive browser
                // sign-in gets it back. So keep it, say what happened, and let the next
                // run try again.
                throw new TrackerException(
                    "Could not reach the Microsoft identity platform to renew the Xbox sign-in.",
                    "The cached refresh token has been kept -- this looks like a transport or " +
                    "service problem rather than a rejected credential. Retry in a minute. If it " +
                    "persists, check for a proxy or captive portal intercepting " +
                    "login.microsoftonline.com.");
            }

            // Azure AD said invalid_grant: the refresh token really is spent. Do not keep a
            // dead credential on disk, and fall through to an interactive sign-in.
            await _store.ClearAsync(ct).ConfigureAwait(false);
        }

        return await SignInInteractivelyAsync(clientId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The three things that can come back from a refresh, which the caller must tell apart:
    /// a token, a refusal of the token itself, and "we could not tell".
    /// </summary>
    /// <param name="Token">The access token, when the exchange worked.</param>
    /// <param name="RefreshTokenIsDead">
    /// True only when Azure AD explicitly said <c>invalid_grant</c>. Anything else -- an
    /// HTML error page from a proxy, a 503, an empty body -- leaves this false, because a
    /// refresh token must never be deleted on the strength of a failure we cannot read.
    /// </param>
    private readonly record struct RefreshOutcome(MicrosoftAccessToken? Token, bool RefreshTokenIsDead);

    /// <summary>
    /// Exchange a refresh token for an access token. Does not throw for the recoverable
    /// case -- a spent refresh token -- because the caller handles that by signing in again.
    /// </summary>
    private async Task<RefreshOutcome> TryRefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = _options.Scope,
        };

        var response = await PostFormAsync(TokenUri, form, ct).ConfigureAwait(false);

        if (response is null)
        {
            // Unreadable, so unclassifiable. Explicitly not "dead".
            return new RefreshOutcome(null, RefreshTokenIsDead: false);
        }

        if (!string.IsNullOrEmpty(response.Error))
        {
            // invalid_grant is the expected "this refresh token is spent" answer. Anything
            // else is a configuration problem the operator should hear about.
            if (string.Equals(response.Error, "invalid_grant", StringComparison.Ordinal))
            {
                return new RefreshOutcome(null, RefreshTokenIsDead: true);
            }

            throw ConfigurationFailure(response);
        }

        if (string.IsNullOrEmpty(response.AccessToken))
        {
            // A 200 with neither an access token nor an error is not a rejection either.
            return new RefreshOutcome(null, RefreshTokenIsDead: false);
        }

        await PersistAsync(clientId, response, ct).ConfigureAwait(false);
        return new RefreshOutcome(ToAccessToken(response), RefreshTokenIsDead: false);
    }

    private async Task<MicrosoftAccessToken> SignInInteractivelyAsync(string clientId, CancellationToken ct)
    {
        var startForm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["scope"] = _options.Scope,
        };

        var deviceCodeUri = new Uri(
            string.Format(CultureInfo.InvariantCulture, XboxEndpoints.DeviceCodeFormat, _options.Tenant));

        using var startContent = new FormUrlEncodedContent(startForm);
        using var startResponse = await _http.PostAsync(deviceCodeUri, startContent, ct).ConfigureAwait(false);

        var startBody = await startResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!startResponse.IsSuccessStatusCode)
        {
            throw new TrackerException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Azure AD refused to start a device code sign-in (HTTP {(int)startResponse.StatusCode})."),
                "Check the client id, and that the app registration is a public client with " +
                "\"Allow public client flows\" enabled. Confidential clients cannot use the device " +
                "code grant at all.");
        }

        var start = Deserialize<DeviceCodeResponse>(startBody);

        if (start?.DeviceCode is null || start.UserCode is null || start.VerificationUri is null)
        {
            throw new TrackerException(
                "Azure AD returned a device code response without a device code.",
                "This is unexpected. Retry once; if it repeats, the app registration is probably " +
                "not configured for public client flows.");
        }

        await _prompt.PresentAsync(
                new DeviceCodeChallenge(
                    start.UserCode,
                    start.VerificationUri,
                    TimeSpan.FromSeconds(start.ExpiresIn <= 0 ? 900 : start.ExpiresIn),
                    start.Message),
                ct)
            .ConfigureAwait(false);

        return await PollForTokenAsync(clientId, start, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Poll the token endpoint until the user finishes, refuses, or runs out of time.
    /// The interval comes from the service, and doubles when it says "slow_down" -- polling
    /// faster than told is how a client gets throttled out of a sign-in it would have won.
    /// </summary>
    private async Task<MicrosoftAccessToken> PollForTokenAsync(
        string clientId,
        DeviceCodeResponse start,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = start.DeviceCode!,
        };

        var interval = TimeSpan.FromSeconds(start.Interval <= 0 ? 5 : start.Interval);
        var deadline = _clock.GetUtcNow().AddSeconds(start.ExpiresIn <= 0 ? 900 : start.ExpiresIn);
        var unreadable = 0;

        while (_clock.GetUtcNow() < deadline)
        {
            // Task.Delay's TimeProvider overload, so a test can drive the poll loop
            // without spending five real seconds per iteration.
            await Task.Delay(interval, _clock, ct).ConfigureAwait(false);

            var response = await PostFormAsync(TokenUri, form, ct).ConfigureAwait(false);

            if (response is null)
            {
                // A body that is not a token response. One is a blip worth riding out; a
                // run of them is something answering that is not the token endpoint, and
                // polling it for the rest of the device code's fifteen-minute life helps
                // nobody. The deadline alone is not a bound worth relying on: it is driven
                // by the injected clock, so under a clock that does not advance on its own
                // this loop does not terminate at all.
                if (++unreadable >= MaxUnreadablePolls)
                {
                    throw new TrackerException(
                        "The token endpoint kept answering with something that was not a token response.",
                        "Check for a proxy or captive portal intercepting login.microsoftonline.com, " +
                        "then start the sign-in again.");
                }

                continue;
            }

            unreadable = 0;

            if (string.IsNullOrEmpty(response.Error))
            {
                await PersistAsync(clientId, response, ct).ConfigureAwait(false);
                return ToAccessToken(response);
            }

            switch (response.Error)
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "authorization_declined":
                    throw new TrackerException(
                        "The sign-in was declined on the verification page.",
                        "Run the sign-in again and approve the request to continue.");

                case "expired_token":
                    throw new TrackerException(
                        "The device code expired before the sign-in was completed.",
                        "Run the sign-in again and enter the code within the time shown.");

                case "bad_verification_code":
                    throw new TrackerException(
                        "Azure AD rejected the device code.",
                        "Start the sign-in again from the beginning; a device code cannot be reused.");

                default:
                    throw ConfigurationFailure(response);
            }
        }

        throw new TrackerException(
            "The device code sign-in timed out.",
            "Run the sign-in again and complete it in the browser within the time shown.");
    }

    private Uri TokenUri =>
        new(string.Format(CultureInfo.InvariantCulture, XboxEndpoints.TokenFormat, _options.Tenant));

    /// <summary>
    /// POST a form and read the body whether it succeeded or not: the device code flow puts
    /// "authorization_pending" behind an HTTP 400, so a status check alone loses it.
    /// </summary>
    private async Task<OAuthTokenResponse?> PostFormAsync(
        Uri uri,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(uri, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Deserialize<OAuthTokenResponse>(body);
    }

    private async Task PersistAsync(string clientId, OAuthTokenResponse response, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            return;
        }

        await _store.SaveAsync(
                new CachedRefreshToken
                {
                    RefreshToken = response.RefreshToken,
                    ClientId = clientId,
                    Scope = response.Scope ?? _options.Scope,
                    ObtainedAt = _clock.GetUtcNow(),
                },
                ct)
            .ConfigureAwait(false);
    }

    private MicrosoftAccessToken ToAccessToken(OAuthTokenResponse response)
    {
        if (string.IsNullOrEmpty(response.AccessToken))
        {
            throw new TrackerException(
                "Azure AD returned a token response with no access token in it.",
                "Confirm the app registration grants the delegated XboxLive.signin permission.");
        }

        // Azure AD access tokens are an hour; treat a missing expires_in as a short one
        // rather than a long one, because renewing early is free and renewing late is not.
        var lifetime = TimeSpan.FromSeconds(response.ExpiresIn <= 0 ? 300 : response.ExpiresIn);
        return new MicrosoftAccessToken(response.AccessToken, _clock.GetUtcNow().Add(lifetime));
    }

    private static TrackerException ConfigurationFailure(OAuthTokenResponse response) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"Azure AD refused the sign-in: {response.Error}."),
            string.IsNullOrWhiteSpace(response.ErrorDescription)
                ? "Check the client id, tenant and requested scope against the app registration."
                : response.ErrorDescription);

    private static T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, XboxJson.Read);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
