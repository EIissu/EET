using System.Globalization;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Halo.Client.Mapping;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The zero-credential path end to end. This is the non-negotiable one: the owner has no
/// API keys and has to be able to run the tracker today.
/// </summary>
public sealed class FixturePathTests
{
    [Fact]
    public async Task ACompleteSnapshotIsProducedWithNoCredentials()
    {
        var source = TestEnv.FixtureSource();
        var player = await source.ResolveAsync(TestEnv.Xuid);
        var snapshot = await source.GetSnapshotAsync(player!);

        Assert.True(snapshot.IsFixture);
        Assert.Equal(GameId.HaloInfinite, snapshot.Game);
        Assert.NotEmpty(snapshot.Headline);
        Assert.NotEmpty(snapshot.Trends);
        Assert.NotEmpty(snapshot.Recent);
        Assert.NotEmpty(snapshot.Breakdowns);
        Assert.True(snapshot.Totals.Matches > 100);

        // Nobody should ever mistake this for real data.
        Assert.Contains(snapshot.Warnings, w => w.Contains("SYNTHETIC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFixtureCoversTheAdvertisedShapeOfACareer()
    {
        var source = TestEnv.FixtureSource();
        var matches = await source.GetMatchesAsync(new Player("t", TestEnv.Xuid, "Xbox"), 120);

        Assert.Equal(120, matches.Count);

        var span = matches.Max(m => m.PlayedAt) - matches.Min(m => m.PlayedAt);
        Assert.InRange(span.TotalDays, 80, 95);

        // Lumpy, like a real career: many days with no games and some with several.
        var days = matches.Select(m => DateOnly.FromDateTime(m.PlayedAt.UtcDateTime)).Distinct().Count();
        Assert.InRange(days, 25, 60);

        Assert.True(matches.Select(m => m.Map).Distinct().Count() >= 6);
        Assert.True(matches.Select(m => m.Mode).Distinct().Count() >= 4);
        Assert.Contains(matches, m => m.Won == true);
        Assert.Contains(matches, m => m.Won == false);
        Assert.Contains(matches, m => m.Won is null);   // ties and abandoned games
    }

    [Fact]
    public async Task TheTrendIsRealEnoughToBeCalledOne()
    {
        // The fixture has a deliberate improvement in it, and the point of Trends.cs is
        // that a direction is only reported when it survives a significance test. If this
        // fails, either the fixture's signal is too weak or the fit is broken -- both worth
        // knowing.
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        var kd = snapshot.Trends.Single(t => t.Key == "kd");
        Assert.Equal("improving", kd.Direction);
        Assert.True(kd.SlopePerWeek > 0, $"K/D slope was {kd.SlopePerWeek.ToString(CultureInfo.InvariantCulture)}");
        Assert.Equal(kd.Points.Count, kd.Smoothed.Count);

        var accuracy = snapshot.Trends.Single(t => t.Key == "accuracy");
        Assert.Equal("improving", accuracy.Direction);

        // Every point carries its sample count so the UI can de-emphasise a two-game day.
        Assert.All(kd.Points, p => Assert.True(p.Samples >= 1));
        Assert.True(kd.Points.Select(p => p.Samples).Distinct().Count() > 1);

        // Oldest first, which is what makes a chart's x-axis read left to right.
        Assert.Equal(kd.Points.OrderBy(p => p.Date).Select(p => p.Date), kd.Points.Select(p => p.Date));
    }

    [Fact]
    public async Task HeadlineNumbersAreAggregateRatesThatMatchTheTotals()
    {
        // The distinction Trends.cs documents: a career K/D is total kills over total
        // deaths, never the mean of per-match ratios. If these two disagree, the headline
        // is showing a number no arithmetic on the totals reproduces.
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        var kd = snapshot.Headline.Single(k => k.Key == "kd");
        Assert.Equal(snapshot.Totals.Kd, kd.Value, 6);

        var perMatchMean = snapshot.Recent.Average(m => m.Kd);
        Assert.NotEqual(perMatchMean, kd.Value, 3);   // and they are genuinely different

        var winRate = snapshot.Headline.Single(k => k.Key == "winRate");
        Assert.InRange(winRate.Value, 0, 1);
        Assert.EndsWith("%", winRate.Formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFormattedNumberIsCultureInvariant()
    {
        // A K/D rendered "1,42" breaks every chart axis. Two layers stop that: the build
        // sets InvariantGlobalization, and every formatter in Format goes through
        // CultureInfo.InvariantCulture explicitly. This asserts the OUTPUT, which is what
        // actually matters, rather than either mechanism.
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        var formatted = snapshot.Headline.Select(k => k.Formatted)
            .Concat(snapshot.Headline.Select(k => k.DeltaFormatted).Where(v => v is not null)!)
            .Concat(snapshot.Breakdowns.SelectMany(b => b.Rows).Select(r => r.Formatted))
            .Select(v => v!)
            .ToList();

        Assert.NotEmpty(formatted);
        foreach (var value in formatted)
        {
            // "1,412" is a thousands separator and legitimate; "1,42" is a decimal comma
            // and is not. The difference is exactly three digits after the comma.
            Assert.DoesNotMatch(@"\d,\d{1,2}(?!\d)", value);
        }

        // And the decimal separator that is used is a dot.
        var kd = snapshot.Headline.Single(k => k.Key == "kd").Formatted;
        Assert.Matches(@"^\d+\.\d{2}$", kd);
        Assert.Equal(
            snapshot.Totals.Kd,
            double.Parse(kd, NumberStyles.Float, CultureInfo.InvariantCulture),
            2);

        // Belt and braces: if InvariantGlobalization is ever turned off, run the real
        // culture swap too rather than silently losing the coverage.
        CultureInfo? german = null;
        try
        {
            german = new CultureInfo("de-DE");
        }
        catch (CultureNotFoundException)
        {
            // Invariant globalization is on, which is the stronger guarantee anyway.
        }

        if (german is not null && german.NumberFormat.NumberDecimalSeparator == ",")
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = german;
                var again = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));
                Assert.Matches(@"^\d+\.\d{2}$", again.Headline.Single(k => k.Key == "kd").Formatted);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }

    [Fact]
    public async Task PagingIsHonouredRatherThanFaked()
    {
        // The fixture transport slices the recorded page by start/count, so the client's
        // paging loop is genuinely exercised offline.
        var client = TestEnv.FixtureClient(TestEnv.Options(o => o.HistoryPageSize = 7));

        var page = await client.GetMatchHistoryAsync(TestEnv.Xuid, start: 10, count: 5);
        Assert.Equal(10, page.Start);
        Assert.Equal(5, page.Matches.Count);

        var firstPage = await client.GetMatchHistoryAsync(TestEnv.Xuid, start: 0, count: 5);
        Assert.NotEqual(firstPage.Matches[0].MatchId, page.Matches[0].MatchId);

        var walked = await client.GetRecentMatchesAsync(TestEnv.Xuid, 30);
        Assert.Equal(30, walked.Count);
        Assert.Equal(30, walked.Select(m => m.MatchId).Distinct().Count());
    }

    [Fact]
    public async Task AskingForMoreThanExistsStopsCleanly()
    {
        var client = TestEnv.FixtureClient(TestEnv.Options(o => o.MatchesToAnalyse = 500));
        var all = await client.GetRecentMatchesAsync(TestEnv.Xuid, 500);

        Assert.Equal(120, all.Count);
    }

    [Fact]
    public async Task FixtureTransportLoadsRawJsonNotAPreBakedSnapshot()
    {
        // If a fixture were a CareerSnapshot, running against fixtures would prove nothing
        // about the code that runs against 343.
        var transport = TestEnv.Fixtures();
        var call = HaloCall.Create(
            TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchCount),
            HaloCachePolicy.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["player"] = Identity.XuidRef(TestEnv.Xuid) });

        var json = await transport.GetJsonAsync(call);

        Assert.Contains("MatchmadeMatchesPlayedCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain("headline", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CareerSnapshot", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnrecordedFixtureIsAnActionableErrorNotACrash()
    {
        var client = TestEnv.FixtureClient();
        var missing = await client.GetMatchStatsAsync("00000000-0000-0000-0000-000000000000");
        Assert.Null(missing);

        var transport = TestEnv.Fixtures();
        var call = HaloCall.Create(
            TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchStats),
            HaloCachePolicy.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["matchId"] = "nope" });

        var error = await Assert.ThrowsAsync<TrackerException>(() => transport.GetJsonAsync(call));
        Assert.Contains("raw API-shaped JSON", error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHomoglyphGamertagIsFoundByTypingTheAsciiLookalike()
    {
        // The case Identity.cs exists for. The fixture tag begins with U+0415 CYRILLIC
        // CAPITAL LETTER IE and cannot be typed; searching the ASCII spelling has to work
        // anyway, and the decoy profile listed first must not win.
        var source = TestEnv.FixtureSource();

        var found = await source.ResolveAsync("Elissu");

        Assert.NotNull(found);
        Assert.Equal(TestEnv.Xuid, found!.Id);
        Assert.Equal(TestEnv.Gamertag, found.Handle);
        Assert.NotEqual("Elissu", found.Handle);
        Assert.True(Identity.LooksLikeHomoglyph(found.Handle));
        Assert.Contains("U+0415", Identity.Explain(found.Handle)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnXuidResolvesWithoutASearchAtAll()
    {
        var source = TestEnv.FixtureSource();

        Assert.Equal(TestEnv.Xuid, (await source.ResolveAsync(TestEnv.Xuid))!.Id);
        Assert.Equal(TestEnv.Xuid, (await source.ResolveAsync($"xuid({TestEnv.Xuid})"))!.Id);
    }

    [Fact]
    public async Task AnUnknownGamertagResolvesToNullRatherThanThrowing()
    {
        // ICareerSource.ResolveAsync documents null as "nothing matches". A gamertag that
        // does not exist is an ordinary answer to an ordinary question, and the API turns it
        // into a 404 carrying the homoglyph explanation -- not a 502, which would claim the
        // tracker itself had broken.
        var source = TestEnv.FixtureSource();

        Assert.Null(await source.ResolveAsync("NotARealTag"));

        var remedy = HaloPlayerQuery.NotFoundRemedy("NotARealTag");
        Assert.Contains("XUID", remedy, StringComparison.Ordinal);
        Assert.Contains("Cyrillic", remedy, StringComparison.Ordinal);

        // And when the typed query itself contains a confusable, name the exact code point.
        Assert.Contains("U+0415", HaloPlayerQuery.NotFoundRemedy(TestEnv.Gamertag), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapAndModeNamesAreResolvedThroughTheDiscoveryFixture()
    {
        var source = TestEnv.FixtureSource();
        var matches = await source.GetMatchesAsync(new Player("t", TestEnv.Xuid, "Xbox"), 40);

        // Names, not GUIDs. If clearance were unavailable live, these would degrade to
        // "Map 8420410b" and everything else would still work -- but with the fixture in
        // place they should resolve.
        Assert.All(matches, m => Assert.DoesNotContain('-', m.Map));
        Assert.All(matches, m => Assert.False(m.Map.StartsWith("Map ", StringComparison.Ordinal)));
        Assert.Contains(matches, m => m.Mode == "Slayer");
        Assert.Contains(matches, m => m.Playlist is not null);
    }

    [Fact]
    public async Task BreakdownsRankOnlyWhatThereIsEnoughDataFor()
    {
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        var best = snapshot.Breakdowns.Single(b => b.Key == "mapBest");
        var worst = snapshot.Breakdowns.Single(b => b.Key == "mapWorst");

        Assert.All(best.Rows, r => Assert.True(r.Samples >= 3));
        Assert.True(best.Rows[0].Value >= worst.Rows[0].Value);
        Assert.All(best.Rows, r => Assert.InRange(r.Share!.Value, 0, 1));

        var modes = snapshot.Breakdowns.Single(b => b.Key == "modeForm");
        Assert.All(modes.Rows, r => Assert.InRange(r.Value, 0, 1));
    }

    [Fact]
    public async Task CsrComesThroughTheClearanceAwarePathAndIsLabelledAsSuch()
    {
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        var csr = snapshot.Headline.Single(k => k.Key == "csr");
        Assert.True(csr.Value > 1000);
        Assert.Contains("clearance", csr.Note!, StringComparison.OrdinalIgnoreCase);

        var series = snapshot.Trends.Single(t => t.Key == "csr");
        Assert.NotEmpty(series.Points);
    }

    [Fact]
    public async Task TheSourceLineSaysWhereEveryNumberCameFrom()
    {
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        Assert.Contains("fixtures", snapshot.Source, StringComparison.OrdinalIgnoreCase);

        // Totals are aggregated from match history by default, because the service record
        // endpoint is not in the manifest.
        Assert.Contains("aggregation", snapshot.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheServiceRecordIsUsedOnlyWhenAskedForAndSaysSo()
    {
        var source = TestEnv.FixtureSource(TestEnv.Options(o => o.UseServiceRecord = true));
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        Assert.Contains("service record", snapshot.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            snapshot.Warnings,
            w => w.Contains("not in 343's published manifest", StringComparison.Ordinal));

        // It describes a whole career, so it must be bigger than the analysed window.
        Assert.True(snapshot.Totals.Matches > 1000);
    }
}
