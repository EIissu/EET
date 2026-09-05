using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The zero-credential path, end to end.
///
/// These run the whole client -- requests, ErrorCode envelopes, paging, the manifest cache,
/// every line of mapping -- against the recorded fixtures, because that is the only way the
/// fixture path is worth having. A fixture source that returned a pre-built CareerSnapshot
/// would prove nothing about the code that runs with a real key.
/// </summary>
public sealed class FixturePathTests
{
    private static BungieOptions Options() => new()
    {
        // No ApiKey. That is the point.
        CacheDirectory = Path.Combine(
            Path.GetTempPath(), "eet-destiny-tests", Guid.NewGuid().ToString("N")),
    };

    private static DestinyTracker Tracker() => DestinyTracker.Create(Options());

    [Fact]
    public void The_shared_fixtures_are_found_by_walking_up_from_the_binary()
    {
        var fixtures = FixtureLocator.Find();

        Assert.NotNull(fixtures);
        Assert.True(File.Exists(Path.Combine(fixtures, "destiny-profile.json")));
        Assert.EndsWith(Path.Combine("Career Stats Shared", "fixtures"), fixtures, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_api_key_the_tracker_starts_in_fixture_mode()
    {
        using var tracker = Tracker();

        Assert.True(tracker.IsFixture);
        Assert.True(tracker.Career.IsFixture);
        Assert.Equal(GameId.Destiny2, tracker.Career.Game);
        Assert.Contains("mode=fixture", tracker.Options.Describe(), StringComparison.Ordinal);
        // Never the key, present or not.
        Assert.DoesNotContain("ApiKey", tracker.Options.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bungie_name_resolves_out_of_the_fixtures()
    {
        using var tracker = Tracker();

        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");

        Assert.NotNull(player);
        Assert.Equal("AnaGuardian#4412", player.Handle);
        Assert.Equal("4611686018400119004", player.Id);
        Assert.Equal("Steam", player.Platform);
    }

    [Fact]
    public async Task A_name_with_no_code_is_refused_before_a_request_goes_out()
    {
        using var tracker = Tracker();

        var ex = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.Career.ResolveAsync("AnaGuardian"));

        Assert.Contains("Guardian#1234", ex.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_matches_nothing_is_a_not_found_with_a_hint()
    {
        using var tracker = Tracker();

        var ex = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.Career.ResolveAsync("NoSuchGuardian#0001"));

        Assert.Equal(404, ex.Data["httpStatus"]);
        Assert.Contains("membership id", ex.Remedy!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_homoglyph_display_name_is_detectable_on_the_way_back_out()
    {
        // The fixture's second card renders as "Ilissu" but starts with U+0406. A player
        // cannot type that name, so a tracker that only searches by name can never find them.
        using var tracker = Tracker();

        var player = await tracker.Career.ResolveAsync("Іlissu#9007");

        Assert.NotNull(player);
        Assert.True(Identity.LooksLikeHomoglyph(player.Handle));
        Assert.True(Identity.LooksTheSame(player.Handle, "Ilissu#9007"));
        Assert.NotNull(Identity.Explain(player.Handle));

        // Searching for the typeable spelling finds nothing, which is exactly the failure
        // Identity exists to explain.
        await Assert.ThrowsAsync<TrackerException>(() => tracker.Career.ResolveAsync("Ilissu#9007"));
    }

    [Fact]
    public async Task A_membership_id_resolves_without_a_name()
    {
        using var tracker = Tracker();

        var player = await tracker.Career.ResolveAsync("4611686018400119004");

        Assert.NotNull(player);
        Assert.Equal("4611686018400119004", player.Id);
        Assert.Equal("Steam", player.Platform);
    }

    [Fact]
    public async Task A_full_career_snapshot_comes_out_of_the_fixtures_with_no_key()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");

        var snapshot = await tracker.Career.GetSnapshotAsync(player!);

        Assert.True(snapshot.IsFixture);
        Assert.Equal("fixture", snapshot.Source);
        Assert.Equal(GameId.Destiny2, snapshot.Game);
        Assert.Equal("AnaGuardian#4412", snapshot.Player.Handle);

        // ~120 matches over ~90 days, merged across three characters.
        Assert.InRange(snapshot.Recent.Count, 100, 140);
        var span = snapshot.Recent[0].PlayedAt - snapshot.Recent[^1].PlayedAt;
        Assert.InRange(span.TotalDays, 80, 95);

        // Newest first, and no duplicates from the per-character merge.
        Assert.Equal(
            snapshot.Recent.Select(m => m.PlayedAt).OrderByDescending(p => p).ToList(),
            snapshot.Recent.Select(m => m.PlayedAt).ToList());
        Assert.Equal(snapshot.Recent.Count, snapshot.Recent.Select(m => m.Id).Distinct().Count());

        // The manifest gave every match a real map name rather than a hash.
        Assert.All(snapshot.Recent, m => Assert.DoesNotContain("Activity ", m.Map, StringComparison.Ordinal));
        Assert.Contains(snapshot.Recent, m => m.Map == "Rusted Lands");

        // The synthetic warning has to be impossible to miss.
        Assert.Contains(snapshot.Warnings, w => w.Contains("synthetic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_headline_answers_how_am_i_doing_without_scrolling()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");
        var snapshot = await tracker.Career.GetSnapshotAsync(player!);

        foreach (var key in new[] { "kd", "kda", "efficiency", "winrate", "timeplayed", "matches" })
        {
            Assert.Contains(snapshot.Headline, k => k.Key == key);
        }

        var kd = snapshot.Headline.Single(k => k.Key == "kd");
        Assert.InRange(kd.Value, 0.5, 3.0);
        Assert.Equal(Better.Higher, kd.Better);
        Assert.NotNull(kd.Delta);
        Assert.NotNull(kd.Improved);

        var winRate = snapshot.Headline.Single(k => k.Key == "winrate");
        Assert.InRange(winRate.Value, 0.2, 0.9);
        Assert.EndsWith("%", winRate.Formatted, StringComparison.Ordinal);

        // Time played is neither good nor bad, and must not render an arrow.
        var time = snapshot.Headline.Single(k => k.Key == "timeplayed");
        Assert.Equal(Better.Neutral, time.Better);
        Assert.Null(time.Improved);
    }

    [Fact]
    public async Task The_lifetime_totals_are_internally_consistent()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");
        var snapshot = await tracker.Career.GetSnapshotAsync(player!);

        var totals = snapshot.Totals;
        Assert.Equal(totals.Matches, totals.Wins + totals.Losses);
        // Both derived properties have to be numbers a Destiny player would recognise.
        Assert.InRange(totals.WinRate, 0.3, 0.7);
        Assert.InRange(totals.Kd, 0.5, 3.0);
    }

    [Fact]
    public async Task The_fixtures_contain_a_trend_the_charts_can_actually_find()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");
        var snapshot = await tracker.Career.GetSnapshotAsync(player!);

        var kd = snapshot.Trends.Single(t => t.Key == "kd");

        Assert.NotEmpty(kd.Points);
        Assert.Equal(kd.Points.Count, kd.Smoothed.Count);
        Assert.All(kd.Points, p => Assert.True(p.Samples > 0));
        Assert.True(kd.SlopePerWeek > 0, "The fixture career is meant to be improving.");
        Assert.Equal("improving", kd.Direction);

        // Deaths per match falls, and falling is the good direction for it.
        var deaths = snapshot.Trends.Single(t => t.Key == "deaths");
        Assert.Equal(Better.Lower, deaths.Better);
    }

    [Fact]
    public async Task Paging_stops_at_the_end_of_each_character_rather_than_running_to_the_limit()
    {
        var options = Options();
        options.ActivityPageSize = 20;
        options.MaxActivityPages = 10;

        var fixtures = FixtureLocator.Find()!;
        var handler = new FixtureMessageHandler(fixtures);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformBaseUrl) };
        options.FixtureDirectory = fixtures;
        using var tracker = DestinyTracker.Create(http, options, fixtureMode: true);

        var player = new Player("AnaGuardian#4412", "4611686018400119004", "Steam");
        var snapshot = await tracker.Career.GetSnapshotAsync(player);

        Assert.InRange(snapshot.Recent.Count, 100, 140);

        // Three characters with roughly 40 matches each at 20 a page: three pages each, and
        // the client must stop on the short third page rather than asking for all ten.
        var activityRequests = handler.Requests.Count(r => r.Contains("/Stats/Activities/", StringComparison.Ordinal));
        Assert.InRange(activityRequests, 6, 12);
    }

    [Fact]
    public async Task A_carnage_report_round_trips_and_a_missing_one_reports_why()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");
        var matches = await tracker.Career.GetMatchesAsync(player!, 1);
        var newest = Assert.Single(matches);

        var report = await tracker.Api.GetPostGameCarnageReportAsync(newest.Id);

        Assert.NotNull(report);
        var entry = Assert.Single(report.Entries!);
        Assert.Equal("AnaGuardian", entry.Player!.DestinyUserInfo!.BungieGlobalDisplayName);

        // precisionKills is the only accuracy-shaped number Bungie publishes, and it lives
        // only here -- never in activity history.
        Assert.NotNull(entry.Extended!.Values);
        Assert.True(DestinyMapper.Stat(entry.Extended.Values, "precisionKills") > 0);

        var ex = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.Api.GetPostGameCarnageReportAsync("99999999999"));
        Assert.Equal(BungiePlatformError.DestinyPGCRNotFound, ex.Data["errorCode"]);
    }

    [Fact]
    public async Task Matches_can_be_asked_for_on_their_own()
    {
        using var tracker = Tracker();
        var player = await tracker.Career.ResolveAsync("AnaGuardian#4412");

        var matches = await tracker.Career.GetMatchesAsync(player!, 5);

        Assert.Equal(5, matches.Count);
        Assert.All(matches, m => Assert.NotEqual(string.Empty, m.Id));
        Assert.All(matches, m => Assert.True(m.Duration > TimeSpan.Zero));
    }

    [Fact]
    public async Task A_platform_the_client_does_not_recognise_is_refused_clearly()
    {
        using var tracker = Tracker();

        var ex = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.Career.GetSnapshotAsync(
                new Player("Someone#0001", "4611686018400119004", "Nintendo")));

        Assert.Contains("Player.Platform", ex.Remedy!, StringComparison.Ordinal);
    }
}
