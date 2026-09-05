using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eet.Xbox;

/// <summary>
/// Serializer settings shared by every request and response in this assembly.
///
/// Xbox Live and Halo disagree about casing -- <c>user.auth.xboxlive.com</c> wants
/// <c>PascalCase</c> property names and <c>achievements.xboxlive.com</c> answers in
/// <c>camelCase</c> -- so the write settings are exact-case (the wire records are named
/// exactly as the services expect) and the read settings are case-insensitive.
/// </summary>
internal static class XboxJson
{
    /// <summary>For parsing responses: tolerant of casing, ignores fields we do not model.</summary>
    internal static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// For building requests: property names go out exactly as declared. The Xbox
    /// endpoints are case-sensitive about <c>RelyingParty</c> and <c>RpsTicket</c>, and a
    /// camelCasing policy here produces a 400 that reads like a 401.
    /// </summary>
    internal static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Parse a timestamp from any of the several shapes these services use, always as UTC.
    /// Xbox sends <c>2024-08-29T17:01:50.0110000Z</c>; the Halo settings service sometimes
    /// omits the <c>Z</c>; and a never-unlocked achievement carries
    /// <c>0001-01-01T00:00:00.0000000</c>, which is a null wearing a costume.
    /// </summary>
    internal static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return null;
        }

        // DateTime.MinValue with a UTC offset is the "never happened" sentinel.
        return parsed.UtcDateTime <= DateTime.MinValue.AddDays(1) ? null : parsed;
    }

    /// <summary>Parse a number that arrived as a string, invariantly. Never throws.</summary>
    internal static double ParseNumber(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}
