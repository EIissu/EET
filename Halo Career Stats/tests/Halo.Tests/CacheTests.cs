using System.Net;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The on-disk cache. The behaviour worth pinning is that a finished match is fetched once
/// and never again: it is what makes a 120-match trend view cheap on the second run, and
/// it is only correct because a completed match's stats are genuinely immutable.
/// </summary>
public sealed class CacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "eet-halo-cache-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task AFinishedMatchIsFetchedOnceAndThenServedFromDisk()
    {
        var upstream = 0;
        var stub = new StubHandler(_ =>
        {
            upstream++;
            return Json("""{"MatchId":"m1"}""");
        });

        var (http, auth) = Build(stub);

        var call = MatchStats("11111111-2222-3333-4444-555555555555");
        var first = await Send(http, call);
        var second = await Send(http, call);

        Assert.Equal(first, second);
        Assert.Equal(1, upstream);

        // The cache sits outermost, so a hit costs no token either. That is the difference
        // between a cache and a cache that still hammers the auth service.
        Assert.Equal(1, auth.SpartanRequests);
    }

    [Fact]
    public async Task ForeverReallyMeansForever()
    {
        var upstream = 0;
        var stub = new StubHandler(_ =>
        {
            upstream++;
            return Json("""{"MatchId":"m1"}""");
        });

        var time = new TestClock(DateTimeOffset.UtcNow);
        var (http, _) = Build(stub, time);

        var call = MatchStats("22222222-2222-3333-4444-555555555555");
        await Send(http, call);

        time.Advance(TimeSpan.FromDays(400));
        await Send(http, call);

        Assert.Equal(1, upstream);
    }

    [Fact]
    public async Task MatchHistoryIsCachedOnlyBriefly()
    {
        // Unlike a finished match, history changes the moment the player finishes a game.
        var upstream = 0;
        var stub = new StubHandler(_ =>
        {
            upstream++;
            return Json("""{"Start":0,"Count":1,"ResultCount":0,"Results":[]}""");
        });

        var time = new TestClock(DateTimeOffset.UtcNow);
        var (http, _) = Build(stub, time, historyLifetime: TimeSpan.FromMinutes(2));

        var call = HaloCall.Create(
            TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchHistory),
            HaloCachePolicy.Short,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["player"] = Identity.XuidRef(TestEnv.Xuid) });

        await Send(http, call);
        await Send(http, call);
        Assert.Equal(1, upstream);      // still fresh

        time.Advance(TimeSpan.FromMinutes(5));
        await Send(http, call);
        Assert.Equal(2, upstream);      // stale, refetched
    }

    [Fact]
    public async Task DifferentRequestsDoNotCollide()
    {
        var upstream = 0;
        var stub = new StubHandler(_ =>
        {
            upstream++;
            return Json("""{"MatchId":"m"}""");
        });

        var (http, _) = Build(stub);

        await Send(http, MatchStats("33333333-2222-3333-4444-555555555555"));
        await Send(http, MatchStats("44444444-2222-3333-4444-555555555555"));

        Assert.Equal(2, upstream);
    }

    [Fact]
    public async Task AFailedResponseIsNeverCached()
    {
        // Caching a 500 forever would turn a transient outage into a permanent one.
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.InternalServerError)
            .Then(_ => Json("""{"MatchId":"m1"}"""));

        var (http, _) = Build(stub, options: TestEnv.Options(o => o.MaxRetries = 0));

        var call = MatchStats("55555555-2222-3333-4444-555555555555");
        using var failure = await http.SendAsync(call.ToRequest());
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);

        var body = await Send(http, call);
        Assert.Contains("m1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingTheCacheIsHonoured()
    {
        var upstream = 0;
        var stub = new StubHandler(_ =>
        {
            upstream++;
            return Json("""{"MatchId":"m1"}""");
        });

        var http = HaloTrackerSetup.CreateHttpClient(
            TestEnv.Options(o => o.CacheDirectory = string.Empty),
            new FakeXboxAuth(),
            new StaticClearanceProvider("flight"),
            loggerFactory: null,
            primary: stub);

        var call = MatchStats("66666666-2222-3333-4444-555555555555");
        await Send(http, call);
        await Send(http, call);

        Assert.Equal(2, upstream);
    }

    // ---------------------------------------------------------------- helpers

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HaloCall MatchStats(string matchId) => HaloCall.Create(
        TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchStats),
        HaloCachePolicy.Forever,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["matchId"] = matchId });

    private static async Task<string> Send(HttpClient http, HaloCall call)
    {
        using var response = await http.SendAsync(call.ToRequest());
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The real handler chain with a fake clock, so cache expiry is asserted rather than
    /// waited for.
    /// </summary>
    private (HttpClient Http, FakeXboxAuth Auth) Build(
        HttpMessageHandler primary,
        TimeProvider? time = null,
        TimeSpan? historyLifetime = null,
        HaloOptions? options = null)
    {
        var auth = new FakeXboxAuth();
        var opts = options ?? TestEnv.Options();

        var chain = new HaloAuthHandler(auth, new StaticClearanceProvider("flight")) { InnerHandler = primary };
        var concurrency = new HaloConcurrencyHandler(opts.MaxConcurrentRequests) { InnerHandler = chain };
        var resilience = new HaloResilienceHandler(
            opts, logger: null, delay: (_, _) => Task.CompletedTask, random: () => 0)
        { InnerHandler = concurrency };
        var cache = new HaloDiskCacheHandler(
            _directory,
            historyLifetime ?? opts.HistoryCacheLifetime,
            logger: null,
            time: time)
        { InnerHandler = resilience };

        return (new HttpClient(cache), auth);
    }
}
