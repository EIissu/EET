using System.Globalization;
using System.Net.Sockets;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Halo.Client.Mapping;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// Bugs that were in this tracker and are not any more. Each test names the wrong behaviour
/// it pins shut, because a regression test whose failure does not say what broke is only
/// half a test.
/// </summary>
public sealed class RegressionTests
{
    // ------------------------------------------------------------- headline deltas

    [Fact]
    public void ADeltaThatFellKeepsItsMinusSign()
    {
        // Was: the sign was only ever written for a POSITIVE delta, so a K/D that dropped
        // by 0.24 rendered as "0.24" -- identical to a gain of 0.24, and the dashboard
        // prints DeltaFormatted verbatim.
        Assert.Equal("-0.24", HaloCareerSource.SignedWith(-0.24, Format.Ratio));
        Assert.Equal("+0.24", HaloCareerSource.SignedWith(0.24, Format.Ratio));
        Assert.Equal("0.00", HaloCareerSource.SignedWith(0, Format.Ratio));

        // The sign has to sit in front of the metric's own unit, which is why Format.Signed
        // cannot do this job.
        Assert.Equal("-1.8%", HaloCareerSource.SignedWith(-0.018, v => Format.Percent(v)));
        Assert.Equal("-33", HaloCareerSource.SignedWith(-33, Format.Integer));
    }

    [Fact]
    public void ADecliningCareerReportsNegativeHeadlineDeltas()
    {
        // The end-to-end version: a player who was better 25 matches ago has to be told so.
        var declining = Career(recentKills: 8, recentDeaths: 16, priorKills: 20, priorDeaths: 8);
        var kpis = HaloCareerSource.BuildHeadline(declining, csr: null, lifetime: null);

        var kd = kpis.Single(k => k.Key == "kd");
        Assert.NotNull(kd.Delta);
        Assert.True(kd.Delta < 0, "the sample career is deliberately a decline");
        Assert.StartsWith("-", kd.DeltaFormatted, StringComparison.Ordinal);
        Assert.False(kd.Improved);

        // And every printed sign agrees with its number, which is the invariant that was
        // actually violated.
        AssertSignsAgree(kpis);
    }

    [Fact]
    public async Task TheRealFixtureAlsoAgreesOnEverySign()
    {
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        AssertSignsAgree(snapshot.Headline);
    }

    private static void AssertSignsAgree(IReadOnlyList<Kpi> kpis)
    {
        var checkedAny = false;
        foreach (var kpi in kpis.Where(k => k.Delta is not null && k.DeltaFormatted is not null))
        {
            checkedAny = true;
            Assert.Equal(kpi.Delta < 0, kpi.DeltaFormatted!.StartsWith('-'));
            Assert.Equal(kpi.Delta > 0, kpi.DeltaFormatted!.StartsWith('+'));
        }

        Assert.True(checkedAny, "no KPI carried a delta, so nothing was actually asserted");
    }

    // ------------------------------------------------------------- paging

    [Fact]
    public async Task APageThatRepeatsItselfStopsTheLoopInsteadOfHammering()
    {
        // Was: only an empty or short page ended the loop, and only NEW matches counted
        // towards the target. A service -- or a caching proxy -- that ignores "start"
        // answers every request with the same full page, so the loop asked 343 for page
        // one forever.
        var page = FullPage(count: 25);
        var transport = new ScriptedTransport(_ => page);
        var client = new HaloClient(transport, TestEnv.Endpoints, TestEnv.Options());

        var collected = await client.GetRecentMatchesAsync(TestEnv.Xuid, 120);

        Assert.Equal(25, collected.Count);
        Assert.True(
            transport.Calls.Count <= 3,
            $"asked for {transport.Calls.Count} pages of a service that only ever has one");
    }

    [Fact]
    public async Task TheCursorAdvancesByWhatCameBackNotByThePageSize()
    {
        // Was: start += pageSize even when the last page was deliberately asked for short,
        // so the matches between start + take and start + pageSize were stepped over.
        var seenStarts = new List<int>();
        var transport = new ScriptedTransport(call =>
        {
            seenStarts.Add(Query(call, "start"));
            return Slice(Query(call, "start"), Query(call, "count"), total: 200);
        });

        var client = new HaloClient(transport, TestEnv.Endpoints, TestEnv.Options(o => o.HistoryPageSize = 10));
        var collected = await client.GetRecentMatchesAsync(TestEnv.Xuid, 25);

        Assert.Equal(25, collected.Count);
        Assert.Equal([0, 10, 20], seenStarts);

        // Contiguous: nothing between two pages was skipped.
        Assert.Equal(Enumerable.Range(0, 25).Select(Id), collected.Select(m => m.MatchId));
    }

    [Fact]
    public async Task PagingStillWalksTheRealFixtureExactly()
    {
        var client = TestEnv.FixtureClient(TestEnv.Options(o => o.HistoryPageSize = 7));
        var walked = await client.GetRecentMatchesAsync(TestEnv.Xuid, 30);

        Assert.Equal(30, walked.Count);
        Assert.Equal(30, walked.Select(m => m.MatchId).Distinct().Count());
    }

    // ------------------------------------------------------------- clearance caching

    [Fact]
    public async Task AFailedFlightLookupIsRetriedRatherThanRememberedForever()
    {
        // Was: the first attempt set a latch, so ONE transient failure disabled rank and
        // asset names for the whole life of the process -- restart or nothing.
        var attempts = 0;
        var transport = new ScriptedTransport(_ =>
        {
            attempts++;
            return attempts == 1 ? null : "{\"FlightConfigurationId\":\"flight-abc\"}";
        });

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var provider = new SettingsClearanceProvider(
            transport,
            TestEnv.Endpoints,
            TestEnv.Options(o => o.ClearanceRetryDelay = TimeSpan.FromMinutes(1)),
            logger: null,
            time: clock);

        Assert.Null(await provider.GetClearanceAsync());

        // Still inside the backoff: no second request, because retrying on every call would
        // just move the problem.
        Assert.Null(await provider.GetClearanceAsync());
        Assert.Equal(1, provider.Fetches);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("flight-abc", await provider.GetClearanceAsync());
        Assert.Equal(2, provider.Fetches);
    }

    [Fact]
    public async Task AFlightIdIsReusedButNotForever()
    {
        // The flight id is mutable -- 343 changes it when they reconfigure a build -- so a
        // process that runs for days has to re-read it, or every clearance-aware request
        // starts 401ing with no way back.
        var served = 0;
        var transport = new ScriptedTransport(_ =>
        {
            served++;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"FlightConfigurationId\":\"flight-{served}\"}}");
        });

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var provider = new SettingsClearanceProvider(
            transport,
            TestEnv.Endpoints,
            TestEnv.Options(o => o.ClearanceLifetime = TimeSpan.FromHours(1)),
            logger: null,
            time: clock);

        Assert.Equal("flight-1", await provider.GetClearanceAsync());
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("flight-1", await provider.GetClearanceAsync());
        Assert.Equal(1, provider.Fetches);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal("flight-2", await provider.GetClearanceAsync());
        Assert.Equal(2, provider.Fetches);
    }

    [Fact]
    public async Task TheFlightLookupAsksTheUrlTheManifestPublishes()
    {
        var transport = new ScriptedTransport(_ => "{\"FlightConfigurationId\":\"f\"}");
        var provider = new SettingsClearanceProvider(transport, TestEnv.Endpoints, TestEnv.Options());

        await provider.GetClearanceAsync();

        var call = Assert.Single(transport.Calls);
        Assert.Equal(HaloEndpointIds.Clearance, call.Endpoint.Id);
        Assert.Equal("settings.svc.halowaypoint.com", call.Endpoint.Authority.Hostname);

        // Audience out of the manifest's own Settings block; sandbox and build out of
        // options, into the manifest's own query template.
        Assert.Equal(
            "/oban/flight-configurations/titles/hi/audiences/RETAIL/active"
            + "?sandbox=UNUSED&build=222249.22.06.08.1730-0&release=1.3",
            call.PathAndQuery);

        // And it is not itself clearance-aware, or fetching it would require itself.
        Assert.False(call.Endpoint.ClearanceAware);
    }

    // ------------------------------------------------------------- transport failures

    [Fact]
    public async Task ANetworkFailureIsAnActionableErrorNotAnUnexpectedOne()
    {
        // Was: HttpRequestException escaped the transport untranslated, so the API answered
        // 500 "this is a bug rather than a configuration problem". A DNS failure is neither.
        using var http = Live(new ThrowingHandler(() =>
            new HttpRequestException("No such host is known.", new SocketException(11001))));

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => new HttpHaloTransport(http).GetJsonAsync(MatchHistoryCall()));

        Assert.Contains("could not be sent", error.Message, StringComparison.Ordinal);
        Assert.Contains("halostats.svc.halowaypoint.com", error.Message, StringComparison.Ordinal);
        Assert.Contains("network", error.Remedy!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fixtures", error.Remedy!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OurOwnTimeoutIsNotReportedAsTheClientHangingUp()
    {
        // Was: HttpClient signals its OWN timeout with TaskCanceledException, which
        // ApiProblems maps to 499 "the client went away before the answer was ready.
        // Nothing to fix." The client had not gone away and there was plenty to fix.
        using var http = Live(new ThrowingHandler(() => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.",
            new TimeoutException())));

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => new HttpHaloTransport(http).GetJsonAsync(MatchHistoryCall()));

        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        Assert.Contains("MaxRetries", error.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerWhoReallyDidCancelStillGetsACancellation()
    {
        // The other half: a genuine cancellation must NOT be dressed up as a tracker
        // failure, or /api/career would answer 502 every time a browser tab closed.
        using var http = Live(new ThrowingHandler(() => new TaskCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new HttpHaloTransport(http).GetJsonAsync(MatchHistoryCall(), cts.Token));
    }

    // ------------------------------------------------------------- identification

    [Fact]
    public async Task EveryLiveRequestSaysWhoItIs()
    {
        // HttpClient sends no User-Agent at all unless told to, which on an undocumented
        // API we are guests on is both rude and the shape of a scraper.
        var stub = new StubHandler();
        var options = TestEnv.Options();
        using var http = HaloTrackerSetup.CreateHttpClient(
            options,
            new FakeXboxAuth(),
            new StaticClearanceProvider("flight"),
            loggerFactory: null,
            primary: stub);

        await new HttpHaloTransport(http).GetJsonAsync(MatchHistoryCall());

        var headers = Assert.Single(stub.Headers);

        // Joined, because HttpClient models User-Agent as a list of products and comments
        // and reassembles it on the wire.
        Assert.Equal(options.UserAgent, string.Join(' ', headers["User-Agent"]));

        // And the Spartan header is still exactly where it was.
        Assert.Equal("test-spartan-token", Assert.Single(headers[HaloAuthHandler.SpartanHeader]));
    }

    // ------------------------------------------------------------- timeout budget

    [Fact]
    public void TheClientTimeoutCanContainTheRetryScheduleItIsPairedWith()
    {
        // Was: a fixed 60s timeout next to defaults whose worst-case backoff is 80s, so a
        // throttled request was killed part-way through a schedule the operator had
        // configured -- and reported as a timeout rather than as the 429 it actually was.
        var options = TestEnv.Options(o =>
        {
            o.MaxRetries = 4;
            o.MaxRetryDelay = TimeSpan.FromSeconds(20);
        });

        var timeout = HaloTrackerSetup.TimeoutFor(options);

        Assert.True(
            timeout > options.MaxRetryDelay * options.MaxRetries,
            $"timeout {timeout} cannot even contain {options.MaxRetries} sleeps of {options.MaxRetryDelay}");

        using var http = HaloTrackerSetup.CreateHttpClient(
            options, new FakeXboxAuth(), new StaticClearanceProvider("f"), primary: new StubHandler());
        Assert.Equal(timeout, http.Timeout);
    }

    [Fact]
    public void AnAbsurdRetryConfigurationStillCannotHangAPageForever()
    {
        var timeout = HaloTrackerSetup.TimeoutFor(TestEnv.Options(o =>
        {
            o.MaxRetries = 500;
            o.MaxRetryDelay = TimeSpan.FromMinutes(10);
        }));

        Assert.Equal(TimeSpan.FromMinutes(5), timeout);
    }

    [Fact]
    public void NoRetriesMeansNoExtraTimeoutBudget()
    {
        var timeout = HaloTrackerSetup.TimeoutFor(TestEnv.Options(o => o.MaxRetries = 0));
        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    // ------------------------------------------------------------- cache location

    [Fact]
    public void TheResponseCacheNeverDefaultsIntoTheWorkingTree()
    {
        // These files are recorded API responses -- a named person's full match history,
        // keyed by XUID. GetFolderPath returns "" rather than throwing when it cannot
        // resolve LocalApplicationData, and Path.Combine("", ...) is a RELATIVE path, which
        // would put them wherever "dotnet run" happened to be started from.
        var directory = HaloDiskCacheHandler.DefaultDirectory;

        Assert.True(Path.IsPathRooted(directory), $"cache directory '{directory}' is relative");
        Assert.EndsWith(Path.Combine("eet-trackers", "halo-cache"), directory, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- helpers

    private static HttpClient Live(HttpMessageHandler primary) =>
        HaloTrackerSetup.CreateHttpClient(
            TestEnv.Options(o => o.MaxRetries = 0),
            new FakeXboxAuth(),
            new StaticClearanceProvider("flight"),
            loggerFactory: null,
            primary: primary);

    private static HaloCall MatchHistoryCall() => HaloCall.Create(
        TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchHistory),
        HaloCachePolicy.None,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["player"] = Identity.XuidRef(TestEnv.Xuid) });

    private static int Query(HaloCall call, string name) =>
        int.Parse(call.Query.First(q => q.Key == name).Value, CultureInfo.InvariantCulture);

    private static string Id(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"00000000-0000-4000-8000-{index:000000000000}");

    private static string FullPage(int count)
    {
        var rows = string.Join(",", Enumerable.Range(0, count).Select(Row));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"Start\":0,\"Count\":{count},\"ResultCount\":{count},\"Results\":[{rows}]}}");
    }

    private static string Slice(int start, int count, int total)
    {
        var take = Math.Max(0, Math.Min(count, total - start));
        var rows = string.Join(",", Enumerable.Range(start, take).Select(Row));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"Start\":{start},\"Count\":{count},\"ResultCount\":{take},\"Results\":[{rows}]}}");
    }

    private static string Row(int index) => string.Create(
        CultureInfo.InvariantCulture,
        $"{{\"MatchId\":\"{Id(index)}\",\"LastTeamId\":0,\"Outcome\":2,\"Rank\":1,\"PresentAtEndOfMatch\":true}}");

    /// <summary>
    /// A career whose most recent window is worse than the one before it. Newest first,
    /// which is the order every headline calculation expects.
    /// </summary>
    private static IReadOnlyList<MatchSummary> Career(
        int recentKills,
        int recentDeaths,
        int priorKills,
        int priorDeaths)
    {
        var start = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero);
        var matches = new List<MatchSummary>();

        for (var i = 0; i < 50; i++)
        {
            var recent = i < 25;
            var kills = recent ? recentKills : priorKills;
            var deaths = recent ? recentDeaths : priorDeaths;

            matches.Add(new MatchSummary(
                Id: Id(i),
                Game: GameId.HaloInfinite,
                PlayedAt: start.AddDays(-i),
                Duration: TimeSpan.FromMinutes(10),
                Mode: "Slayer",
                Map: "Live Fire",
                Playlist: "Ranked Arena",
                Won: !recent,
                Kills: kills,
                Deaths: deaths,
                Assists: 3,
                Accuracy: recent ? 0.40 : 0.50,
                Score: 1000,
                Kda: kills - deaths,
                Extra: new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [HaloMetrics.ShotsHit] = recent ? 40 : 50,
                    [HaloMetrics.ShotsFired] = 100,
                    [HaloMetrics.DamageDealt] = recent ? 4000 : 9000,
                }));
        }

        return matches;
    }
}
