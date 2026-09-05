using System.Globalization;
using System.Text;
using Eet.Halo.Client.Endpoints;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Http;

/// <summary>How long a response may be reused.</summary>
public enum HaloCachePolicy
{
    /// <summary>Never store it.</summary>
    None,

    /// <summary>Store it briefly. Match history changes every time the player finishes a game.</summary>
    Short,

    /// <summary>
    /// Store it forever. A finished match's stats are immutable, so re-fetching them is
    /// pure cost to 343 and pure latency to us.
    /// </summary>
    Forever,
}

/// <summary>
/// One resolved request: which endpoint, with which path and query values, and how
/// cacheable the answer is.
///
/// This is the only thing the transports see, which is what lets the fixture transport and
/// the live transport be genuinely interchangeable -- the fixture path resolves endpoints
/// out of 343's manifest and builds the same URI, it simply answers from disk.
/// </summary>
public sealed record HaloCall(
    HaloEndpoint Endpoint,
    IReadOnlyDictionary<string, string> PathArgs,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    HaloCachePolicy Cache)
{
    public static HaloCall Create(
        HaloEndpoint endpoint,
        HaloCachePolicy cache = HaloCachePolicy.None,
        IReadOnlyDictionary<string, string>? pathArgs = null,
        IReadOnlyList<KeyValuePair<string, string>>? query = null) =>
        new(endpoint, pathArgs ?? new Dictionary<string, string>(StringComparer.Ordinal), query ?? [], cache);

    /// <summary>The path with its <c>{placeholders}</c> filled in, query string included.</summary>
    public string PathAndQuery => BuildPathAndQuery();

    public Uri Uri => new(Endpoint.Authority.BaseUri, BuildPathAndQuery());

    /// <summary>
    /// A stable, filesystem-safe key for this exact request. Used both as the disk cache
    /// key and as the lookup key into the recorded fixtures, so a fixture is addressed by
    /// the request that would have produced it.
    /// </summary>
    public string CacheKey =>
        string.Create(CultureInfo.InvariantCulture, $"{Endpoint.Authority.Hostname}{BuildPathAndQuery()}");

    private string BuildPathAndQuery()
    {
        var path = Substitute(Endpoint.PathTemplate);
        var builder = new StringBuilder(path);

        var template = Endpoint.QueryTemplate;
        if (!string.IsNullOrEmpty(template))
        {
            builder.Append(Substitute(template.StartsWith('?') ? template : "?" + template));
        }

        var first = builder.ToString().IndexOf('?', StringComparison.Ordinal) < 0;
        foreach (var (key, value) in Query)
        {
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(EscapeHaloValue(value));
        }

        return builder.ToString();
    }

    private string Substitute(string template)
    {
        if (template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 32);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            var name = template[(open + 1)..close];
            if (!PathArgs.TryGetValue(name, out var value))
            {
                throw new TrackerException(
                    $"Endpoint '{Endpoint.Id}' needs a value for '{{{name}}}' and none was supplied.",
                    $"Pass '{name}' in the call's path arguments. The template is '{Endpoint.PathTemplate}{Endpoint.QueryTemplate}'.");
            }

            builder.Append(EscapeHaloValue(value));
            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Percent-encode a value, then put the parentheses back.
    ///
    /// Every player-shaped path segment on these services is literally <c>xuid(1234...)</c>.
    /// Parentheses are sub-delimiters and legal unescaped in a path segment, and 343's own
    /// client sends them raw; sending <c>xuid%282814...%29</c> instead is a good way to get
    /// an unhelpful 400 or an empty result set. Everything else stays escaped.
    /// </summary>
    internal static string EscapeHaloValue(string value) =>
        Uri.EscapeDataString(value).Replace("%28", "(", StringComparison.Ordinal).Replace("%29", ")", StringComparison.Ordinal);
}
