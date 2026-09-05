using System.Net;
using System.Text;

namespace Eet.Destiny.Tests;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/>. Nothing in this test project touches the
/// network, and nothing needs a Bungie API key.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
    private int _calls;

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Always answer with the same body and HTTP 200.</summary>
    public static StubHandler Always(string body) =>
        new((_, _) => Ok(body));

    /// <summary>Answer each call from the list in turn, repeating the last entry.</summary>
    public static StubHandler Sequence(params string[] bodies) =>
        new((_, index) => Ok(bodies[Math.Min(index, bodies.Length - 1)]));

    /// <summary>Every request that reached the handler, method and path with query.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Request bodies, indexed alongside <see cref="Requests"/>. Null for GETs.</summary>
    public List<string?> Bodies { get; } = [];

    public int CallCount => _calls;

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.Method} {request.RequestUri?.PathAndQuery}");
        Bodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var index = _calls;
        _calls++;
        return _respond(request, index);
    }
}

/// <summary>Small builders for Bungie-shaped bodies, so the tests read as the API does.</summary>
public static class Envelopes
{
    public static string Success(string responseJson) =>
        "{\"Response\":" + responseJson
        + ",\"ErrorCode\":1,\"ThrottleSeconds\":0,\"ErrorStatus\":\"Success\","
        + "\"Message\":\"Ok\",\"MessageData\":{}}";

    /// <summary>A failure, served the way Bungie serves it: with HTTP 200.</summary>
    public static string Failure(int errorCode, string status, string message = "", int throttleSeconds = 0) =>
        "{\"ErrorCode\":" + errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"ThrottleSeconds\":" + throttleSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"ErrorStatus\":\"" + status + "\",\"Message\":\"" + message + "\",\"MessageData\":{}}";

    /// <summary>ErrorCode 1 with no Response at all, as returned past the end of a history.</summary>
    public const string SuccessWithNoPayload =
        """{"ErrorCode":1,"ThrottleSeconds":0,"ErrorStatus":"Success","Message":"Ok","MessageData":{}}""";
}
