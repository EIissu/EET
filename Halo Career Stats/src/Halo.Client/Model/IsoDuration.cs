using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Eet.Halo.Client.Model;

/// <summary>
/// Halo's services return every duration as an ISO-8601 period: <c>PT10M32.5S</c> for a
/// match, <c>P10DT4H30M</c> for a lifetime.
///
/// This matters more than it looks. <see cref="TimeSpan.Parse(string, IFormatProvider)"/>
/// does not read that format at all -- it wants <c>10:32.5</c> -- so a naive
/// <c>[JsonConverter]</c>-free deserialise throws on the very first match. Going the other
/// way, <see cref="XmlConvert.ToTimeSpan"/> does read it, which is why this leans on
/// System.Xml rather than hand-rolling a parser.
/// </summary>
public static class IsoDuration
{
    public static TimeSpan? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            // Some fields come back as a plain HH:MM:SS instead. Take that too rather than
            // losing a whole match over a formatting inconsistency we did not cause.
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var fallback) ? fallback : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static string ToIso(TimeSpan value) => XmlConvert.ToString(value);
}

/// <summary>Reads <see cref="TimeSpan"/> from an ISO-8601 period string.</summary>
public sealed class IsoDurationConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IsoDuration.TryParse(reader.GetString()) ?? TimeSpan.Zero;

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(IsoDuration.ToIso(value));
    }
}

/// <summary>Same, for the many duration fields that are legitimately absent.</summary>
public sealed class NullableIsoDurationConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : IsoDuration.TryParse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(IsoDuration.ToIso(value.Value));
        }
    }
}
