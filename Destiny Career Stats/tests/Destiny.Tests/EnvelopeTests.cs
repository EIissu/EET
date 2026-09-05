using System.Net;
using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The ErrorCode envelope, which is the single thing most often got wrong against this API.
/// Bungie answers HTTP 200 for an invalid key, a private profile and a rate limit alike, so
/// every test here checks that the status code was not what decided the outcome.
/// </summary>
public sealed class EnvelopeTests
{
    [Fact]
    public void Unwrap_returns_the_payload_when_error_code_is_one()
    {
        var json = Envelopes.Success("""{"version":"1.2.3"}""");

        var manifest = BungieResponse.Unwrap<DestinyManifest>(json, "a test");

        Assert.Equal("1.2.3", manifest.Version);
    }

    [Fact]
    public void Http_200_with_a_failing_error_code_is_a_failure()
    {
        // The whole point. This body would be a success to anything that looks at the status
        // code, and it is what a private profile actually returns.
        var json = Envelopes.Failure(
            BungiePlatformError.DestinyPrivacyRestriction,
            "DestinyPrivacyRestriction",
            "Your privacy settings do not allow this data to be shown.");

        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>(json, "the profile"));

        Assert.Contains("1665", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DestinyPrivacyRestriction", ex.Message, StringComparison.Ordinal);
        Assert.Equal(BungiePlatformError.DestinyPrivacyRestriction, ex.Data["errorCode"]);
        Assert.NotNull(ex.Remedy);
        Assert.Contains("privacy", ex.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_recorded_privacy_fixture_is_read_as_a_failure()
    {
        // Reading the real file rather than a hand-built string, so a fixture that stops
        // being a valid failure response fails a test rather than quietly passing.
        var fixtures = FixtureLocator.Find();
        Assert.NotNull(fixtures);

        var json = await File.ReadAllTextAsync(Path.Combine(fixtures, "destiny-error-privacy.json"));

        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyProfileResponse>(json, "the fixture profile"));

        Assert.Equal(BungiePlatformError.DestinyPrivacyRestriction, ex.Data["errorCode"]);
    }

    [Fact]
    public async Task The_recorded_throttle_fixture_carries_its_wait_into_the_remedy()
    {
        var fixtures = FixtureLocator.Find();
        Assert.NotNull(fixtures);

        var json = await File.ReadAllTextAsync(Path.Combine(fixtures, "destiny-error-throttled.json"));

        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyProfileResponse>(json, "the fixture profile"));

        Assert.Equal(4, ex.Data["throttleSeconds"]);
        Assert.Contains("4s", ex.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public void Success_with_no_payload_is_null_not_an_error()
    {
        // Activity history past the last page. Treating this as a failure is a real bug: it
        // turns "you have no more matches" into "the career could not be loaded".
        var result = BungieResponse.UnwrapOptional<ActivityHistoryResults>(
            Envelopes.SuccessWithNoPayload, "a page past the end");

        Assert.Null(result);
    }

    [Fact]
    public void Success_with_no_payload_still_throws_when_the_caller_required_one()
    {
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>(Envelopes.SuccessWithNoPayload, "the manifest"));

        Assert.Contains("no payload", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_body_that_is_not_an_envelope_says_so()
    {
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>("<html>Just a moment...</html>", "the manifest"));

        Assert.Contains("not a platform envelope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PlatformBaseUrl", ex.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_is_not_an_envelope_is_reported_as_an_upstream_fault()
    {
        // Nothing about a Cloudflare interstitial is the caller's doing, and a codeless
        // TrackerException reaches the HTTP boundary as 400 Bad Request unless it says so.
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>("<html>Just a moment...</html>", "the manifest"));

        Assert.Equal(502, ex.Data["httpStatus"]);
    }

    [Fact]
    public void A_success_with_no_payload_where_one_was_required_is_an_upstream_fault()
    {
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>(Envelopes.SuccessWithNoPayload, "the manifest"));

        Assert.Equal(502, ex.Data["httpStatus"]);
    }

    [Fact]
    public void A_platform_error_is_left_to_its_error_code_rather_than_forced_to_a_status()
    {
        // ErrorCode is the richer signal; stamping a status on top of it would override the
        // mapping in DestinyProblem that turns 1665 into 403 and 2101 into 401.
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>(
                Envelopes.Failure(BungiePlatformError.DestinyPrivacyRestriction, "DestinyPrivacyRestriction"),
                "the profile"));

        Assert.Null(ex.Data["httpStatus"]);
        Assert.Equal(BungiePlatformError.DestinyPrivacyRestriction, ex.Data["errorCode"]);
    }

    [Theory]
    [InlineData(BungiePlatformError.ThrottleLimitExceeded)]
    [InlineData(BungiePlatformError.ThrottleLimitExceededSeconds)]
    [InlineData(BungiePlatformError.PerEndpointRequestThrottleExceeded)]
    [InlineData(BungiePlatformError.PerApplicationAnonymousThrottleExceeded)]
    [InlineData(BungiePlatformError.PerUserThrottleExceeded)]
    [InlineData(BungiePlatformError.DestinyThrottledByGameServer)]
    public void Throttle_codes_are_recognised(int errorCode)
    {
        Assert.True(BungiePlatformError.IsThrottle(errorCode));
    }

    [Theory]
    [InlineData(BungiePlatformError.Success)]
    [InlineData(BungiePlatformError.DestinyPrivacyRestriction)]
    [InlineData(BungiePlatformError.ApiInvalidOrExpiredKey)]
    public void Non_throttle_codes_are_not_mistaken_for_throttles(int errorCode)
    {
        Assert.False(BungiePlatformError.IsThrottle(errorCode));
    }

    [Theory]
    [InlineData(BungiePlatformError.ApiInvalidOrExpiredKey)]
    [InlineData(BungiePlatformError.ApiKeyMissingFromRequest)]
    [InlineData(BungiePlatformError.DestinyPrivacyRestriction)]
    [InlineData(BungiePlatformError.DestinyAccountNotFound)]
    [InlineData(BungiePlatformError.SystemDisabled)]
    [InlineData(99999)]
    public void Every_error_code_gets_an_actionable_remedy(int errorCode)
    {
        var remedy = BungiePlatformError.Remedy(errorCode);

        Assert.False(string.IsNullOrWhiteSpace(remedy));
        // A remedy that does not tell you where to go is not a remedy.
        Assert.True(
            remedy.Contains("http", StringComparison.OrdinalIgnoreCase)
            || remedy.Contains("Retry", StringComparison.OrdinalIgnoreCase)
            || remedy.Contains("Check", StringComparison.OrdinalIgnoreCase)
            || remedy.Contains("Use ", StringComparison.Ordinal)
            || remedy.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || remedy.Contains("Re-read", StringComparison.Ordinal)
            || remedy.Contains("treat", StringComparison.OrdinalIgnoreCase),
            $"Remedy for {errorCode} does not say what to do: {remedy}");
    }

    [Fact]
    public async Task An_api_key_is_never_echoed_into_an_error()
    {
        var options = new BungieOptions { ApiKey = "not-a-real-key-0000" };
        var handler = StubHandler.Always(
            Envelopes.Failure(BungiePlatformError.ApiInvalidOrExpiredKey, "ApiInvalidOrExpiredKey"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformBaseUrl) };
        var client = new BungieApiClient(http, options, (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());

        Assert.DoesNotContain("not-a-real-key-0000", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-key-0000", ex.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_http_failure_with_no_envelope_is_reported_as_transport()
    {
        var handler = new StubHandler((_, _) =>
            StubHandler.Status(HttpStatusCode.ServiceUnavailable, "<html>maintenance</html>"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.bungie.net/Platform/") };
        var client = new BungieApiClient(http, new BungieOptions(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());

        Assert.Contains("HTTP 503", ex.Message, StringComparison.Ordinal);
        Assert.Contains("maintenance", ex.Remedy!, StringComparison.OrdinalIgnoreCase);
    }
}
