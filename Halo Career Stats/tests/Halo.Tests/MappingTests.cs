using System.Text.Json;
using Eet.Halo.Client.Mapping;
using Eet.Halo.Client.Model;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The mapping from raw Halo responses into the shared model, driven by the real fixtures.
/// </summary>
public sealed class MappingTests
{
    [Fact]
    public void AccuracyIsAFractionNotAPercentage()
    {
        // The service says 46.875 meaning 46.875%. Shipping that straight through renders a
        // very convincing 4687.5%.
        var core = Core(shotsFired: 320, shotsHit: 150, accuracy: 46.875);
        var fraction = HaloMapper.AccuracyFraction(core);

        Assert.NotNull(fraction);
        Assert.Equal(150.0 / 320.0, fraction!.Value, 6);
        Assert.InRange(fraction.Value, 0, 1);
    }

    [Fact]
    public void AccuracyFallsBackToTheReportedPercentageWhenShotsAreMissing()
    {
        var core = Core(shotsFired: 0, shotsHit: 0, accuracy: 46.875);
        Assert.Equal(0.46875, HaloMapper.AccuracyFraction(core)!.Value, 6);
    }

    [Fact]
    public void StatsAreSummedAcrossEveryTeamBlockNotJustTheFirst()
    {
        // A player who switches teams mid-match accrues stats under each team id. Taking
        // PlayerTeamStats[0] silently under-reports every such match.
        var player = new HaloMatchPlayer(
            "xuid(1)", 1, 0, 2, 1, null,
            [
                new HaloPlayerTeamStats(0, new HaloStatsBundle(Core(100, 40, 0.4, kills: 7, deaths: 5, assists: 2))),
                new HaloPlayerTeamStats(1, new HaloStatsBundle(Core(220, 90, 0.41, kills: 11, deaths: 6, assists: 3))),
            ]);

        var summed = HaloMapper.SumCoreStats(player);

        Assert.NotNull(summed);
        Assert.Equal(18, summed!.Kills);
        Assert.Equal(11, summed.Deaths);
        Assert.Equal(5, summed.Assists);
        Assert.Equal(320, summed.ShotsFired);
        Assert.Equal(130, summed.ShotsHit);
    }

    [Fact]
    public void OutcomesMapToWonWithTiesAndQuitsAsNull()
    {
        Assert.True(HaloEnums.ToWon(2));
        Assert.False(HaloEnums.ToWon(3));

        // Counting a tie or an abandoned game as a loss is how a tracker tells somebody
        // their win rate is worse than it is.
        Assert.Null(HaloEnums.ToWon(1));
        Assert.Null(HaloEnums.ToWon(4));
        Assert.True(HaloEnums.IsDidNotFinish(4));
    }

    [Fact]
    public void BotsAndOtherPlayersAreNotMistakenForTheSubject()
    {
        var stats = new HaloMatchStatsResponse(
            "m1", null,
            null,
            [
                new HaloMatchPlayer("bid(2814669301245176)", 2, 0, 2, 1, null, null),
                new HaloMatchPlayer("xuid(9999999999999999)", 1, 0, 2, 1, null, null),
                new HaloMatchPlayer("xuid(2814669301245176)", 1, 1, 3, 4, null, null),
            ]);

        var found = HaloMapper.FindPlayer(stats, TestEnv.Xuid);

        Assert.NotNull(found);
        Assert.Equal("xuid(2814669301245176)", found!.PlayerId);
        Assert.Equal(1, found.LastTeamId);
    }

    [Fact]
    public void IsoDurationsAreParsedBecauseTimeSpanParseCannot()
    {
        // TimeSpan.Parse throws on all three of these. Every match duration is one of them.
        Assert.Equal(TimeSpan.FromSeconds(632.5), IsoDuration.TryParse("PT10M32.5S"));
        Assert.Equal(new TimeSpan(10, 4, 30, 0), IsoDuration.TryParse("P10DT4H30M"));
        Assert.Equal(TimeSpan.FromHours(2), IsoDuration.TryParse("PT2H"));
        Assert.Null(IsoDuration.TryParse(null));
        Assert.Null(IsoDuration.TryParse("nonsense"));
    }

    [Fact]
    public void UnknownGameVariantCategoriesGetAPlaceholderRatherThanAGuess()
    {
        Assert.Equal("Slayer", HaloEnums.GameVariantCategoryName(6));
        Assert.True(HaloEnums.IsKnownGameVariantCategory(6));

        // 343 does not publish this table and it grows every season. An id we do not know
        // must group correctly and look obviously like a gap.
        Assert.Equal("Mode 137", HaloEnums.GameVariantCategoryName(137));
        Assert.False(HaloEnums.IsKnownGameVariantCategory(137));
    }

    [Fact]
    public void RankFormattingCorrectsTheZeroBasedSubTierAndSpecialCasesOnyx()
    {
        Assert.Equal("Diamond 1", HaloEnums.FormatRank(Csr(1450, "Diamond", subTier: 0)));
        Assert.Equal("Diamond 3", HaloEnums.FormatRank(Csr(1450, "Diamond", subTier: 2)));

        // Above Onyx there are no sub-tiers; the number itself is the rank.
        Assert.Equal("Onyx 1706", HaloEnums.FormatRank(Csr(1706, "Onyx", subTier: 0)));
        Assert.Equal("Unranked", HaloEnums.FormatRank(null));
    }

    [Fact]
    public void ModeNamePrefersThePublishedVariantNameOverTheGuessedTable()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aaaa"] = "Capture the Flag:Ranked",
        };

        var info = Info(category: 137, variantAssetId: "aaaa");

        // The published name wins, and its playlist suffix is trimmed so that ranked and
        // social games of the same mode group together.
        Assert.Equal("Capture the Flag", HaloMapper.ModeName(info, names));

        // With nothing published, fall back -- and the fallback for an unknown id is a
        // placeholder, not an invention.
        Assert.Equal("Mode 137", HaloMapper.ModeName(info, assetNames: null));
    }

    [Fact]
    public void AnUnnamedMapRendersAsAnObviousGapNotAsAGuid()
    {
        var info = Info(category: 6, variantAssetId: "aaaa", mapAssetId: "8420410b-044d-44d7-80b6-98a766c8c39f");
        var name = HaloMapper.MapName(info, assetNames: null);

        Assert.Equal("Map 8420410b", name);
    }

    // ---------------------------------------------------------------- fixture-driven

    [Fact]
    public async Task TheRealFixtureMapsIntoTheSharedModel()
    {
        var client = TestEnv.FixtureClient();
        var history = await client.GetRecentMatchesAsync(TestEnv.Xuid, 5);
        var first = history[0];

        var stats = await client.GetMatchStatsAsync(first.MatchId);
        var skill = await client.GetMatchSkillAsync(first.MatchId, TestEnv.Xuid);
        var summary = HaloMapper.ToMatchSummary(stats, TestEnv.Xuid, null, skill);

        Assert.NotNull(summary);
        Assert.Equal(GameId.HaloInfinite, summary!.Game);
        Assert.Equal(first.MatchId, summary.Id);
        Assert.True(summary.Duration > TimeSpan.Zero);
        Assert.True(summary.Kills >= 0);
        Assert.InRange(summary.Accuracy!.Value, 0, 1);
        Assert.True(summary.Extra!.ContainsKey(HaloMetrics.DamagePerMinute));
    }

    [Fact]
    public async Task TheSubjectIsFoundEvenThoughTheFixtureNeverPutsThemFirst()
    {
        var client = TestEnv.FixtureClient();
        var history = await client.GetRecentMatchesAsync(TestEnv.Xuid, 20);

        var positions = new List<int>();
        foreach (var listing in history)
        {
            var stats = await client.GetMatchStatsAsync(listing.MatchId);
            var players = stats!.Players!;
            positions.Add(players.ToList().FindIndex(p => p.PlayerId == $"xuid({TestEnv.Xuid})"));
        }

        // A mapper that grabbed Players[0] would be wrong every single time here.
        Assert.All(positions, index => Assert.True(index > 0, "the subject should never be at index 0"));
        Assert.True(positions.Distinct().Count() > 1, "the subject's position should vary");
    }

    [Fact]
    public async Task SocialMatchesHaveNoRankAndThatIsNotAnError()
    {
        // ResultCode != 0 with a null Result is how "no rank in this playlist" arrives.
        var client = TestEnv.FixtureClient();
        var history = await client.GetRecentMatchesAsync(TestEnv.Xuid, 120);

        var results = new List<HaloMatchSkillResult?>();
        foreach (var listing in history)
        {
            results.Add(await client.GetMatchSkillAsync(listing.MatchId, TestEnv.Xuid));
        }

        Assert.Contains(results, r => r is null);                       // social
        Assert.Contains(results, r => r?.RankRecap?.PostMatchCsr is not null);  // ranked
    }

    [Fact]
    public void EveryFixtureIsMarkedSynthetic()
    {
        foreach (var path in Directory.GetFiles(TestEnv.FixtureDirectory, "halo-*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(
                document.RootElement.TryGetProperty("_note", out var note),
                $"{Path.GetFileName(path)} has no _note marking it synthetic");
            Assert.Contains("SYNTHETIC", note.GetString()!, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- builders

    private static HaloCoreStats Core(
        int shotsFired = 0,
        int shotsHit = 0,
        double accuracy = 0,
        int kills = 0,
        int deaths = 0,
        int assists = 0) =>
        new(0, 0, 0, 0, 0, kills, deaths, assists, 0, 0, 0, null, 0, 0, 0, 0,
            shotsFired, shotsHit, accuracy, 0, 0, 0, 0, 0, 0, 0, 0, null, 0);

    private static HaloCsr Csr(int value, string tier, int subTier) =>
        new(value, 0, tier, 0, subTier, null, 0, 0, 10);

    private static HaloMatchInfo Info(int category, string variantAssetId, string? mapAssetId = null) =>
        new(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), 3, category,
            LevelId: null,
            MapVariant: mapAssetId is null ? null : new HaloAssetRef(2, mapAssetId, "v"),
            UgcGameVariant: new HaloAssetRef(6, variantAssetId, "v"),
            ClearanceId: null, Playlist: null, PlaylistExperience: 3, PlaylistMapModePair: null,
            SeasonId: null, PlayableDuration: null, TeamsEnabled: true, TeamScoringEnabled: true);
}
