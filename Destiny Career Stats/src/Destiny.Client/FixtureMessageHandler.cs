using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// Serves the recorded Bungie fixtures as if they had come off the wire.
///
/// This sits underneath <see cref="BungieApiClient"/> rather than replacing it, which is
/// the whole point: with no API key the requests, the ErrorCode envelope handling, the
/// paging, the manifest version check and every line of mapping code run exactly as they do
/// against bungie.net. A fixture source that handed back a pre-built
/// <see cref="CareerSnapshot"/> would test none of that.
///
/// The fixtures are synthetic. They are shaped like Bungie's responses, and the numbers in
/// them were generated, not recorded from a real player.
/// </summary>
public sealed class FixtureMessageHandler : HttpMessageHandler
{
    private readonly string _directory;

    public FixtureMessageHandler(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(directory))
        {
            throw new TrackerException(
                $"No fixture directory at {directory}.",
                "Fixtures live in Career Stats Shared/fixtures. Set Bungie:FixtureDirectory if the "
                + "tracker is running from somewhere that cannot find them by walking up from the "
                + "binary.");
        }
    }

    /// <summary>Requests served so far. Useful for asserting that paging actually stopped.</summary>
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri
            ?? throw new InvalidOperationException("A fixture request must have a URI.");

        lock (Requests)
        {
            Requests.Add($"{request.Method} {uri.PathAndQuery}");
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);

        // Definition tables are plain files on the CDN, with no platform envelope.
        if (segments.Length > 0 && segments[0].Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            var table = segments[^1];
            var file = table.StartsWith("DestinyActivityModeDefinition", StringComparison.OrdinalIgnoreCase)
                ? "destiny-activity-mode-definitions.json"
                : "destiny-activity-definitions.json";

            return Raw(await ReadAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (segments.Length < 2 || !segments[0].Equals("Platform", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(uri.AbsolutePath);
        }

        var route = segments[1..];

        // POST Destiny2/SearchDestinyPlayerByBungieName/{membershipType}/
        if (route is ["Destiny2", "SearchDestinyPlayerByBungieName", _])
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Envelope(await SearchAsync(body, cancellationToken).ConfigureAwait(false));
        }

        // GET User/GetMembershipsById/{membershipId}/{membershipType}/
        if (route is ["User", "GetMembershipsById", _, _])
        {
            return Raw(await ReadAsync("destiny-memberships.json", cancellationToken).ConfigureAwait(false));
        }

        // GET Destiny2/{membershipType}/Profile/{destinyMembershipId}/
        if (route is ["Destiny2", _, "Profile", _])
        {
            return Raw(await ReadAsync("destiny-profile.json", cancellationToken).ConfigureAwait(false));
        }

        // GET Destiny2/{membershipType}/Account/{id}/Character/{characterId}/Stats/
        if (route is ["Destiny2", _, "Account", _, "Character", _, "Stats"])
        {
            return Raw(await ReadAsync("destiny-historical-stats.json", cancellationToken).ConfigureAwait(false));
        }

        // GET Destiny2/{membershipType}/Account/{id}/Character/{characterId}/Stats/Activities/
        if (route is ["Destiny2", _, "Account", _, "Character", var characterId, "Stats", "Activities"])
        {
            query.TryGetValue("page", out var page);
            query.TryGetValue("count", out var count);
            return await ActivitiesAsync(characterId, page, count, cancellationToken).ConfigureAwait(false);
        }

        // GET Destiny2/Stats/PostGameCarnageReport/{activityId}/
        if (route is ["Destiny2", "Stats", "PostGameCarnageReport", var activityId])
        {
            var file = $"destiny-pgcr-{activityId}.json";
            return File.Exists(Path.Combine(_directory, file))
                ? Raw(await ReadAsync(file, cancellationToken).ConfigureAwait(false))
                // Exactly what Bungie does for a report that has aged out: HTTP 200, and a
                // failure in the envelope.
                : Envelope("null", BungiePlatformError.DestinyPGCRNotFound, "DestinyPGCRNotFound");
        }

        // GET Destiny2/Manifest/
        if (route is ["Destiny2", "Manifest"])
        {
            return Raw(await ReadAsync("destiny-manifest.json", cancellationToken).ConfigureAwait(false));
        }

        return NotFound(uri.AbsolutePath);
    }

    /// <summary>
    /// The exact-match search, including the case that matters: a name that does not exist
    /// comes back as ErrorCode 1 with an empty array, not as an error.
    /// </summary>
    private async Task<string> SearchAsync(string requestBody, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(requestBody)
            ? null
            : JsonSerializer.Deserialize<ExactSearchRequest>(requestBody, BungieResponse.Json);

        var json = await ReadAsync("destiny-search-player.json", ct).ConfigureAwait(false);
        var all = BungieResponse.UnwrapOptional<List<UserInfoCard>>(json, "the fixture player search") ?? [];

        if (wanted is null)
        {
            return JsonSerializer.Serialize(all, BungieResponse.Json);
        }

        var matched = all.Where(card =>
                string.Equals(card.BungieGlobalDisplayName, wanted.DisplayName, StringComparison.Ordinal)
                && card.BungieGlobalDisplayNameCode == wanted.DisplayNameCode)
            .ToList();

        return JsonSerializer.Serialize(matched, BungieResponse.Json);
    }

    /// <summary>
    /// One page of a character's history. Paging is real here rather than faked, so the
    /// client's "a short page is the last page" rule is genuinely exercised.
    /// </summary>
    private async Task<HttpResponseMessage> ActivitiesAsync(
        string characterId, string? pageValue, string? countValue, CancellationToken ct)
    {
        var file = $"destiny-activities-{characterId}.json";
        if (!File.Exists(Path.Combine(_directory, file)))
        {
            return Envelope("null", BungiePlatformError.DestinyCharacterNotFound, "DestinyCharacterNotFound");
        }

        var json = await ReadAsync(file, ct).ConfigureAwait(false);
        var results = BungieResponse.UnwrapOptional<ActivityHistoryResults>(json, "fixture activity history");
        var activities = results?.Activities ?? [];

        var page = int.TryParse(pageValue, CultureInfo.InvariantCulture, out var p) ? Math.Max(0, p) : 0;
        var count = int.TryParse(countValue, CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, 1, 250) : 25;

        var slice = activities.Skip(page * count).Take(count).ToList();
        if (slice.Count == 0)
        {
            // Past the end Bungie answers ErrorCode 1 with no Response at all. Getting this
            // wrong -- treating a missing payload as a failure -- is a real bug this fixture
            // exists to catch.
            return Envelope(null, BungiePlatformError.Success, "Success");
        }

        return Envelope(JsonSerializer.Serialize(
            new ActivityHistoryResults { Activities = slice }, BungieResponse.Json));
    }

    /// <summary>
    /// A query string reader, rather than a reference to System.Web, so this project keeps
    /// its "BCL only, no packages, restores offline" property.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0)
            {
                values[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return values;
    }

    private async Task<string> ReadAsync(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
        {
            var missing = new TrackerException(
                $"Missing fixture {fileName}.",
                $"Expected it at {path}. The fixtures are generated by "
                + "Destiny Career Stats/tools/generate-fixtures.py.");

            // A gap in this machine's fixture set is a server-side problem. Left codeless it
            // would reach the caller as 400 Bad Request, blaming them for a file they have
            // never heard of.
            missing.Data["httpStatus"] = 500;
            throw missing;
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Envelope(
        string? responseJson, int errorCode = BungiePlatformError.Success, string status = "Success")
    {
        var builder = new StringBuilder("{");
        if (responseJson is not null)
        {
            builder.Append("\"Response\":").Append(responseJson).Append(',');
        }

        builder.Append(CultureInfo.InvariantCulture, $"\"ErrorCode\":{errorCode},");
        builder.Append("\"ThrottleSeconds\":0,");
        builder.Append(CultureInfo.InvariantCulture, $"\"ErrorStatus\":\"{status}\",");
        builder.Append(CultureInfo.InvariantCulture, $"\"Message\":\"{(errorCode == BungiePlatformError.Success ? "Ok" : status)}\",");
        builder.Append("\"MessageData\":{}}");

        // HTTP 200 even when the envelope carries a failure. That is Bungie's behaviour, and
        // reproducing it is the point.
        return Raw(builder.ToString());
    }

    private static HttpResponseMessage NotFound(string path) => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent(
            $"{{\"ErrorCode\":{BungiePlatformError.NotFound},\"ThrottleSeconds\":0,"
            + $"\"ErrorStatus\":\"NotFound\",\"Message\":\"No fixture route for {path}\",\"MessageData\":{{}}}}",
            Encoding.UTF8,
            "application/json"),
    };
}

/// <summary>Finding the shared fixtures without hard-coding a path.</summary>
public static class FixtureLocator
{
    /// <summary>
    /// Walk up from a starting directory looking for <c>Career Stats Shared/fixtures</c>.
    ///
    /// The binary lands in bin/Debug/net10.0, several levels below the repository, and the
    /// working directory when someone runs <c>dotnet run</c> is the repository root. Walking
    /// up from both covers every way this is actually launched.
    /// </summary>
    /// <param name="start">
    /// Where to begin walking up from. When given, it is the only place searched.
    /// </param>
    public static string? Find(string? start = null)
    {
        var candidates = start is not null
            ? [start]
            : new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                var fixtures = Path.Combine(directory.FullName, "Career Stats Shared", "fixtures");
                if (Directory.Exists(fixtures))
                {
                    return fixtures;
                }

                // Also handles being launched from inside one of the sibling folders.
                var sibling = Path.Combine(directory.FullName, "..", "Career Stats Shared", "fixtures");
                if (Directory.Exists(sibling))
                {
                    return sibling;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
