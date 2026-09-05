using System.Net;

namespace Eet.Xbox.Tests;

/// <summary>
/// The live achievements client: which URL, which headers, and whether it follows the
/// pagination it is given.
/// </summary>
public sealed class AchievementsClientTests
{
    private static XboxAchievementsClient Client(StubHandler stub, HttpClient http) =>
        new(http, new FixtureXboxAuth(new TestClock()));

    [Fact]
    public async Task Achievements_are_requested_per_title_with_contract_version_2()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();
        await Client(stub, http).GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);

        var request = stub.For("achievements.xboxlive.com");

        Assert.Equal(
            $"https://achievements.xboxlive.com/users/xuid({Responses.Xuid})/achievements?titleId=2043073184",
            request.Uri.AbsoluteUri);

        Assert.Equal("2", request.Header("x-xbl-contract-version"));
    }

    [Fact]
    public async Task Every_request_carries_both_halves_of_the_XBL3_header()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();
        await Client(stub, http).GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);

        foreach (var request in stub.Requests)
        {
            var authorization = request.Header("Authorization");
            Assert.NotNull(authorization);
            Assert.StartsWith("XBL3.0 x=", authorization, StringComparison.Ordinal);
            Assert.Contains(";", authorization, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_xuid_wrapper_is_unwrapped_rather_than_double_wrapped()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();

        // Identity.XuidRef produces "xuid(...)", and callers pass whichever they have.
        await Client(stub, http).GetTitleAchievementsAsync($"xuid({Responses.Xuid})", XboxTitles.HaloInfinite);

        Assert.DoesNotContain("xuid(xuid(", stub.For("achievements").Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains($"xuid({Responses.Xuid})", stub.For("achievements").Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_is_followed_to_the_end()
    {
        var stub = new StubHandler()
            .RouteSequence(
                "achievements.xboxlive.com",
                (HttpStatusCode.OK, Responses.AchievementsPageOne),
                (HttpStatusCode.OK, Responses.AchievementsPageTwo))
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();
        var result = await Client(stub, http).GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);

        // Stopping at page one would report a player who owns half the achievements they
        // actually do -- a wrong number that looks entirely reasonable.
        Assert.Equal(2, stub.CountFor("achievements.xboxlive.com"));
        Assert.Equal(2, result.Achievements.Count);

        Assert.Contains(
            "continuationToken=PAGE-TWO",
            stub.Requests[1].Uri.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_achievements_are_requested_without_a_title_filter()
    {
        var stub = new StubHandler().Route("achievements.xboxlive.com", Responses.Achievements);

        using var http = stub.Client();
        var recent = await Client(stub, http).GetRecentAchievementsAsync(Responses.Xuid);

        var uri = stub.Single.Uri;

        Assert.Equal($"/users/xuid({Responses.Xuid})/achievements", uri.AbsolutePath);
        Assert.DoesNotContain("titleId", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task Recent_achievements_ask_the_service_for_recent_ones()
    {
        var stub = new StubHandler().Route("achievements.xboxlive.com", Responses.Achievements);

        using var http = stub.Client();
        await Client(stub, http).GetRecentAchievementsAsync(Responses.Xuid, maxItems: 25);

        var query = stub.Single.Uri.Query;

        // The REST reference for this URI documents orderBy defaulting to "Unordered" and
        // unlockedOnly defaulting to false. Sending neither asks for the player's whole
        // achievement catalogue in no particular order and then keeps the first N of it,
        // which is not a recent-unlocks feed -- it is an arbitrary slice that happens to
        // have the right length.
        Assert.Contains("orderBy=UnlockTime", query, StringComparison.Ordinal);
        Assert.Contains("unlockedOnly=true", query, StringComparison.Ordinal);
        Assert.Contains("maxItems=25", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_achievements_come_back_newest_first_whatever_order_the_service_used()
    {
        // Oldest first, which is what an "UnlockTime" sort could equally well mean and what
        // an "Unordered" one may return regardless.
        const string Ascending = """
            {
              "achievements": [
                {
                  "id": "old", "name": "Older",
                  "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
                  "progressState": "Achieved",
                  "progression": { "timeUnlocked": "2026-01-01T00:00:00.0000000Z" },
                  "rewards": [ { "value": "10", "type": "Gamerscore", "valueType": "Int" } ]
                },
                {
                  "id": "new", "name": "Newer",
                  "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
                  "progressState": "Achieved",
                  "progression": { "timeUnlocked": "2026-08-01T00:00:00.0000000Z" },
                  "rewards": [ { "value": "10", "type": "Gamerscore", "valueType": "Int" } ]
                }
              ],
              "pagingInfo": { "continuationToken": null, "totalRecords": 2 }
            }
            """;

        var stub = new StubHandler().Route("achievements.xboxlive.com", Ascending);

        using var http = stub.Client();
        var recent = await Client(stub, http).GetRecentAchievementsAsync(Responses.Xuid);

        // The interface promises newest first, and FixtureXboxAchievements delivers that by
        // sorting. If the live client only forwards whatever arrived, the fixture path and
        // the live path answer the same question differently, and fixture output stops
        // being evidence about live output.
        Assert.Equal(["new", "old"], recent.Select(a => a.Id));
    }

    [Fact]
    public async Task Title_metadata_is_refetched_once_it_goes_stale()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        var clock = new TestClock();
        using var http = stub.Client();
        var client = new XboxAchievementsClient(http, new FixtureXboxAuth(clock), new XboxOptions(), clock);

        await client.GetTitleHistoryAsync(Responses.Xuid);
        await client.GetTitleHistoryAsync(Responses.Xuid);
        Assert.Equal(1, stub.CountFor("titlehub.xboxlive.com"));

        // "Last played" and a running gamerscore are the things a career tracker exists to
        // watch move. Cached for the life of the client -- in the API host, the life of the
        // process -- they would be read once at startup and never again, and the dashboard
        // would show a last-played date frozen while the player was still playing.
        clock.Advance(TimeSpan.FromMinutes(6));
        await client.GetTitleHistoryAsync(Responses.Xuid);
        Assert.Equal(2, stub.CountFor("titlehub.xboxlive.com"));
    }

    [Fact]
    public async Task An_empty_body_behind_a_200_is_a_remedy_not_an_ArgumentException()
    {
        var stub = new StubHandler().Route("achievements.xboxlive.com", HttpStatusCode.OK, string.Empty);

        using var http = stub.Client();

        var error = await Assert.ThrowsAsync<Eet.Trackers.Core.TrackerException>(
            () => Client(stub, http).GetRecentAchievementsAsync(Responses.Xuid));

        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Remedy);
    }

    [Fact]
    public async Task Recent_achievements_respect_the_requested_limit()
    {
        var stub = new StubHandler().Route("achievements.xboxlive.com", Responses.Achievements);

        using var http = stub.Client();
        var recent = await Client(stub, http).GetRecentAchievementsAsync(Responses.Xuid, maxItems: 1);

        Assert.Single(recent);
    }

    [Fact]
    public async Task The_title_hub_is_asked_with_the_achievement_decoration_and_a_language()
    {
        var stub = new StubHandler().Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();
        await Client(stub, http).GetTitleHistoryAsync(Responses.Xuid);

        var request = stub.Single;

        // Without the decoration segment every title returns null achievement totals, which
        // renders as a player who has never unlocked anything.
        Assert.EndsWith("/titles/titlehistory/decoration/achievement,scid", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("2", request.Header("x-xbl-contract-version"));
        Assert.Equal("en-US, en", request.Header("Accept-Language"));
    }

    [Fact]
    public async Task The_title_hub_is_fetched_once_per_player_not_once_per_call()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", Responses.TitleHistory);

        using var http = stub.Client();
        var client = Client(stub, http);

        await client.GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);
        await client.GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);

        Assert.Equal(1, stub.CountFor("titlehub.xboxlive.com"));
    }

    [Fact]
    public async Task A_failing_title_hub_degrades_rather_than_failing_the_whole_call()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", Responses.Achievements)
            .Route("titlehub.xboxlive.com", HttpStatusCode.Forbidden, "{}");

        using var http = stub.Client();
        var result = await Client(stub, http).GetTitleAchievementsAsync(Responses.Xuid, XboxTitles.HaloInfinite);

        // The name and totals fall back to what the achievements themselves said.
        Assert.Equal("Halo Infinite", result.TitleName);
        Assert.Equal(70, result.TotalGamerscore);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task Profile_lookup_uses_contract_version_3_and_names_its_settings()
    {
        var stub = new StubHandler().Route("profile.xboxlive.com", Responses.Profile);

        using var http = stub.Client();
        var profile = await Client(stub, http).ResolveGamertagAsync("Fixture Player");

        var request = stub.Single;

        Assert.Equal("3", request.Header("x-xbl-contract-version"));
        Assert.Contains("/users/gt(Fixture%20Player)/profile/settings", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("settings=Gamertag,GameDisplayPicRaw,Gamerscore", Uri.UnescapeDataString(request.Uri.AbsoluteUri), StringComparison.Ordinal);
        Assert.Equal("2814648798129555", profile!.Xuid);
    }

    [Fact]
    public async Task A_gamertag_that_does_not_exist_returns_null_rather_than_throwing()
    {
        var stub = new StubHandler().Route("profile.xboxlive.com", HttpStatusCode.NotFound, "{}");

        using var http = stub.Client();
        Assert.Null(await Client(stub, http).ResolveGamertagAsync("NobodyAtAll"));
    }

    [Fact]
    public async Task A_non_ascii_gamertag_is_escaped_into_the_url()
    {
        var stub = new StubHandler().Route("profile.xboxlive.com", Responses.Profile);

        using var http = stub.Client();
        await Client(stub, http).ResolveGamertagAsync(FixtureXboxAuth.FixtureGamertag);

        // U+0406 percent-encodes to %D0%86. Interpolated raw it would either fail to build
        // a Uri or be silently transcoded.
        Assert.Contains("%D0%86lissu", stub.Single.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_live_client_reports_itself_as_live()
    {
        var stub = new StubHandler();
        using var http = stub.Client();

        Assert.False(Client(stub, http).IsFixture);
    }
}
