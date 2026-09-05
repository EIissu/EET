using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Mvc;

namespace Eet.Halo.Api;

/// <summary>
/// JSON conventions for everything this API emits.
///
/// Two deliberate departures from the defaults, both aimed at the dashboard that has to
/// consume this:
///
///   * TimeSpan is written as a number of seconds. The framework default is
///     "00:12:34.5670000", which every JavaScript consumer then has to parse by hand and
///     half of them get wrong.
///
///   * Enums are written as names. "Higher" tells a chart which way is good;
///     <c>0</c> does not.
///
/// Everything numeric is already culture-invariant -- System.Text.Json always writes
/// invariant numbers -- and every pre-formatted string in the payload went through
/// <see cref="Format"/>, which pins InvariantCulture explicitly. A K/D is "1.42" for every
/// reader on earth.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TimeSpanSecondsConverter());
        return options;
    }
}

/// <summary>Writes a <see cref="TimeSpan"/> as seconds, and reads either seconds or the constant format.</summary>
public sealed class TimeSpanSecondsConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? TimeSpan.FromSeconds(reader.GetDouble())
            : TimeSpan.TryParse(reader.GetString(), CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : TimeSpan.Zero;

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(Math.Round(value.TotalSeconds, 3));
    }
}

/// <summary>
/// RFC 7807 responses.
///
/// The contract with the caller is that <c>detail</c> always says what to do about it. A
/// <see cref="TrackerException"/> already carries exactly that in its Remedy, which is the
/// whole reason the shared model has the property, so mapping one to the other is the point
/// of this class. The message goes in <c>title</c> and the remedy is repeated in a
/// <c>remedy</c> extension so a UI can style it separately from the failure itself.
/// </summary>
public static class ApiProblems
{
    public const string RemedyExtension = "remedy";

    /// <summary>
    /// The RFC 9110 section that defines each status. That is what `type` is for -- a
    /// pointer to what the status means, not a link to the RFC that defines the envelope.
    /// </summary>
    private static readonly Dictionary<int, string> StatusTypes = new()
    {
        [StatusCodes.Status400BadRequest] = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        [StatusCodes.Status404NotFound] = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        [StatusCodes.Status500InternalServerError] = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        [StatusCodes.Status502BadGateway] = "https://tools.ietf.org/html/rfc9110#section-15.6.3",
    };

    public static ProblemDetails From(Exception? error, string instance)
    {
        if (error is TrackerException tracker)
        {
            return Build(
                StatusCodes.Status502BadGateway,
                tracker.Message,
                tracker.Remedy ?? "No specific remedy is known for this failure.",
                instance);
        }

        if (error is OperationCanceledException)
        {
            return Build(
                StatusCodes.Status499ClientClosedRequest,
                "The request was cancelled.",
                "The client went away before the answer was ready. Nothing to fix.",
                instance);
        }

        return Build(
            StatusCodes.Status500InternalServerError,
            "The tracker hit an unexpected error.",
            "This is a bug rather than a configuration problem. The server log has the stack trace.",
            instance);
    }

    public static IResult BadRequest(string title, string remedy) =>
        Results.Problem(Build(StatusCodes.Status400BadRequest, title, remedy, instance: null));

    public static IResult NotFound(string title, string remedy) =>
        Results.Problem(Build(StatusCodes.Status404NotFound, title, remedy, instance: null));

    private static ProblemDetails Build(int status, string title, string remedy, string? instance)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = remedy,
            Instance = instance,
            Type = StatusTypes.GetValueOrDefault(status),
        };
        problem.Extensions[RemedyExtension] = remedy;
        return problem;
    }
}

/// <summary>
/// Finds the dashboard directory another agent is building, without requiring it to exist.
/// </summary>
public static class StaticAssets
{
    public static string? Locate(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured))
        {
            return Directory.Exists(configured) ? configured : null;
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

/// <summary>
/// Reports whether anything that looks like Xbox Live configuration is present, without
/// ever reading a secret's value.
///
/// This exists to close a specific gap: the tracker silently serving fixtures to somebody
/// who has gone to the trouble of configuring credentials, and who then spends an afternoon
/// wondering why their K/D is not theirs. If configuration is present but no
/// <see cref="IXboxAuth"/> is registered, say so in plain words at /api/health.
/// </summary>
public static class CredentialHints
{
    private static readonly string[] Keys =
    [
        "Xbox:ClientId",
        "Xbox:ClientSecret",
        "Xbox:RefreshToken",
        "XBOX_CLIENT_ID",
        "XBOX_CLIENT_SECRET",
        "XBOX_REFRESH_TOKEN",
    ];

    public static object Detect(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Only ever the names, never the values. Nothing in this method can leak a secret
        // into a log or an HTTP response.
        var present = Keys.Where(k => !string.IsNullOrWhiteSpace(configuration[k])).ToArray();

        return new
        {
            configuredKeys = present,
            note = present.Length == 0
                ? "No Xbox Live configuration found, so the tracker is serving fixtures. That is the supported zero-credential mode, not a failure."
                : "Xbox Live configuration is present. It will only be used once an IXboxAuth implementation is registered in Program.cs -- see the comment there. Until then the tracker still serves fixtures.",
        };
    }
}
