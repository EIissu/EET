using Eet.Trackers.Core;
using Xunit;

namespace Eet.Trackers.Core.Tests;

public class IdentityTests
{
    /// <summary>
    /// The real case this code exists for. The gamertag reads "Elissu" but the third
    /// character is U+0406, CYRILLIC CAPITAL LETTER BYELORUSSIAN-UKRAINIAN I, standing in
    /// for a Latin capital I -- which itself is only there because a capital I looks like a
    /// lowercase L in most fonts. Two layers of disguise, and the practical result is a tag
    /// no keyboard can produce.
    /// </summary>
    private const string CyrillicTag = "EІissu";

    private const string LatinLookalike = "EIissu";

    [Fact]
    public void The_cyrillic_gamertag_is_detected()
    {
        Assert.True(Identity.LooksLikeHomoglyph(CyrillicTag));
        Assert.False(Identity.LooksLikeHomoglyph(LatinLookalike));
    }

    [Fact]
    public void The_two_spellings_are_different_strings_that_look_identical()
    {
        Assert.NotEqual(CyrillicTag, LatinLookalike);
        Assert.True(Identity.LooksTheSame(CyrillicTag, LatinLookalike));
        Assert.Equal(LatinLookalike, Identity.ToAsciiSkeleton(CyrillicTag));
    }

    [Fact]
    public void The_explanation_names_the_offending_code_point()
    {
        var explanation = Identity.Explain(CyrillicTag);

        Assert.NotNull(explanation);
        Assert.Contains("U+0406", explanation);
        Assert.Contains("XUID", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plain_name_needs_no_explanation()
    {
        Assert.Null(Identity.Explain("Master Chief"));
        Assert.Null(Identity.Explain(LatinLookalike));
    }

    [Theory]
    [InlineData("Оnyx", "Onyx")]        // Cyrillic О
    [InlineData("Ѕpartan", "Spartan")]  // Cyrillic Ѕ
    [InlineData("Аrbiter", "Arbiter")]  // Cyrillic А
    [InlineData("Νoble", "Noble")]      // Greek Ν
    public void Common_confusables_fold_to_their_ascii_lookalike(string disguised, string plain)
    {
        Assert.True(Identity.LooksLikeHomoglyph(disguised));
        Assert.Equal(plain, Identity.ToAsciiSkeleton(disguised));
        Assert.True(Identity.LooksTheSame(disguised, plain));
    }

    [Fact]
    public void Case_is_ignored_when_comparing_appearances()
    {
        Assert.True(Identity.LooksTheSame(CyrillicTag, "eiissu"));
    }

    // --- xuid references ------------------------------------------------------------------

    [Theory]
    [InlineData("2533274792115567", "xuid(2533274792115567)")]
    [InlineData("xuid(2533274792115567)", "xuid(2533274792115567)")]
    public void XuidRef_wraps_exactly_once(string input, string expected) =>
        Assert.Equal(expected, Identity.XuidRef(input));

    [Fact]
    public void BareXuid_unwraps_and_is_the_inverse_of_XuidRef()
    {
        Assert.Equal("2533274792115567", Identity.BareXuid("xuid(2533274792115567)"));
        Assert.Equal("2533274792115567", Identity.BareXuid("2533274792115567"));
        Assert.Equal("123", Identity.BareXuid(Identity.XuidRef("123")));
    }

    // --- bungie names ---------------------------------------------------------------------

    [Fact]
    public void A_bungie_name_splits_into_display_name_and_code()
    {
        Assert.True(Identity.TryParseBungieName("Guardian#1234", out var name, out var code));
        Assert.Equal("Guardian", name);
        Assert.Equal(1234, code);
    }

    [Fact]
    public void A_bungie_name_may_contain_spaces_and_its_own_hashes()
    {
        Assert.True(Identity.TryParseBungieName("The #1 Guardian#0007", out var name, out var code));
        Assert.Equal("The #1 Guardian", name);   // split on the LAST hash, not the first
        Assert.Equal(7, code);
    }

    [Theory]
    [InlineData("Guardian")]        // no code at all
    [InlineData("Guardian#")]       // trailing hash
    [InlineData("#1234")]           // no display name
    [InlineData("Guardian#abcd")]   // code is not a number
    [InlineData("")]
    public void Malformed_bungie_names_are_rejected_rather_than_guessed(string input) =>
        Assert.False(Identity.TryParseBungieName(input, out _, out _));

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        Assert.True(Identity.TryParseBungieName("  Guardian # 1234 ", out var name, out var code));
        Assert.Equal("Guardian", name);
        Assert.Equal(1234, code);
    }
}
