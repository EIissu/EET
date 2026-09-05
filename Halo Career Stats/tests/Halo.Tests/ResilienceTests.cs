using System.Globalization;
using System.Net;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// Retry, backoff and the politeness cap. Nothing here sleeps: the delay function is
/// injected, so the schedule is asserted rather than waited out.
/// </summary>
public sealed class ResilienceTests
{
    [Fact]
    public async Task RetriesA429AndHonoursRetryAfterSeconds()
    {
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.TooManyRequests, retryAfter: "7")
            .ThenJson("""{"ok":true}""");

        // The cap has to be above the asked-for wait, or the clamp is what is under test.
        var (handler, waits) = Handler(TestEnv.Options(o => o.MaxRetryDelay = TimeSpan.FromSeconds(30)));
        var response = await Send(handler, stub, HaloEndpointIds.MatchHistory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);

        // The server named a number. Waiting less than it is the one behaviour guaranteed
        // to make throttling worse.
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(waits));
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDateIsAlsoHonoured()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(4);
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.ServiceUnavailable,
                retryAfter: when.ToString("R", CultureInfo.InvariantCulture))
            .ThenJson("{}");

        var (handler, waits) = Handler(TestEnv.Options(o => o.MaxRetryDelay = TimeSpan.FromSeconds(30)));
        await Send(handler, stub, HaloEndpointIds.MatchHistory);

        var wait = Assert.Single(waits);
        Assert.InRange(wait.TotalSeconds, 1.0, 6.0);
    }

    [Fact]
    public async Task ARetryAfterLongerThanTheCapIsClampedRatherThanObeyed()
    {
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.TooManyRequests, retryAfter: "600")
            .ThenJson("{}");

        var options = TestEnv.Options(o => o.MaxRetryDelay = TimeSpan.FromSeconds(20));
        var (handler, waits) = Handler(options);
        await Send(handler, stub, HaloEndpointIds.MatchHistory);

        // A ten-minute wait should fail a dashboard request, not hang it.
        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(waits));
    }

    [Fact]
    public async Task BackoffIsExponentialWithJitterWhenTheServerSaysNothing()
    {
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.InternalServerError)
            .ThenStatus(HttpStatusCode.BadGateway)
            .ThenStatus(HttpStatusCode.ServiceUnavailable)
            .ThenJson("{}");

        var options = TestEnv.Options(o =>
        {
            o.BaseRetryDelay = TimeSpan.FromMilliseconds(100);
            o.MaxRetries = 3;
            o.MaxRetryDelay = TimeSpan.FromSeconds(30);
        });

        // Jitter pinned at its maximum so the schedule is deterministic; the point of the
        // test is the doubling, not the randomness.
        var (handler, waits) = Handler(options, random: () => 1.0);
        var response = await Send(handler, stub, HaloEndpointIds.MatchHistory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)],
            waits);
    }

    [Fact]
    public async Task FullJitterMeansTheWaitIsNeverMoreThanTheExponential()
    {
        var stub = new StubHandler()
            .ThenStatus(HttpStatusCode.InternalServerError)
            .ThenJson("{}");

        var options = TestEnv.Options(o => o.BaseRetryDelay = TimeSpan.FromMilliseconds(400));
        var (handler, waits) = Handler(options, random: () => 0.25);
        await Send(handler, stub, HaloEndpointIds.MatchHistory);

        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(waits));
    }

    [Fact]
    public async Task GivesUpAfterMaxRetriesRatherThanHammering()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{}"),
        });

        var options = TestEnv.Options(o => o.MaxRetries = 2);
        var (handler, waits) = Handler(options);
        var response = await Send(handler, stub, HaloEndpointIds.MatchHistory);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, stub.Requests.Count);   // one attempt plus two retries
        Assert.Equal(2, waits.Count);
    }

    [Fact]
    public async Task A403IsNeverRetried()
    {
        // Repeating a request the server has already refused is how a fan tool gets its
        // whole user base blocked. A private profile is a 403 and it will still be a 403
        // in fifty milliseconds.
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{}"),
        });

        var (handler, waits) = Handler(TestEnv.Options());
        var response = await Send(handler, stub, HaloEndpointIds.MatchHistory);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(stub.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A404IsRetriedOnlyWhereTheManifestSaysItShouldBe()
    {
        // Stats_GetMatchStats uses linearretry404retry: RetryIfNotFound is true, because a
        // match document is written asynchronously after the game ends.
        Assert.True(TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchStats).Retry.RetryIfNotFound);
        Assert.False(TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchHistory).Retry.RetryIfNotFound);

        var retryable = new StubHandler().ThenStatus(HttpStatusCode.NotFound).ThenJson("{}");
        var (handlerA, waitsA) = Handler(TestEnv.Options());
        var found = await Send(handlerA, retryable, HaloEndpointIds.MatchStats);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Single(waitsA);

        var terminal = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}"),
        });
        var (handlerB, waitsB) = Handler(TestEnv.Options());
        var missing = await Send(handlerB, terminal, HaloEndpointIds.MatchHistory);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Single(terminal.Requests);
        Assert.Empty(waitsB);
    }

    [Fact]
    public async Task ConcurrencyIsCappedAtTheConfiguredLimit()
    {
        var gate = new TaskCompletionSource();
        var inFlight = 0;
        var peak = 0;

        var stub = new StubHandler(_ =>
        {
            var current = Interlocked.Increment(ref inFlight);
            var seen = Volatile.Read(ref peak);
            while (current > seen && Interlocked.CompareExchange(ref peak, current, seen) != seen)
            {
                seen = Volatile.Read(ref peak);
            }

            Interlocked.Decrement(ref inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var concurrency = new HaloConcurrencyHandler(3) { InnerHandler = new BlockingHandler(gate.Task, stub) };
        using var http = new HttpClient(concurrency);

        var call = HaloCall.Create(
            TestEnv.Endpoints.Resolve(HaloEndpointIds.MatchStats),
            HaloCachePolicy.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["matchId"] = "abc" });

        var requests = Enumerable.Range(0, 12).Select(_ => http.SendAsync(call.ToRequest())).ToList();
        await Task.Delay(50);
        gate.SetResult();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(3, concurrency.Limit);
        Assert.True(concurrency.PeakInFlight <= 3, $"peak in flight was {concurrency.PeakInFlight}");
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static (HaloResilienceHandler Handler, List<TimeSpan> Waits) Handler(
        HaloOptions options,
        Func<double>? random = null)
    {
        var waits = new List<TimeSpan>();
        var handler = new HaloResilienceHandler(
            options,
            logger: null,
            delay: (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            random: random ?? (() => 1.0));

        return (handler, waits);
    }

    private static async Task<HttpResponseMessage> Send(
        HaloResilienceHandler handler,
        HttpMessageHandler primary,
        string endpointId)
    {
        handler.InnerHandler = primary;
        using var http = new HttpClient(handler);

        var call = HaloCall.Create(
            TestEnv.Endpoints.Resolve(endpointId),
            HaloCachePolicy.None,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["player"] = Identity.XuidRef(TestEnv.Xuid),
                ["matchId"] = "11111111-2222-3333-4444-555555555555",
            });

        return await http.SendAsync(call.ToRequest());
    }

    /// <summary>Holds every request until a gate opens, so concurrency can actually pile up.</summary>
    private sealed class BlockingHandler : DelegatingHandler
    {
        private readonly Task _gate;

        public BlockingHandler(Task gate, HttpMessageHandler inner)
        {
            _gate = gate;
            InnerHandler = inner;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
