using System.Net;
using System.Text;
using System.Text.Json;

namespace Eet.Xbox.Tests;

/// <summary>One request as it was actually sent, kept so a test can assert on it.</summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string Body,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The body parsed as JSON, for asserting on request shapes.</summary>
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>A form-encoded body split into its fields. The OAuth endpoints use these.</summary>
    public IReadOnlyDictionary<string, string> Form()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in Body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            fields[Uri.UnescapeDataString(split[0])] =
                split.Length > 1 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : string.Empty;
        }

        return fields;
    }
}

/// <summary>
/// A stub transport.
///
/// The whole test suite runs through this: routes are matched on the request URI in
/// registration order, every request is recorded for assertion, and an unmatched request
/// fails loudly rather than returning a plausible empty body. That last part matters --
/// a stub that silently answers 200 with "{}" turns "the client called the wrong URL" into
/// "the mapping produced nothing", which is a much harder bug to read.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)>
        _routes = [];

    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>The single request sent, failing the test if there was not exactly one.</summary>
    public RecordedRequest Single => Assert.Single(_requests);

    /// <summary>The first recorded request whose URL contains <paramref name="fragment"/>.</summary>
    public RecordedRequest For(string fragment) =>
        _requests.FirstOrDefault(r => r.Uri.AbsoluteUri.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"No request was sent to a URL containing \"{fragment}\". Sent: " +
            string.Join(", ", _requests.Select(r => r.Uri.AbsoluteUri)));

    /// <summary>How many requests went to URLs containing <paramref name="fragment"/>.</summary>
    public int CountFor(string fragment) =>
        _requests.Count(r => r.Uri.AbsoluteUri.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public StubHandler Route(string urlFragment, HttpStatusCode status, string body)
    {
        _routes.Add((
            request => request.RequestUri!.AbsoluteUri.Contains(urlFragment, StringComparison.OrdinalIgnoreCase),
            _ => Respond(status, body)));

        return this;
    }

    public StubHandler Route(string urlFragment, string body) => Route(urlFragment, HttpStatusCode.OK, body);

    /// <summary>
    /// Answer differently on each successive call to the same URL. Used for the token
    /// renewal and pagination tests, where "the second call returns something else" is the
    /// entire point.
    /// </summary>
    public StubHandler RouteSequence(string urlFragment, params (HttpStatusCode Status, string Body)[] responses)
    {
        var index = 0;

        _routes.Add((
            request => request.RequestUri!.AbsoluteUri.Contains(urlFragment, StringComparison.OrdinalIgnoreCase),
            _ =>
            {
                var response = responses[Math.Min(index, responses.Length - 1)];
                index++;
                return Respond(response.Status, response.Body);
            }));

        return this;
    }

    /// <summary>An <see cref="HttpClient"/> wired to this stub. Disposal is the caller's.</summary>
    public HttpClient Client() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, headers));

        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                return respond(request);
            }
        }

        throw new InvalidOperationException(
            $"No stub route matched {request.Method} {request.RequestUri}. " +
            "Add a Route(...) for it, or fix the URL the client is building.");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}

/// <summary>
/// A clock a test can move.
///
/// <see cref="TimeProvider.System"/> would make the expiry tests either slow or flaky, and
/// the device code poll loop would spend five real seconds per iteration. Timers fire
/// immediately here, so a poll loop that waits correctly in production runs instantly in a
/// test without the test having to know it is a loop.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    /// <summary>Every delay completes at once, so waiting logic is exercised without waiting.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new ImmediateTimer(callback, state);

    private sealed class ImmediateTimer : ITimer
    {
        public ImmediateTimer(TimerCallback callback, object? state) => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Records the device code challenge instead of printing it.</summary>
public sealed class RecordingPrompt : IDeviceCodePrompt
{
    public DeviceCodeChallenge? Presented { get; private set; }

    public Task PresentAsync(DeviceCodeChallenge challenge, CancellationToken ct = default)
    {
        Presented = challenge;
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory token store, so no test writes a credential to a real profile.</summary>
public sealed class MemoryTokenStore : IRefreshTokenStore
{
    public CachedRefreshToken? Current { get; set; }

    public int Saves { get; private set; }

    public int Clears { get; private set; }

    public Task<CachedRefreshToken?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveAsync(CachedRefreshToken token, CancellationToken ct = default)
    {
        Current = token;
        Saves++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        Current = null;
        Clears++;
        return Task.CompletedTask;
    }
}
