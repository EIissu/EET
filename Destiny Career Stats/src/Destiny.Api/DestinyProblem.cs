using System.Globalization;
using Eet.Destiny.Client;
using Eet.Trackers.Core;

namespace Eet.Destiny.Api;

/// <summary>
/// RFC 7807 responses, with the one addition that matters: a <c>remedy</c>.
///
/// <see cref="TrackerException"/> carries a plain-language fix alongside every failure, and
/// throwing that away at the HTTP boundary would waste the most useful thing the client
/// produces. A caller staring at "ErrorCode 2101" gets told to check the key; a caller
/// staring at a private profile gets told that only the player can change it.
/// </summary>
public static class DestinyProblem
{
    private const string TypeBase = "https://bungie-net.github.io/#PlatformErrorCodes/";

    /// <summary>Run an endpoint, turning any known failure into ProblemDetails.</summary>
    public static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (TrackerException ex)
        {
            return From(ex);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(
                title: "Bungie.net is unreachable",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?>
                {
                    ["remedy"] = "Check network access to https://www.bungie.net. Nothing about this "
                        + "failure came from Bungie: the request did not arrive.",
                });
        }
        catch (TaskCanceledException ex)
        {
            return Results.Problem(
                title: "Bungie.net timed out",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout,
                extensions: new Dictionary<string, object?>
                {
                    ["remedy"] = "Retry. Bungie's activity history endpoint is slow for accounts with "
                        + "long histories; lowering ActivityPageSize also lowers the per-request cost.",
                });
        }
    }

    /// <summary>
    /// Map a client failure onto a status code.
    ///
    /// The choice worth defending: a private profile is 403, not 502. It is a refusal with a
    /// reason, and reporting it as an upstream fault would send an operator hunting for a
    /// problem on this side that does not exist.
    /// </summary>
    public static IResult From(TrackerException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var errorCode = exception.Data["errorCode"] as int?;

        // Two kinds of failure carry no ErrorCode and still know what they mean, so the
        // client stamps the status on directly: a search that matched nothing (Bungie called
        // it a success), and anything that failed before an envelope existed at all -- a
        // maintenance page, a Cloudflare interstitial, a CDN 404. Without this the default
        // below would call every one of them a 400, which blames the caller for an outage.
        var explicitStatus = exception.Data["httpStatus"] as int?;

        var status = explicitStatus ?? errorCode switch
        {
            null => StatusCodes.Status400BadRequest,
            BungiePlatformError.DestinyPrivacyRestriction => StatusCodes.Status403Forbidden,
            BungiePlatformError.ApiInvalidOrExpiredKey or BungiePlatformError.ApiKeyMissingFromRequest =>
                StatusCodes.Status401Unauthorized,
            BungiePlatformError.SystemDisabled => StatusCodes.Status503ServiceUnavailable,
            var code when code is not null && BungiePlatformError.IsThrottle(code.Value) =>
                StatusCodes.Status429TooManyRequests,
            var code when code is not null && BungiePlatformError.IsNotFound(code.Value) =>
                StatusCodes.Status404NotFound,
            _ => StatusCodes.Status502BadGateway,
        };

        var extensions = new Dictionary<string, object?>();
        if (exception.Remedy is not null)
        {
            extensions["remedy"] = exception.Remedy;
        }

        if (errorCode is { } code2)
        {
            extensions["bungieErrorCode"] = code2;
        }

        if (exception.Data["errorStatus"] is string errorStatus)
        {
            extensions["bungieErrorStatus"] = errorStatus;
        }

        if (exception.Data["throttleSeconds"] is int throttleSeconds)
        {
            extensions["throttleSeconds"] = throttleSeconds;
        }

        // The status bungie.net actually answered with, when it differs from the one being
        // reported on. An operator chasing a 502 wants to know it was a 503 behind it.
        if (exception.Data["upstreamStatus"] is int upstreamStatus)
        {
            extensions["bungieHttpStatus"] = upstreamStatus;
        }

        return Results.Problem(
            title: status switch
            {
                StatusCodes.Status400BadRequest => "Bad request",
                StatusCodes.Status404NotFound => "Not found",
                StatusCodes.Status403Forbidden => "Private profile",
                StatusCodes.Status429TooManyRequests => "Rate limited by Bungie",
                StatusCodes.Status503ServiceUnavailable => "Bungie.net is unavailable",
                StatusCodes.Status504GatewayTimeout => "Bungie.net timed out",
                _ => "Bungie API error",
            },
            // The remedy is repeated into `detail` rather than left only in the extension.
            // A generic HTTP client -- including the site's own, and every RFC 7807 viewer
            // ever written -- shows `detail` and nothing else, and "Bungie has no player
            // called Ilissu#9007" on its own is a dead end. The sentence explaining that the
            // name may not be the text it appears to be is the whole value of the answer.
            detail: Detail(exception.Message, exception.Remedy),
            statusCode: status,
            type: errorCode is { } t
                ? TypeBase + t.ToString(CultureInfo.InvariantCulture)
                : null,
            extensions: extensions);
    }

    public static IResult BadRequest(string title, string detail) => Results.Problem(
        title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string title, string detail, string? remedy = null) =>
        Results.Problem(
            title: title,
            detail: Detail(detail, remedy),
            statusCode: StatusCodes.Status404NotFound,
            extensions: remedy is null
                ? null
                : new Dictionary<string, object?> { ["remedy"] = remedy });

    /// <summary>
    /// The 404 for a path under /api that no route claims.
    ///
    /// It exists because the single-page fallback would otherwise answer this with
    /// index.html and HTTP 200. A caller that asked for JSON must get JSON, and a status
    /// that means what it says, however wrong the path was.
    /// </summary>
    public static IResult UnknownApiRoute(string path) => Results.Problem(
        title: "No such API route",
        detail: $"Nothing is mapped at \"{path}\". This tracker serves GET /api/health, "
            + "/api/player?q=, /api/career and /api/matches. Everything outside /api is "
            + "answered with the single-page app instead, which is why this is JSON rather "
            + "than HTML.",
        statusCode: StatusCodes.Status404NotFound,
        instance: path);

    /// <summary>
    /// What went wrong, then what to do about it, in the one field every client displays.
    /// The remedy is not appended twice when the message already ends in it.
    /// </summary>
    private static string Detail(string message, string? remedy) =>
        string.IsNullOrWhiteSpace(remedy) || message.Contains(remedy, StringComparison.Ordinal)
            ? message
            : message.TrimEnd() + " " + remedy.TrimStart();
}

/// <summary>
/// Locating the dashboard another agent is building into <c>Career Stats Shared/web</c>.
///
/// It may not be there. The API has to start anyway, so this returns null rather than
/// throwing, and the health endpoint reports what it found.
/// </summary>
public static class SharedWeb
{
    /// <param name="start">
    /// Where to begin walking up from. When given, it is the only place searched -- a caller
    /// that names a directory means that directory.
    /// </param>
    public static string? Find(string? start = null) => Locate(WebAssets.VanillaDirectory, start);

    /// <summary>
    /// The same walk, for any directory relative to the repository root.
    ///
    /// The built React app lives at <c>Career Stats Web/dist</c> and has to be found the
    /// same way the vanilla dashboard is: this API gets run from its own project directory,
    /// from the repository root, and from a publish output, and the front end sits somewhere
    /// above it in all three cases.
    /// </summary>
    /// <param name="relative">A path relative to the repository root, in either slash style.</param>
    public static string? Locate(string relative, string? start = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        var segments = relative.Split('/', '\\');
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
                foreach (var prefix in Prefixes)
                {
                    var web = prefix.Length == 0
                        ? Path.Combine([directory.FullName, .. segments])
                        : Path.Combine([directory.FullName, prefix, .. segments]);

                    if (Directory.Exists(web))
                    {
                        return web;
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Checked at every level: the directory itself, and one step to the side. The second
    /// is what finds the front end when the walk starts inside a sibling project folder.
    /// </summary>
    private static readonly string[] Prefixes = [string.Empty, ".."];
}
