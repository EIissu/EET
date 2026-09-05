using System.Globalization;
using Eet.Trackers.Core;

namespace Eet.Xbox.Tests;

/// <summary>
/// Raw Xbox JSON into the records in Contracts.cs.
///
/// Each of these pins one detail of the Xbox shape that is easy to read past and produces
/// data that looks plausible when got wrong -- which is worse than a crash, because nobody
/// investigates a number that renders.
/// </summary>
public sealed class AchievementMappingTests
{
    private static IReadOnlyList<Achievement> Mapped =>
        AchievementMapper.MapAchievements(Responses.Achievements, XboxTitles.HaloInfinite);

    [Fact]
    public void Gamerscore_comes_out_of_the_rewards_array_not_a_field()
    {
        var unlocked = Mapped[0];

        // 20 gamerscore plus a cosmetic "InApp" reward worth 1 of something else. Summing
        // every reward regardless of type would report 21.
        Assert.Equal(20, unlocked.Gamerscore);
        Assert.Equal(50, Mapped[1].Gamerscore);
    }

    [Fact]
    public void Progress_state_is_a_string_not_a_boolean()
    {
        Assert.True(Mapped[0].Unlocked);
        Assert.False(Mapped[1].Unlocked);
    }

    [Fact]
    public void A_locked_achievement_has_no_unlock_time_despite_carrying_one()
    {
        // The wire says "0001-01-01T00:00:00.0000000". Trusted, a dashboard renders
        // "unlocked 2025 years ago" and sorts every locked achievement to the top.
        Assert.Null(Mapped[1].UnlockedAt);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 14, 20, 31, 7, 472, TimeSpan.Zero),
            Mapped[0].UnlockedAt);
    }

    [Fact]
    public void Progress_is_averaged_across_a_multi_requirement_achievement()
    {
        // 30/100 and 10/50 -> 30% and 20% -> 25%.
        Assert.Equal(25, Mapped[1].ProgressPercent);
    }

    [Fact]
    public void An_unlocked_achievement_is_a_hundred_percent_without_consulting_requirements()
    {
        Assert.Equal(100, Mapped[0].ProgressPercent);
    }

    [Fact]
    public void A_locked_achievement_shows_how_to_get_it_and_an_unlocked_one_shows_what_it_meant()
    {
        Assert.Equal("You captured twenty-five zones.", Mapped[0].Description);
        Assert.Equal("Score 100 flag captures", Mapped[1].Description);
    }

    [Fact]
    public void Rarity_is_read_when_present_and_absent_without_throwing()
    {
        Assert.True(Mapped[0].IsRare);
        Assert.Equal(4.5, Mapped[0].RarityPercent);

        // Older titles omit the block entirely.
        Assert.False(Mapped[1].IsRare);
        Assert.Null(Mapped[1].RarityPercent);
    }

    [Fact]
    public void The_title_association_supplies_the_id_and_name()
    {
        Assert.Equal(XboxTitles.HaloInfinite, Mapped[0].TitleId);
        Assert.Equal("Halo Infinite", Mapped[0].TitleName);
    }

    [Fact]
    public void The_icon_is_taken_from_the_media_asset_typed_Icon()
    {
        Assert.Equal("https://images-eds-ssl.xboxlive.com/image?url=one", Mapped[0].IconUrl);
    }

    [Fact]
    public void Summarising_counts_only_unlocked_gamerscore_as_earned()
    {
        var summary = AchievementMapper.Summarise(XboxTitles.HaloInfinite, Mapped);

        Assert.Equal(20, summary.EarnedGamerscore);
        Assert.Equal(70, summary.TotalGamerscore);
        Assert.Equal(1, summary.EarnedCount);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(20d / 70d, summary.Completion, 10);
    }

    [Fact]
    public void Title_metadata_totals_win_over_the_page_we_happened_to_fetch()
    {
        var metadata = AchievementMapper.MapTitles(Responses.TitleHistory)[XboxTitles.HaloInfinite];
        var summary = AchievementMapper.Summarise(XboxTitles.HaloInfinite, Mapped, metadata);

        // The title hub knows the game has 119 achievements worth 2420; the two we happen
        // to have fetched do not change that.
        Assert.Equal(2420, summary.TotalGamerscore);
        Assert.Equal(119, summary.TotalCount);

        // Earned still comes from what we actually saw.
        Assert.Equal(20, summary.EarnedGamerscore);
        Assert.Equal(1, summary.EarnedCount);
    }

    [Fact]
    public void LastPlayed_prefers_the_title_hub_over_the_most_recent_unlock()
    {
        var metadata = AchievementMapper.MapTitles(Responses.TitleHistory)[XboxTitles.HaloInfinite];
        var summary = AchievementMapper.Summarise(XboxTitles.HaloInfinite, Mapped, metadata);

        // You keep playing after you stop unlocking things. The last unlock is 14 July;
        // the game was last launched on 1 September.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 18, 45, 0, TimeSpan.Zero), summary.LastPlayed);
    }

    [Fact]
    public void LastPlayed_falls_back_to_the_most_recent_unlock_when_the_title_hub_is_unavailable()
    {
        var summary = AchievementMapper.Summarise(XboxTitles.HaloInfinite, Mapped);

        Assert.Equal(new DateTimeOffset(2026, 7, 14, 20, 31, 7, 472, TimeSpan.Zero), summary.LastPlayed);
    }

    [Fact]
    public void The_profile_settings_list_is_looked_up_by_id_not_by_position()
    {
        var profile = AchievementMapper.MapProfile(Responses.Profile);

        Assert.NotNull(profile);
        Assert.Equal("2814648798129555", profile.Xuid);
        Assert.Equal("Fixture Player", profile.Gamertag);
        Assert.Equal(1475, profile.Gamerscore);
        Assert.Equal("https://images-eds-ssl.xboxlive.com/image?url=pic", profile.IconUrl);
    }

    [Fact]
    public void A_mapped_profile_becomes_a_Player_keyed_on_the_xuid()
    {
        var player = AchievementMapper.MapProfile(Responses.Profile)!.ToPlayer();

        // Id is the XUID, not the gamertag: display names are mutable and, for a homoglyph
        // tag, not typeable.
        Assert.Equal("2814648798129555", player.Id);
        Assert.Equal("Fixture Player", player.Handle);
        Assert.Equal("Xbox", player.Platform);
    }

    [Fact]
    public void A_continuation_token_is_reported_when_present_and_null_when_not()
    {
        Assert.Equal("PAGE-TWO", AchievementMapper.ContinuationToken(Responses.AchievementsPageOne));
        Assert.Null(AchievementMapper.ContinuationToken(Responses.AchievementsPageTwo));
        Assert.Null(AchievementMapper.ContinuationToken(Responses.Achievements));
    }

    [Fact]
    public void An_empty_achievements_array_maps_to_an_empty_list_rather_than_throwing()
    {
        var mapped = AchievementMapper.MapAchievements("""
            { "achievements": [], "pagingInfo": { "continuationToken": null } }
            """);

        Assert.Empty(mapped);

        var summary = AchievementMapper.Summarise(XboxTitles.HaloInfinite, mapped);
        Assert.Equal(0, summary.Completion);
        Assert.Null(summary.LastPlayed);
    }

    [Fact]
    public void A_decimal_on_the_wire_is_read_with_a_dot_not_a_comma()
    {
        // The obvious test -- switch to de-DE and re-parse -- cannot be written here:
        // Directory.Build.props sets InvariantGlobalization, so CultureInfo.GetCultureInfo
        // throws for any named culture and the process has no other culture to switch to.
        // That is a strong guarantee, but it is a build setting rather than a property of
        // this code, so the parsing is pinned directly instead: 4.5 must be four and a half,
        // never forty-five, and a rarity percentage read the wrong way would render a common
        // achievement as a rare one.
        var mapped = AchievementMapper.MapAchievements(Responses.Achievements, XboxTitles.HaloInfinite);

        Assert.Equal(4.5, mapped[0].RarityPercent);
        Assert.Equal(20, mapped[0].Gamerscore);
        Assert.Equal(25, mapped[1].ProgressPercent);

        // And the shared formatter renders it the way a chart axis will read it.
        Assert.Equal("4.50", Format.Ratio(mapped[0].RarityPercent!.Value));
        Assert.DoesNotContain(",", Format.Ratio(mapped[0].RarityPercent!.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_a_TrackerException_with_a_remedy()
    {
        var error = Assert.Throws<TrackerException>(() => AchievementMapper.MapAchievements("{ not json"));

        Assert.NotNull(error.Remedy);
        Assert.Contains("fixture", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }
}
