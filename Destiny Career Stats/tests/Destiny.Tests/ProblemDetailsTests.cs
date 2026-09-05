using Eet.Destiny.Api;
using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The HTTP boundary. The thing worth guarding is that a Bungie ErrorCode becomes a status
/// code that means the same thing, and that the client's remedy survives the trip.
/// </summary>
public sealed class ProblemDetailsTests
{
    private static TrackerException Failure(int errorCode, int throttleSeconds = 0)
    {
        var envelope = new BungieEnvelope<object>
        {
            ErrorCode = errorCode,
            ErrorStatus = "Stub",
            Message = "Stub message",
            ThrottleSeconds = throttleSeconds,
        };

        return BungieResponse.ToException(envelope, "a test");
    }

    private static ProblemHttpResult Problem(TrackerException exception) =>
        Assert.IsType<ProblemHttpResult>(DestinyProblem.From(exception));

    [Fact]
    public void A_private_profile_is_a_refusal_with_a_reason_not_an_upstream_fault()
    {
        // 502 would send an operator hunting for a problem on this side that does not exist.
        var problem = Problem(Failure(BungiePlatformError.DestinyPrivacyRestriction));

        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal("Private profile", problem.ProblemDetails.Title);
        Assert.Equal(1665, problem.ProblemDetails.Extensions["bungieErrorCode"]);
        Assert.Contains(
            "privacy",
            (string)problem.ProblemDetails.Extensions["remedy"]!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_api_key_is_a_401()
    {
        var problem = Problem(Failure(BungiePlatformError.ApiInvalidOrExpiredKey));

        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Contains(
            "bungie.net/en/Application",
            (string)problem.ProblemDetails.Extensions["remedy"]!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_throttle_is_a_429_that_says_how_long_to_wait()
    {
        var problem = Problem(Failure(BungiePlatformError.PerEndpointRequestThrottleExceeded, throttleSeconds: 9));

        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
        Assert.Equal(9, problem.ProblemDetails.Extensions["throttleSeconds"]);
    }

    [Fact]
    public void A_missing_account_is_a_404()
    {
        var problem = Problem(Failure(BungiePlatformError.DestinyAccountNotFound));

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("Not found", problem.ProblemDetails.Title);
    }

    [Fact]
    public void Maintenance_is_a_503()
    {
        var problem = Problem(Failure(BungiePlatformError.SystemDisabled));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public void An_unmapped_error_code_is_an_upstream_fault()
    {
        var problem = Problem(Failure(1618));

        Assert.Equal(StatusCodes.Status502BadGateway, problem.StatusCode);
        Assert.Equal("Bungie API error", problem.ProblemDetails.Title);
    }

    [Fact]
    public void A_search_miss_carries_its_own_status_because_bungie_called_it_a_success()
    {
        var exception = new TrackerException("No such player.", "Try a membership id.");
        exception.Data["httpStatus"] = 404;

        var problem = Problem(exception);

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("Try a membership id.", problem.ProblemDetails.Extensions["remedy"]);
    }

    [Fact]
    public void A_client_side_argument_error_is_a_400_not_a_bad_gateway()
    {
        var problem = Problem(new TrackerException("\"Nintendo\" is not a Destiny platform.", "Use Steam."));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("Bad request", problem.ProblemDetails.Title);
    }

    [Fact]
    public async Task Bungie_maintenance_is_not_reported_as_a_bad_request()
    {
        // End to end through the real client: an HTML 503 from bungie.net has to reach the
        // caller as 503, not as "Bad request". A 400 tells an operator to fix their input
        // and tells a retrying client not to bother -- both wrong, and both expensive.
        var handler = new StubHandler((_, _) => StubHandler.Status(
            System.Net.HttpStatusCode.ServiceUnavailable,
            "<html><body>Destiny 2 is temporarily offline for maintenance.</body></html>"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.bungie.net/Platform/") };
        var client = new BungieApiClient(http, new BungieOptions(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());
        var problem = Problem(ex);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("Bungie.net is unavailable", problem.ProblemDetails.Title);
        Assert.Equal(503, problem.ProblemDetails.Extensions["bungieHttpStatus"]);
        Assert.Contains(
            "maintenance",
            (string)problem.ProblemDetails.Extensions["remedy"]!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edge_rate_limit_stays_a_429_so_a_backoff_still_fires()
    {
        var handler = new StubHandler((_, _) => StubHandler.Status(
            System.Net.HttpStatusCode.TooManyRequests, "<html>slow down</html>"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.bungie.net/Platform/") };
        var client = new BungieApiClient(http, new BungieOptions(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());
        var problem = Problem(ex);

        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
    }

    [Fact]
    public void A_non_envelope_body_is_a_bad_gateway_not_a_bad_request()
    {
        var ex = Assert.Throws<TrackerException>(
            () => BungieResponse.Unwrap<DestinyManifest>("<html>Just a moment...</html>", "the manifest"));

        Assert.Equal(StatusCodes.Status502BadGateway, Problem(ex).StatusCode);
    }

    [Fact]
    public async Task The_guard_turns_an_unreachable_bungie_into_a_502()
    {
        var result = await DestinyProblem.GuardAsync(
            () => throw new HttpRequestException("No such host is known."));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, problem.StatusCode);
        Assert.Contains(
            "did not arrive",
            (string)problem.ProblemDetails.Extensions["remedy"]!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_guard_leaves_a_successful_result_alone()
    {
        var result = await DestinyProblem.GuardAsync(() => Task.FromResult<IResult>(TypedResults.Ok(42)));

        Assert.IsType<Ok<int>>(result);
    }

    [Fact]
    public void The_dashboard_directory_is_optional()
    {
        // Another agent builds Career Stats Shared/web. This API has to start either way.
        Assert.Null(SharedWeb.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
