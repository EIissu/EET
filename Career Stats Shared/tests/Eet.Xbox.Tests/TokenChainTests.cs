using System.Net;
using System.Text.Json;
using Eet.Trackers.Core;

namespace Eet.Xbox.Tests;

/// <summary>
/// What the three token exchanges actually put on the wire.
///
/// These assert on request bodies rather than on the returned token, because the returned
/// token is whatever the stub says it is -- the part that can genuinely be wrong is the
/// shape of what we send, and every one of these assertions corresponds to a documented
/// way the chain fails silently when got wrong.
/// </summary>
public sealed class TokenChainTests
{
    private static XboxOptions Options => new()
    {
        ClientId = "00000000-0000-0000-0000-00000000c0de",
        Tenant = "consumers",
    };

    /// <summary>
    /// A chain wired to a movable clock.
    ///
    /// The lifetime is a TimeSpan rather than an absolute instant on purpose: the clock the
    /// XboxAuth uses is created in here, so a test that computed an expiry from its own
    /// separate clock and then advanced that one would be moving a clock nothing reads, and
    /// would silently assert nothing.
    /// </summary>
    private static (StubHandler Stub, XboxAuth Auth, TestClock Clock) Arrange(
        TimeSpan? xstsLifetime = null,
        string? spartanBody = null)
    {
        var clock = new TestClock();

        var stub = new StubHandler()
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess())
            .Route("user.auth.xboxlive.com", Responses.UserAuthenticate())
            .Route(
                "xsts.auth.xboxlive.com",
                Responses.XstsAuthorize(clock.GetUtcNow().Add(xstsLifetime ?? TimeSpan.FromHours(4))))
            .Route("spartan-token", spartanBody ?? Responses.SpartanToken());

        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken
            {
                RefreshToken = "stored-refresh-token",
                ClientId = Options.ClientId!,
                ObtainedAt = clock.GetUtcNow().AddDays(-1),
            },
        };

        return (stub, new XboxAuth(stub.Client(), Options, store, new RecordingPrompt(), clock), clock);
    }

    [Fact]
    public async Task UserAuthenticate_sends_the_d_prefixed_rps_ticket()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);

        var request = stub.For("user.auth.xboxlive.com");
        var properties = request.Json.GetProperty("Properties");

        // The "d=" prefix is required for Azure AD tokens. Without it the endpoint answers
        // 400 with an empty body, which reads like a malformed request rather than a
        // missing two characters.
        Assert.Equal("d=azure-ad-access-token", properties.GetProperty("RpsTicket").GetString());
        Assert.Equal("RPS", properties.GetProperty("AuthMethod").GetString());
        Assert.Equal("user.auth.xboxlive.com", properties.GetProperty("SiteName").GetString());
        Assert.Equal("http://auth.xboxlive.com", request.Json.GetProperty("RelyingParty").GetString());
        Assert.Equal("JWT", request.Json.GetProperty("TokenType").GetString());
    }

    [Fact]
    public async Task UserAuthenticate_sends_contract_version_1_and_json_content_type()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);

        var request = stub.For("user.auth.xboxlive.com");
        Assert.Equal("1", request.Header("x-xbl-contract-version"));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xsts_sends_the_user_token_the_sandbox_and_the_requested_relying_party()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetXstsTokenAsync(RelyingParty.Halo);

        var request = stub.For("xsts.auth.xboxlive.com");
        var properties = request.Json.GetProperty("Properties");

        Assert.Equal("RETAIL", properties.GetProperty("SandboxId").GetString());
        Assert.Equal(
            Responses.UserToken,
            properties.GetProperty("UserTokens").EnumerateArray().Single().GetString());
        Assert.Equal(RelyingParty.Halo, request.Json.GetProperty("RelyingParty").GetString());
        Assert.Equal("JWT", request.Json.GetProperty("TokenType").GetString());
        Assert.Equal("1", request.Header("x-xbl-contract-version"));
    }

    [Fact]
    public async Task Xsts_result_carries_both_halves_of_the_authorization_header()
    {
        var (_, auth, _) = Arrange();
        using var _auth = auth;

        var token = await auth.GetXstsTokenAsync(RelyingParty.XboxLive);

        // A request carrying only the token is rejected. Both halves or nothing.
        Assert.Equal(Responses.UserHash, token.UserHash);
        Assert.Equal($"XBL3.0 x={Responses.UserHash};{Responses.XstsTokenValue}", token.AuthorizationHeader);
        Assert.Equal(Responses.Xuid, token.Xuid);
    }

    [Fact]
    public async Task Asking_for_a_second_relying_party_reuses_the_user_token()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetXstsTokenAsync(RelyingParty.Halo);
        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);

        // This is the whole reason achievements are nearly free once Halo works: steps 1
        // and 2 happen once, and only step 3 is repeated per relying party.
        Assert.Equal(1, stub.CountFor("user.auth.xboxlive.com"));
        Assert.Equal(1, stub.CountFor("/oauth2/v2.0/token"));
        Assert.Equal(2, stub.CountFor("xsts.auth.xboxlive.com"));
    }

    [Fact]
    public async Task An_unexpired_xsts_token_is_served_from_cache()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);
        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);

        Assert.Equal(1, stub.CountFor("xsts.auth.xboxlive.com"));
    }

    [Fact]
    public async Task An_xsts_token_is_renewed_before_it_expires_not_after()
    {
        var (stub, auth, clock) = Arrange(xstsLifetime: TimeSpan.FromMinutes(10));
        using var _auth = auth;

        var first = await auth.GetXstsTokenAsync(RelyingParty.XboxLive);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), first.ExpiresAt);

        // Seven minutes on: three minutes of life left, comfortably outside the margin, so
        // still served from cache.
        clock.Advance(TimeSpan.FromMinutes(7));
        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);
        Assert.Equal(1, stub.CountFor("xsts.auth.xboxlive.com"));

        // Nine minutes on: still a minute of life left, but now inside the two-minute
        // safety margin. A token renewed only once it has actually expired guarantees at
        // least one 401 per session, which is exactly what the margin exists to prevent.
        clock.Advance(TimeSpan.FromMinutes(2));
        await auth.GetXstsTokenAsync(RelyingParty.XboxLive);
        Assert.Equal(2, stub.CountFor("xsts.auth.xboxlive.com"));
    }

    [Fact]
    public async Task Spartan_token_request_has_the_audience_version_and_proof_343_expects()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetSpartanTokenAsync();

        var request = stub.For("spartan-token");

        Assert.Equal("urn:343:s3:services", request.Json.GetProperty("Audience").GetString());
        Assert.Equal("4", request.Json.GetProperty("MinVersion").GetString());

        var proof = request.Json.GetProperty("Proof").EnumerateArray().Single();
        Assert.Equal(Responses.XstsTokenValue, proof.GetProperty("Token").GetString());
        Assert.Equal("Xbox_XSTSv3", proof.GetProperty("TokenType").GetString());
    }

    [Fact]
    public async Task Spartan_token_is_obtained_from_a_halo_audience_xsts_token()
    {
        var (stub, auth, _) = Arrange();
        using var _auth = auth;

        await auth.GetSpartanTokenAsync();

        // An XSTS token for http://xboxlive.com is accepted by this endpoint and then
        // produces nothing useful, so which relying party was asked for is load bearing.
        var xsts = stub.For("xsts.auth.xboxlive.com");
        Assert.Equal(RelyingParty.Halo, xsts.Json.GetProperty("RelyingParty").GetString());
    }

    [Fact]
    public async Task Spartan_token_reads_the_object_wrapped_expiry()
    {
        var (_, auth, clock) = Arrange(spartanBody: Responses.SpartanToken(expires: "2026-09-04T16:00:00.000Z"));
        using var _auth = auth;

        var token = await auth.GetSpartanTokenAsync();

        // ExpiresUtc is {"ISO8601Date": "..."} rather than a bare string. Read as a string
        // it comes back null, the expiry silently falls back to "an hour from now", and the
        // real four-hour lifetime is thrown away.
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 16, 0, 0, TimeSpan.Zero), token.ExpiresAt);
        Assert.Equal(clock.GetUtcNow().AddHours(4), token.ExpiresAt);
    }

    [Fact]
    public async Task Spartan_token_also_accepts_a_flattened_expiry()
    {
        var (_, auth, _) = Arrange(spartanBody: Responses.SpartanTokenFlatExpiry());
        using var _auth = auth;

        var token = await auth.GetSpartanTokenAsync();

        Assert.Equal(new DateTimeOffset(2026, 9, 4, 16, 0, 0, TimeSpan.Zero), token.ExpiresAt);
    }

    [Fact]
    public async Task Spartan_token_falls_back_to_TokenDuration_when_there_is_no_expiry()
    {
        var body = """
            { "SpartanToken": "v4=fixture", "TokenDuration": "03:00:00" }
            """;

        var (_, auth, clock) = Arrange(spartanBody: body);
        using var _auth = auth;

        var token = await auth.GetSpartanTokenAsync();

        Assert.Equal(clock.GetUtcNow().AddHours(3), token.ExpiresAt);
    }

    [Fact]
    public async Task Spartan_token_is_cached_until_it_nears_expiry()
    {
        var (stub, auth, clock) = Arrange(spartanBody: Responses.SpartanToken(expires: "2026-09-04T13:00:00.000Z"));
        using var _auth = auth;

        await auth.GetSpartanTokenAsync();
        await auth.GetSpartanTokenAsync();
        Assert.Equal(1, stub.CountFor("spartan-token"));

        clock.Advance(TimeSpan.FromMinutes(59));
        await auth.GetSpartanTokenAsync();
        Assert.Equal(2, stub.CountFor("spartan-token"));
    }

    [Fact]
    public async Task A_missing_client_id_is_a_remedy_not_a_null_reference()
    {
        var stub = new StubHandler();
        using var http = stub.Client();
        using var auth = new XboxAuth(http, new XboxOptions(), new MemoryTokenStore(), new RecordingPrompt());

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => auth.GetXstsTokenAsync(RelyingParty.XboxLive));

        Assert.Contains("fixture", error.Remedy, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_response_with_a_token_but_no_user_hash_is_rejected()
    {
        var clock = new TestClock();
        var noUhs = """
            { "NotAfter": "2026-09-05T12:00:00.0000000Z", "Token": "t", "DisplayClaims": { "xui": [ { } ] } }
            """;

        var stub = new StubHandler()
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess())
            .Route("user.auth.xboxlive.com", Responses.UserAuthenticate())
            .Route("xsts.auth.xboxlive.com", noUhs);

        using var http = stub.Client();
        using var auth = new XboxAuth(
            http,
            Options,
            new MemoryTokenStore
            {
                Current = new CachedRefreshToken { RefreshToken = "r", ClientId = Options.ClientId! },
            },
            new RecordingPrompt(),
            clock);

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => auth.GetXstsTokenAsync(RelyingParty.XboxLive));

        Assert.Contains("user hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_json_response_says_so_rather_than_throwing_a_json_exception()
    {
        var stub = new StubHandler()
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess())
            .Route("user.auth.xboxlive.com", HttpStatusCode.OK, "<html>captive portal</html>");

        using var http = stub.Client();
        using var auth = new XboxAuth(
            http,
            Options,
            new MemoryTokenStore
            {
                Current = new CachedRefreshToken { RefreshToken = "r", ClientId = Options.ClientId! },
            },
            new RecordingPrompt(),
            new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => auth.GetXstsTokenAsync(RelyingParty.XboxLive));

        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(error.InnerException);
    }
}
