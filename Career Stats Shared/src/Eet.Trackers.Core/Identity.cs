using System.Globalization;
using System.Text;

namespace Eet.Trackers.Core;

/// <summary>
/// Working with display names that are not what they look like.
///
/// Xbox gamertags allow non-Latin letters, and plenty of people use them deliberately: a
/// tag that renders as "Elissu" may actually begin with CYRILLIC CAPITAL LETTER BYELORUSSIAN-
/// UKRAINIAN I (U+0406), which is a different character from LATIN CAPITAL LETTER I (U+0049)
/// and cannot be produced by typing on a normal keyboard.
///
/// The practical consequence for a stat tracker is blunt: search that tag by typing it and
/// the API returns nothing, forever, with no useful error. Most trackers simply fail here.
/// This class exists so ours does not -- it detects the case, explains it, and steers the
/// caller to look up by XUID instead, which is stable and unambiguous.
/// </summary>
public static class Identity
{
    /// <summary>
    /// Characters that render close enough to an ASCII letter to be mistaken for one.
    /// Restricted to the Cyrillic and Greek blocks, which is where essentially all
    /// real-world gamertag homoglyphs come from.
    /// </summary>
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cyrillic capitals
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['З'] = '3',
        ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
        ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['У'] = 'Y',
        ['Х'] = 'X', ['І'] = 'I', ['Ј'] = 'J', ['Ѕ'] = 'S',
        // Cyrillic lowercase
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i',
        ['ј'] = 'j', ['ѕ'] = 's', ['м'] = 'm', ['н'] = 'h',
        // Greek capitals
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z',
        ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M',
        ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T',
        ['Υ'] = 'Y', ['Χ'] = 'X',
        // Greek lowercase
        ['ο'] = 'o', ['α'] = 'a', ['ε'] = 'e', ['ι'] = 'i',
        ['ν'] = 'v', ['ρ'] = 'p', ['τ'] = 't', ['χ'] = 'x',
    };

    /// <summary>
    /// True when the name contains at least one character that reads as ASCII but is not.
    /// </summary>
    public static bool LooksLikeHomoglyph(string name) =>
        !string.IsNullOrEmpty(name) && name.Any(Confusables.ContainsKey);

    /// <summary>
    /// The name with every confusable folded to the ASCII letter it imitates. Useful for
    /// matching a user's typed guess against a tag they cannot actually type.
    /// </summary>
    public static string ToAsciiSkeleton(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKC))
        {
            builder.Append(Confusables.TryGetValue(ch, out var ascii) ? ascii : ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Do two names look identical to a human, even if they are different strings?
    /// </summary>
    public static bool LooksTheSame(string a, string b) =>
        string.Equals(ToAsciiSkeleton(a), ToAsciiSkeleton(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A diagnostic a person can act on, naming the offending code points. Returns null
    /// when the name is plain ASCII and there is nothing to explain.
    /// </summary>
    public static string? Explain(string name)
    {
        if (!LooksLikeHomoglyph(name))
        {
            return null;
        }

        var offenders = name
            .Select((ch, index) => (ch, index))
            .Where(t => Confusables.ContainsKey(t.ch))
            .Select(t => string.Create(
                CultureInfo.InvariantCulture,
                $"'{t.ch}' at position {t.index} is U+{(int)t.ch:X4}, not '{Confusables[t.ch]}' (U+{(int)Confusables[t.ch]:X4})"))
            .ToList();

        return
            $"\"{name}\" renders like \"{ToAsciiSkeleton(name)}\" but is not the same text: " +
            string.Join("; ", offenders) +
            ". Searching for the typed version will not find this player -- look them up by XUID instead.";
    }

    /// <summary>
    /// The <c>xuid(...)</c> wrapper the Halo services expect where a player is named.
    /// </summary>
    public static string XuidRef(string xuid) =>
        xuid.StartsWith("xuid(", StringComparison.OrdinalIgnoreCase) ? xuid : $"xuid({xuid})";

    /// <summary>Pull the bare id back out of an <c>xuid(...)</c> wrapper.</summary>
    public static string BareXuid(string reference) =>
        reference.StartsWith("xuid(", StringComparison.OrdinalIgnoreCase) && reference.EndsWith(')')
            ? reference[5..^1]
            : reference;

    /// <summary>
    /// Split a Bungie name such as <c>Guardian#1234</c> into its display name and code.
    /// Bungie's search endpoint takes the two separately and matches nothing if you send
    /// the combined string.
    /// </summary>
    public static bool TryParseBungieName(string input, out string displayName, out short code)
    {
        displayName = string.Empty;
        code = 0;

        var hash = input.LastIndexOf('#');
        if (hash <= 0 || hash == input.Length - 1)
        {
            return false;
        }

        displayName = input[..hash].Trim();
        return short.TryParse(
            input[(hash + 1)..].Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out code) && displayName.Length > 0;
    }
}
