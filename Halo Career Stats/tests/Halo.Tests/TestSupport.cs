using System.Net;
using System.Text;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;

namespace Eet.Halo.Tests;

/// <summary>
/// A stub primary handler. Records every request it sees and replays a scripted sequence
/// of responses, so a test can assert both what went out and what the client did with what
/// came back. No socket is ever opened.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _fallback;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage>? fallback = null) => _fallback = fallback;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Headers are captured separately: HttpRequestMessage is not safe to read after disposal.</summary>
    public List<Dictionary<string, string[]>> Headers { get; } = [];

    public StubHandler Then(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        _script.Enqueue(response);
        return this;
    }

    public StubHandler ThenStatus(HttpStatusCode status, string? retryAfter = null, string body = "{}")
    {
        return Then(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        });
    }

    public StubHandler ThenJson(string json) => Then(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Headers.Add(request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase));

        var next = _script.Count > 0
            ? _script.Dequeue()
            : _fallback ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });

        return Task.FromResult(next(request));
    }
}

/// <summary>
/// A stand-in for the token chain. This is a test double for the INTERFACE only -- the real
/// implementation is Eet.Xbox's job and nothing here should be mistaken for it.
/// </summary>
public sealed class FakeXboxAuth : IXboxAuth
{
    private readonly string _spartan;

    public FakeXboxAuth(string spartan = "test-spartan-token") => _spartan = spartan;

    public int SpartanRequests { get; private set; }

    public Task<XstsToken> GetXstsTokenAsync(string relyingParty, CancellationToken ct = default) =>
        Task.FromResult(new XstsToken("xsts", "userhash", DateTimeOffset.UtcNow.AddHours(1), "2814669301245176"));

    public Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct = default)
    {
        SpartanRequests++;
        return Task.FromResult(new SpartanToken(_spartan, DateTimeOffset.UtcNow.AddHours(1)));
    }
}

/// <summary>Shared helpers: where the fixtures are, and how to assemble a client over a stub.</summary>
public static class TestEnv
{
    /// <summary>
    /// Walk up from the test binary to Career Stats Shared/fixtures. The tests use the real
    /// fixtures on purpose -- a fixture that only satisfies a hand-written test is not a
    /// fixture, it is a mock with extra steps.
    /// </summary>
    public static string FixtureDirectory { get; } =
        FixtureHaloTransport.Locate("Career Stats Shared/fixtures", AppContext.BaseDirectory)
        ?? throw new InvalidOperationException(
            $"Could not find Career Stats Shared/fixtures walking up from {AppContext.BaseDirectory}.");

    public static HaloOptions Options(Action<HaloOptions>? configure = null)
    {
        var options = new HaloOptions
        {
            // No disk cache in tests: it would leak state between runs and make failures
            // depend on what a previous test happened to write.
            CacheDirectory = string.Empty,
            MaxRetries = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };
        configure?.Invoke(options);
        return options;
    }

    public static HaloEndpointResolver Endpoints { get; } = new(HaloEndpointManifest.Default);

    public static FixtureHaloTransport Fixtures() => new(FixtureDirectory);

    public static HaloClient FixtureClient(HaloOptions? options = null) =>
        new(Fixtures(), Endpoints, options ?? Options());

    public static HaloCareerSource FixtureSource(HaloOptions? options = null)
    {
        var opts = options ?? Options();
        var fixtures = Fixtures();
        return new HaloCareerSource(
            new HaloClient(fixtures, Endpoints, opts),
            new FixturePlayerDirectory(fixtures),
            opts);
    }

    /// <summary>The synthetic player the fixtures describe.</summary>
    public const string Xuid = "2814669301245176";

    /// <summary>
    /// The homoglyph gamertag, written as an escape rather than as the literal character.
    ///
    /// Every source file in this tracker is deliberately pure ASCII. A .cs file with no
    /// byte-order mark and a non-ASCII literal in it is at the mercy of whatever encoding
    /// the compiler guesses, and on a machine whose ANSI codepage is 1252 the guess turns
    /// U+0415 into a mojibake pair -- which is exactly the class of bug this constant exists
    /// to test. An escape cannot be mis-decoded.
    /// </summary>
    public const string Gamertag = "\u0415lissu";
}

/// <summary>
/// A transport that answers from a script rather than from disk or from 343, so the
/// components that sit ON the transport -- paging, clearance caching -- can be driven into
/// states no recorded fixture contains.
/// </summary>
public sealed class ScriptedTransport : IHaloTransport
{
    private readonly Func<HaloCall, string?> _answer;

    public ScriptedTransport(Func<HaloCall, string?> answer) => _answer = answer;

    public bool IsFixture => true;

    public string Description => "scripted";

    public List<HaloCall> Calls { get; } = [];

    public Task<string> GetJsonAsync(HaloCall call, CancellationToken ct = default) =>
        TryGetJsonAsync(call, ct).ContinueWith(
            t => t.Result ?? throw new TrackerException("nothing scripted", "add a script entry"),
            ct,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public Task<string?> TryGetJsonAsync(HaloCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Task.FromResult(_answer(call));
    }
}

/// <summary>A primary handler that always throws, for the failures that never reach a status code.</summary>
public sealed class ThrowingHandler : HttpMessageHandler
{
    private readonly Func<Exception> _error;

    public ThrowingHandler(Func<Exception> error) => _error = error;

    public int Attempts { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Attempts++;
        throw _error();
    }
}

/// <summary>
/// A clock the test drives by hand. Written locally rather than pulled from
/// Microsoft.Extensions.TimeProvider.Testing so the suite needs no package that is not
/// already on the machine -- and it needs exactly one method.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
