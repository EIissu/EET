using Eet.Trackers.Core;

namespace Eet.Xbox.Tests;

/// <summary>
/// The zero-credential path.
///
/// This is the promise the whole project rests on: the owner has no Azure app registration
/// today and must be able to run the thing anyway. So these tests construct everything the
/// way a first run would -- no client id, no token cache, no network -- and assert that
/// real data comes out the other end.
/// </summary>
public sealed class FixtureModeTests
{
    private static readonly FixtureStore Fixtures = new();

    [Fact]
    public async Task With_no_credentials_at_all_the_factory_returns_fixture_sources()
    {
        var options = new XboxOptions();

        Assert.False(options.HasCredentials);

        var auth = XboxServices.CreateAuth(options);
        var achievements = XboxServices.CreateAchievements(options, auth);

        // IXboxAuth has no IsFixture of its own -- see the report; the concrete type is
        // the only signal the shared contract offers here.
        Assert.IsType<FixtureXboxAuth>(auth);
        Assert.True(achievements.IsFixture);

        // And it actually produces data, which is the part that matters.
        var title = await achievements.GetTitleAchievementsAsync(
            FixtureXboxAuth.FixtureXuid,
            XboxTitles.HaloInfinite);

        Assert.True(title.TotalCount > 100);
        Assert.True(title.EarnedCount > 0);
    }

    [Fact]
    public void A_configured_client_id_switches_to_the_live_clients()
    {
        var options = new XboxOptions { ClientId = "00000000-0000-0000-0000-00000000c0de" };

        using var http = new HttpClient();
        var auth = XboxServices.CreateAuth(options, http, new NullRefreshTokenStore());

        Assert.IsType<XboxAuth>(auth);
        Assert.IsType<XboxAchievementsClient>(XboxServices.CreateAchievements(options, auth, http));

        (auth as IDisposable)?.Dispose();
    }

    [Fact]
    public void The_source_description_never_lets_synthetic_data_pass_as_real()
    {
        var achievements = new FixtureXboxAchievements(Fixtures);

        Assert.Contains("Synthetic", XboxServices.DescribeSource(achievements), StringComparison.Ordinal);

        var warnings = XboxServices.Warnings(new XboxOptions(), achievements);
        Assert.Single(warnings);
        Assert.Contains("EET_XBOX_CLIENT_ID", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fixture_token_chain_produces_usable_but_obviously_fake_tokens()
    {
        // The system clock, not a TestClock: XstsToken.IsExpired in the shared contract
        // reads DateTimeOffset.UtcNow directly rather than any injected TimeProvider, so a
        // token minted against a fake clock reads as expired the moment the two disagree.
        // See the report -- this is a contract limitation, not a choice made here.
        var auth = new FixtureXboxAuth();

        var xsts = await auth.GetXstsTokenAsync(RelyingParty.XboxLive);
        var spartan = await auth.GetSpartanTokenAsync();

        // Structurally valid, so downstream header building works unchanged...
        Assert.False(xsts.IsExpired);
        Assert.False(spartan.IsExpired);
        Assert.StartsWith("XBL3.0 x=", xsts.AuthorizationHeader, StringComparison.Ordinal);
        Assert.Equal(FixtureXboxAuth.FixtureXuid, xsts.Xuid);

        // ...and unmistakably not a credential, so one appearing in a log or a screenshot
        // is never mistaken for a leak.
        Assert.Contains("NOT-A-CREDENTIAL", xsts.Token, StringComparison.Ordinal);
        Assert.Contains("NOT-A-CREDENTIAL", spartan.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fixtures_go_through_the_real_mapper()
    {
        // If the fixture were a pre-baked TitleAchievements this would prove nothing. It is
        // raw API-shaped JSON, so getting here means MapAchievements ran on it.
        var raw = await Fixtures.ReadAsync(FixtureXboxAchievements.HaloAchievementsFixture);

        Assert.Contains("\"progressState\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"rewards\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"titleAssociations\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EarnedGamerscore\"", raw, StringComparison.Ordinal);

        // And it says what it is.
        Assert.Contains("SYNTHETIC", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_halo_fixture_covers_about_120_achievements_over_90_days()
    {
        var achievements = new FixtureXboxAchievements(Fixtures);
        var title = await achievements.GetTitleAchievementsAsync(
            FixtureXboxAuth.FixtureXuid,
            XboxTitles.HaloInfinite);

        Assert.InRange(title.Achievements.Count, 110, 130);

        var unlocks = title.Achievements
            .Where(a => a.UnlockedAt is not null)
            .Select(a => a.UnlockedAt!.Value)
            .ToList();

        Assert.InRange(unlocks.Count, 50, 100);

        var span = unlocks.Max() - unlocks.Min();
        Assert.InRange(span.TotalDays, 80, 92);

        // Enough distinct days that a daily trend series has something to fit a line to.
        Assert.True(unlocks.Select(u => DateOnly.FromDateTime(u.UtcDateTime)).Distinct().Count() > 30);
    }

    [Fact]
    public async Task The_fixture_unlock_rate_visibly_declines_over_the_window()
    {
        var achievements = new FixtureXboxAchievements(Fixtures);
        var title = await achievements.GetTitleAchievementsAsync(
            FixtureXboxAuth.FixtureXuid,
            XboxTitles.HaloInfinite);

        var unlocks = title.Achievements
            .Where(a => a.UnlockedAt is not null)
            .Select(a => a.UnlockedAt!.Value)
            .OrderBy(u => u)
            .ToList();

        var midpoint = unlocks[0].AddDays((unlocks[^1] - unlocks[0]).TotalDays / 2);
        var firstHalf = unlocks.Count(u => u < midpoint);
        var secondHalf = unlocks.Count - firstHalf;

        // A real trend, not noise: the fixture is a player who picked the game up hard and
        // tailed off, so the charts have something true to show rather than a flat line
        // the significance test would correctly refuse to call.
        Assert.True(
            firstHalf > secondHalf * 1.5,
            $"Expected a clear decline; got {firstHalf} unlocks in the first half and {secondHalf} in the second.");
    }

    [Fact]
    public async Task The_fixture_exercises_the_awkward_parts_of_the_xbox_shape()
    {
        var achievements = new FixtureXboxAchievements(Fixtures);
        var title = await achievements.GetTitleAchievementsAsync(
            FixtureXboxAuth.FixtureXuid,
            XboxTitles.HaloInfinite);

        // Locked achievements exist, and none of them claim to have been unlocked in year 1.
        Assert.Contains(title.Achievements, a => !a.Unlocked);
        Assert.All(title.Achievements.Where(a => !a.Unlocked), a => Assert.Null(a.UnlockedAt));

        // Some are partially complete, which is the InProgress state.
        Assert.Contains(title.Achievements, a => !a.Unlocked && a.ProgressPercent is > 0 and < 100);

        // Some carry no rarity block at all, as older titles do.
        Assert.Contains(title.Achievements, a => a.RarityPercent is null);

        // And some are rare, so the dashboard's rare badge has something to render.
        Assert.Contains(title.Achievements, a => a.IsRare);

        // Gamerscore parsed out of the string rewards.
        Assert.True(title.TotalGamerscore > 1000);
        Assert.True(title.EarnedGamerscore > 0);
        Assert.True(title.EarnedGamerscore < title.TotalGamerscore);
    }

    [Fact]
    public async Task The_recent_feed_is_newest_first_and_spans_more_than_one_title()
    {
        var recent = await new FixtureXboxAchievements(Fixtures)
            .GetRecentAchievementsAsync(FixtureXboxAuth.FixtureXuid, maxItems: 40);

        Assert.NotEmpty(recent);

        for (var i = 1; i < recent.Count; i++)
        {
            Assert.True(
                (recent[i - 1].UnlockedAt ?? DateTimeOffset.MinValue) >= (recent[i].UnlockedAt ?? DateTimeOffset.MinValue),
                "The recent feed must be ordered newest first.");
        }

        Assert.True(recent.Select(a => a.TitleId).Distinct().Count() > 1);
    }

    [Fact]
    public async Task The_fixture_gamertag_is_a_homoglyph_and_says_so_when_it_is_mistyped()
    {
        var achievements = new FixtureXboxAchievements(Fixtures);

        // Exactly the real tag: found.
        var found = await achievements.ResolveGamertagAsync(FixtureXboxAuth.FixtureGamertag);
        Assert.NotNull(found);
        Assert.Equal(FixtureXboxAuth.FixtureXuid, found.Xuid);
        Assert.True(Identity.LooksLikeHomoglyph(found.Gamertag));

        // The version a person can type: not found, but with an explanation rather than a
        // silent null, because a silent null is what makes people think it is broken.
        var error = await Assert.ThrowsAsync<TrackerException>(() => achievements.ResolveGamertagAsync("Ilissu"));

        Assert.Contains("U+0406", error.Message, StringComparison.Ordinal);
        Assert.Contains("XUID", error.Remedy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FixtureXboxAuth.FixtureXuid, error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completely_different_gamertag_is_a_plain_null()
    {
        Assert.Null(await new FixtureXboxAchievements(Fixtures).ResolveGamertagAsync("SomeoneElse"));
    }

    [Fact]
    public async Task The_title_history_fixture_carries_the_metadata_the_summary_needs()
    {
        var titles = await new FixtureXboxAchievements(Fixtures).GetTitleHistoryAsync(FixtureXboxAuth.FixtureXuid);

        var halo = Assert.Single(titles, t => t.TitleId == XboxTitles.HaloInfinite);

        Assert.Equal("Halo Infinite", halo.Name);
        Assert.NotNull(halo.LastPlayed);
        Assert.True(halo.TotalGamerscore > 0);
        Assert.True(halo.TotalAchievements > 100);
    }

    [Fact]
    public async Task The_fixtures_are_available_even_with_no_source_tree_on_disk()
    {
        // Point the store at a directory that does not exist. The embedded copies must
        // still answer, because a published build has no Career Stats Shared/fixtures next to
        // it and "works with zero credentials" has to survive that.
        var embeddedOnly = new FixtureStore(Path.Combine(Path.GetTempPath(), "eet-no-such-fixture-dir"));

        var raw = await embeddedOnly.ReadAsync(FixtureXboxAchievements.HaloAchievementsFixture);

        Assert.Contains("SYNTHETIC", raw, StringComparison.Ordinal);
        Assert.Contains(FixtureXboxAchievements.ProfileFixture, embeddedOnly.Names());
    }

    [Fact]
    public async Task A_missing_fixture_names_where_it_looked()
    {
        var store = new FixtureStore(Path.Combine(Path.GetTempPath(), "eet-no-such-fixture-dir"));

        var error = await Assert.ThrowsAsync<TrackerException>(() => store.ReadAsync("not-a-fixture.json"));

        Assert.Contains("not-a-fixture.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("EET_FIXTURES_DIR", error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fixtures_on_disk_are_found_from_the_test_assembly()
    {
        // Not strictly required -- the embedded copies would do -- but if the walk-up
        // search has broken, the owner's "edit a fixture and rerun" loop has broken too.
        Assert.NotNull(Fixtures.Directory);
        Assert.Contains(FixtureXboxAchievements.HaloAchievementsFixture, Fixtures.Names());
    }
}
