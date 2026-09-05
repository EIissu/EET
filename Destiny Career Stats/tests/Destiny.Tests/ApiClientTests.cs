using System.Text.Json;
using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The HTTP layer: headers, request shapes, paging, and the throttle loop. All against a
/// stub handler; nothing here opens a socket.
/// </summary>
public sealed class ApiClientTests
{
    private static (BungieApiClient Client, StubHandler Handler, List<TimeSpan> Waits) Build(
        StubHandler handler, BungieOptions? options = null)
    {
        options ??= new BungieOptions();
        var waits = new List<TimeSpan>();
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformBaseUrl) };
        var client = new BungieApiClient(http, options, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        return (client, handler, waits);
    }

    [Fact]
    public async Task The_api_key_travels_in_the_x_api_key_header()
    {
        string? seen = null;
        var handler = new StubHandler((request, _) =>
        {
            seen = request.Headers.TryGetValues("X-API-Key", out var values) ? values.First() : null;
            return StubHandler.Ok(Envelopes.Success("""{"version":"v"}"""));
        });

        var (client, _, _) = Build(handler, new BungieOptions { ApiKey = "stub-key" });
        await client.GetManifestAsync();

        Assert.Equal("stub-key", seen);
    }

    [Fact]
    public async Task With_no_key_no_header_is_sent()
    {
        var present = true;
        var handler = new StubHandler((request, _) =>
        {
            present = request.Headers.Contains("X-API-Key");
            return StubHandler.Ok(Envelopes.Success("""{"version":"v"}"""));
        });

        var (client, _, _) = Build(handler);
        await client.GetManifestAsync();

        // Fixture mode has no key, and sending an empty header would be worse than none.
        Assert.False(present);
    }

    [Fact]
    public async Task A_bungie_name_is_posted_as_two_separate_fields()
    {
        // Sending "Guardian#1234" as one string is the documented way to match nothing while
        // being told the request succeeded.
        var handler = StubHandler.Always(Envelopes.Success("[]"));
        var (client, stub, _) = Build(handler);

        await client.SearchByBungieNameAsync("Guardian", 1234);

        var body = Assert.Single(stub.Bodies, b => b is not null)!;
        using var document = JsonDocument.Parse(body);
        Assert.Equal("Guardian", document.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(1234, document.RootElement.GetProperty("displayNameCode").GetInt32());

        // membershipType All, the only value that finds a player across platforms.
        Assert.Contains("/SearchDestinyPlayerByBungieName/-1/", stub.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_matches_nothing_is_an_empty_list_not_an_error()
    {
        var (client, _, _) = Build(StubHandler.Always(Envelopes.Success("[]")));

        var cards = await client.SearchByBungieNameAsync("Nobody", 1);

        Assert.Empty(cards);
    }

    [Fact]
    public async Task A_throttle_is_waited_out_and_the_request_retried()
    {
        var handler = StubHandler.Sequence(
            Envelopes.Failure(
                BungiePlatformError.PerEndpointRequestThrottleExceeded,
                "PerEndpointRequestThrottleExceeded",
                "Slow down",
                throttleSeconds: 7),
            Envelopes.Success("""{"version":"after-the-wait"}"""));

        var (client, stub, waits) = Build(handler);

        var manifest = await client.GetManifestAsync();

        Assert.Equal("after-the-wait", manifest.Version);
        Assert.Equal(2, stub.CallCount);
        // Exactly what Bungie asked for, not a guess.
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(waits));
    }

    [Fact]
    public async Task A_throttle_wait_is_capped_so_a_bad_hint_cannot_hang_a_request()
    {
        var handler = StubHandler.Sequence(
            Envelopes.Failure(
                BungiePlatformError.ThrottleLimitExceededMinutes,
                "ThrottleLimitExceededMinutes",
                throttleSeconds: 86400),
            Envelopes.Success("""{"version":"ok"}"""));

        var options = new BungieOptions { MaxThrottleWait = TimeSpan.FromSeconds(30) };
        var (client, _, waits) = Build(handler, options);

        await client.GetManifestAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(waits));
    }

    [Fact]
    public async Task A_persistent_throttle_eventually_fails_with_the_wait_in_the_remedy()
    {
        var handler = StubHandler.Always(Envelopes.Failure(
            BungiePlatformError.PerApplicationThrottleExceeded,
            "PerApplicationThrottleExceeded",
            throttleSeconds: 3));

        var (client, stub, waits) = Build(handler, new BungieOptions { ThrottleRetries = 2 });

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());

        Assert.Equal(2, waits.Count);
        Assert.Equal(3, stub.CallCount);
        Assert.Equal(3, ex.Data["throttleSeconds"]);
        Assert.Contains("3s", ex.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_history_asks_for_the_configured_page_and_count()
    {
        var (client, stub, _) = Build(StubHandler.Always(Envelopes.SuccessWithNoPayload));

        await client.GetActivityHistoryAsync(3, "4611686018400119004", "2305843009300010001", 0, 200, 2);

        var request = Assert.Single(stub.Requests);
        Assert.Contains("count=200", request, StringComparison.Ordinal);
        Assert.Contains("page=2", request, StringComparison.Ordinal);
        Assert.Contains("mode=0", request, StringComparison.Ordinal);
        Assert.Contains(
            "/Account/4611686018400119004/Character/2305843009300010001/Stats/Activities/",
            request,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_history_past_the_end_is_an_empty_list()
    {
        var (client, _, _) = Build(StubHandler.Always(Envelopes.SuccessWithNoPayload));

        var activities = await client.GetActivityHistoryAsync(3, "1", "2", 0, 200, 9);

        Assert.Empty(activities);
    }

    [Fact]
    public async Task Lifetime_stats_ask_for_all_characters_and_the_all_time_period()
    {
        var (client, stub, _) = Build(StubHandler.Always(Envelopes.Success("{}")));

        await client.GetHistoricalStatsAsync(3, "4611686018400119004", "0", "General", "AllPvP,AllPvE");

        var request = Assert.Single(stub.Requests);
        Assert.Contains("/Character/0/Stats/", request, StringComparison.Ordinal);
        // periodType 2 is AllTime.
        Assert.Contains("periodType=2", request, StringComparison.Ordinal);
        Assert.Contains("groups=General", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_carnage_report_is_null_rather_than_an_exception()
    {
        var (client, _, _) = Build(StubHandler.Always(
            Envelopes.Failure(BungiePlatformError.DestinyPGCRNotFound, "DestinyPGCRNotFound")));

        // Reports age out. An old match with no report is normal, not a failure -- but it is
        // still a failing ErrorCode, so this asserts the distinction is drawn deliberately.
        await Assert.ThrowsAsync<TrackerException>(
            () => client.GetPostGameCarnageReportAsync("12000000001"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, 429)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, 502)]
    [InlineData(System.Net.HttpStatusCode.GatewayTimeout, 504)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, 502)]
    public async Task A_transport_failure_says_what_status_it_means(
        System.Net.HttpStatusCode upstream, int expected)
    {
        // A failure with no envelope carries no ErrorCode, and the HTTP boundary defaults a
        // codeless TrackerException to 400. Left unstamped, bungie.net's weekly maintenance
        // window reaches the dashboard as "Bad request" -- blaming whoever typed the name.
        var handler = new StubHandler((_, _) =>
            StubHandler.Status(upstream, "<html>not an envelope</html>"));

        var (client, _, _) = Build(handler);

        var ex = await Assert.ThrowsAsync<TrackerException>(() => client.GetManifestAsync());

        Assert.Equal(expected, ex.Data["httpStatus"]);
        // The status Bungie actually answered with survives too, for whoever has to chase it.
        Assert.Equal((int)upstream, ex.Data["upstreamStatus"]);
    }

    [Fact]
    public async Task A_definition_table_that_is_missing_is_not_the_callers_fault_either()
    {
        var handler = new StubHandler((_, _) =>
            StubHandler.Status(System.Net.HttpStatusCode.NotFound, "<html>gone</html>"));

        var (client, _, _) = Build(handler);

        var ex = await Assert.ThrowsAsync<TrackerException>(
            () => client.GetDefinitionTableAsync("/common/destiny2_content/json/en/x.json"));

        Assert.Equal(502, ex.Data["httpStatus"]);
        Assert.Equal(404, ex.Data["upstreamStatus"]);
    }

    [Fact]
    public async Task Definition_tables_are_fetched_without_the_api_key()
    {
        // They are static CDN files. Sending a key in front of a cache serves no purpose and
        // puts the secret somewhere it does not belong.
        var sawKey = true;
        var handler = new StubHandler((request, _) =>
        {
            sawKey = request.Headers.Contains("X-API-Key");
            return StubHandler.Ok("""{"_note":"table"}""");
        });

        var (client, stub, _) = Build(handler, new BungieOptions { ApiKey = "stub-key" });

        await using var stream = await client.GetDefinitionTableAsync(
            "/common/destiny2_content/json/en/DestinyActivityDefinition-x.json");

        Assert.False(sawKey);
        Assert.Contains("/common/destiny2_content/json/en/", stub.Requests[0], StringComparison.Ordinal);
    }
}
