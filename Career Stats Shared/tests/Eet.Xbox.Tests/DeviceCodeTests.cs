using System.Net;
using Eet.Trackers.Core;

namespace Eet.Xbox.Tests;

/// <summary>
/// Step 1: the device code grant, and the refresh that means it only happens once.
/// </summary>
public sealed class DeviceCodeTests
{
    private const string ClientId = "00000000-0000-0000-0000-00000000c0de";

    private static XboxOptions Options => new() { ClientId = ClientId, Tenant = "consumers" };

    [Fact]
    public async Task A_stored_refresh_token_signs_in_without_prompting_anyone()
    {
        var stub = new StubHandler().Route("/oauth2/v2.0/token", Responses.OAuthSuccess());
        var prompt = new RecordingPrompt();
        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "stored", ClientId = ClientId },
        };

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(http, Options, store, prompt, new TestClock());

        var token = await identity.AcquireAsync();

        Assert.Equal("azure-ad-access-token", token.AccessToken);

        // The point of XboxLive.offline_access: no browser, no code, no human.
        Assert.Null(prompt.Presented);
        Assert.Equal("refresh_token", stub.Single.Form()["grant_type"]);
        Assert.Equal("stored", stub.Single.Form()["refresh_token"]);
    }

    [Fact]
    public async Task The_refresh_request_carries_the_xbox_scopes()
    {
        var stub = new StubHandler().Route("/oauth2/v2.0/token", Responses.OAuthSuccess());
        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "stored", ClientId = ClientId },
        };

        using var http = stub.Client();
        await new MicrosoftIdentityClient(http, Options, store, new RecordingPrompt(), new TestClock())
            .AcquireAsync();

        var scope = stub.Single.Form()["scope"];

        Assert.Contains("XboxLive.signin", scope, StringComparison.Ordinal);

        // Without offline_access there is no refresh token, and the user signs in hourly.
        Assert.Contains("XboxLive.offline_access", scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dead_refresh_token_is_cleared_and_the_user_is_asked_to_sign_in()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .RouteSequence(
                "/oauth2/v2.0/token",
                (HttpStatusCode.BadRequest, Responses.InvalidGrant),
                (HttpStatusCode.OK, Responses.OAuthSuccess()));

        var prompt = new RecordingPrompt();
        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "spent", ClientId = ClientId },
        };

        using var http = stub.Client();
        var token = await new MicrosoftIdentityClient(http, Options, store, prompt, new TestClock()).AcquireAsync();

        Assert.Equal("azure-ad-access-token", token.AccessToken);

        // A refresh token Azure AD has rejected is a dead credential. Leaving it on disk
        // achieves nothing and keeps a secret around for no reason.
        Assert.Equal(1, store.Clears);
        Assert.NotNull(prompt.Presented);
    }

    [Fact]
    public async Task A_transient_token_endpoint_failure_keeps_the_refresh_token()
    {
        // A captive portal, a proxy, or Azure AD itself having a bad minute: HTTP 503 and a
        // body that is not a token response at all.
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>");

        var prompt = new RecordingPrompt();
        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "still-good", ClientId = ClientId },
        };

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(http, Options, store, prompt, new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        // Only invalid_grant means the refresh token is dead. Deleting it because a failure
        // was unreadable turns a minute of downtime into a permanent logout: the file is
        // gone and only an interactive browser sign-in brings it back.
        Assert.Equal(0, store.Clears);
        Assert.NotNull(store.Current);
        Assert.Equal("still-good", store.Current.RefreshToken);

        // And it must not silently start a device code sign-in either, which would ask a
        // human to go and type a code because of a transport blip.
        Assert.Null(prompt.Presented);
        Assert.Equal(0, stub.CountFor("/devicecode"));

        Assert.Contains("kept", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_response_with_neither_a_token_nor_an_error_does_not_clear_the_credential()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", HttpStatusCode.OK, """
                { "token_type": "Bearer", "expires_in": 3600 }
                """);

        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "still-good", ClientId = ClientId },
        };

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(http, Options, store, new RecordingPrompt(), new TestClock());

        await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        Assert.Equal(0, store.Clears);
    }

    [Fact]
    public async Task The_challenge_tells_the_user_where_to_go_and_what_to_type()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess());

        var prompt = new RecordingPrompt();

        using var http = stub.Client();
        await new MicrosoftIdentityClient(http, Options, new MemoryTokenStore(), prompt, new TestClock())
            .AcquireAsync();

        Assert.NotNull(prompt.Presented);
        Assert.Equal("ABCD-EFGH", prompt.Presented.UserCode);
        Assert.Equal("https://microsoft.com/link", prompt.Presented.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(900), prompt.Presented.ExpiresIn);
    }

    [Fact]
    public async Task The_device_code_request_asks_for_the_configured_client_and_scope()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess());

        using var http = stub.Client();
        await new MicrosoftIdentityClient(http, Options, new MemoryTokenStore(), new RecordingPrompt(), new TestClock())
            .AcquireAsync();

        var start = stub.For("/devicecode");

        Assert.Equal(ClientId, start.Form()["client_id"]);
        Assert.Equal(XboxEndpoints.DefaultScope, start.Form()["scope"]);
        Assert.Contains("/consumers/", start.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_keeps_going_while_authorization_is_pending()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .RouteSequence(
                "/oauth2/v2.0/token",
                (HttpStatusCode.BadRequest, Responses.AuthorizationPending),
                (HttpStatusCode.BadRequest, Responses.AuthorizationPending),
                (HttpStatusCode.BadRequest, Responses.SlowDown),
                (HttpStatusCode.OK, Responses.OAuthSuccess()));

        using var http = stub.Client();
        var token = await new MicrosoftIdentityClient(
                http,
                Options,
                new MemoryTokenStore(),
                new RecordingPrompt(),
                new TestClock())
            .AcquireAsync();

        Assert.Equal("azure-ad-access-token", token.AccessToken);

        // authorization_pending arrives behind an HTTP 400. A client that checks the status
        // before reading the body treats every poll as a hard failure and never finishes.
        Assert.Equal(4, stub.CountFor("/oauth2/v2.0/token"));

        Assert.Equal(
            "urn:ietf:params:oauth:grant-type:device_code",
            stub.For("/oauth2/v2.0/token").Form()["grant_type"]);
    }

    [Fact]
    public async Task A_poll_loop_against_an_unreadable_endpoint_gives_up_rather_than_spinning()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", HttpStatusCode.BadGateway, "<html>Bad Gateway</html>");

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(
            http, Options, new MemoryTokenStore(), new RecordingPrompt(), new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        Assert.Contains("not a token response", error.Message, StringComparison.OrdinalIgnoreCase);

        // The only other bound on this loop is a deadline computed from the injected clock,
        // which a clock that does not advance on its own never reaches. This test would hang
        // rather than fail without the retry bound -- that is the failure mode being fixed.
        Assert.InRange(stub.CountFor("/oauth2/v2.0/token"), 1, 10);
    }

    [Fact]
    public async Task A_declined_sign_in_says_it_was_declined()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", HttpStatusCode.BadRequest, """
                { "error": "authorization_declined" }
                """);

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(
            http, Options, new MemoryTokenStore(), new RecordingPrompt(), new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        Assert.Contains("declined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_device_code_says_so_rather_than_looping()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", HttpStatusCode.BadRequest, """
                { "error": "expired_token" }
                """);

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(
            http, Options, new MemoryTokenStore(), new RecordingPrompt(), new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_confidential_client_gets_told_why_device_code_will_not_work()
    {
        var stub = new StubHandler().Route("/devicecode", HttpStatusCode.BadRequest, """
            { "error": "unauthorized_client" }
            """);

        using var http = stub.Client();
        var identity = new MicrosoftIdentityClient(
            http, Options, new MemoryTokenStore(), new RecordingPrompt(), new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(() => identity.AcquireAsync());

        Assert.Contains("public client", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refresh_token_from_a_different_client_id_is_not_reused()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess());

        var prompt = new RecordingPrompt();
        var store = new MemoryTokenStore
        {
            Current = new CachedRefreshToken { RefreshToken = "other-apps-token", ClientId = "a-different-app" },
        };

        using var http = stub.Client();
        await new MicrosoftIdentityClient(http, Options, store, prompt, new TestClock()).AcquireAsync();

        // A refresh token is scoped to the app registration that minted it. Sending it with
        // a different client id is a guaranteed failure, so do not try.
        Assert.NotNull(prompt.Presented);
        Assert.Equal(0, stub.CountFor("refresh_token"));
    }

    [Fact]
    public async Task A_new_refresh_token_is_persisted_for_next_time()
    {
        var stub = new StubHandler()
            .Route("/devicecode", Responses.DeviceCodeStart)
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess());

        var store = new MemoryTokenStore();
        var clock = new TestClock();

        using var http = stub.Client();
        await new MicrosoftIdentityClient(http, Options, store, new RecordingPrompt(), clock).AcquireAsync();

        Assert.Equal(1, store.Saves);
        Assert.Equal("azure-ad-refresh-token", store.Current!.RefreshToken);
        Assert.Equal(ClientId, store.Current.ClientId);
        Assert.Equal(clock.GetUtcNow(), store.Current.ObtainedAt);
    }
}
