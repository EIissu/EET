using Eet.Core;
using Xunit;

namespace Eet.Tests;

/// <summary>
/// Specification section 7, case by case.
/// </summary>
/// <remarks>
/// Every test here exists because a plausible C# translation of the spec gets it wrong:
/// by throwing (the <c>int.MinValue</c> cases), by saturating, by flooring instead of
/// truncating, or by propagating a sign bit into a shift that should zero-fill.
/// </remarks>
public class OpsTests
{
    [Theory]
    [InlineData(int.MaxValue, 1, int.MinValue)]
    [InlineData(int.MinValue, -1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, -2)]
    public void AddWrapsInsteadOfOverflowing(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Add(a, b));

    [Theory]
    [InlineData(int.MinValue, 1, int.MaxValue)]
    [InlineData(int.MaxValue, -1, int.MinValue)]
    public void SubWrapsInsteadOfOverflowing(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Sub(a, b));

    [Fact]
    public void AddingTheTwoSmallestValuesGivesZero()
        => Assert.Equal(0, Ops.Add(int.MinValue, int.MinValue));

    [Theory]
    [InlineData(65536, 65536, 0)]
    [InlineData(123456789, 987654321, -67153019)]
    [InlineData(int.MinValue, -1, int.MinValue)]
    [InlineData(int.MinValue, 2, 0)]
    public void MulKeepsOnlyTheLowThirtyTwoBits(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Mul(a, b));

    [Fact]
    public void NegOfTheSmallestValueIsItself()
        => Assert.Equal(int.MinValue, Ops.Neg(int.MinValue));

    [Theory]
    [InlineData(7, 2, 3)]
    [InlineData(-7, 2, -3)]
    [InlineData(7, -2, -3)]
    [InlineData(-7, -2, 3)]
    [InlineData(1, 2, 0)]
    [InlineData(-1, 2, 0)]
    public void DivTruncatesTowardZeroRatherThanFlooring(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Div(a, b));

    [Theory]
    [InlineData(7, 2, 1)]
    [InlineData(-7, 2, -1)]
    [InlineData(7, -2, 1)]
    [InlineData(-7, -2, -1)]
    public void ModTakesTheSignOfTheDividend(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Mod(a, b));

    [Theory]
    [InlineData(7, 2)]
    [InlineData(-7, 2)]
    [InlineData(7, -2)]
    [InlineData(-7, -2)]
    [InlineData(int.MaxValue, 3)]
    [InlineData(int.MinValue, 3)]
    [InlineData(int.MinValue, -3)]
    [InlineData(0, -5)]
    public void DivAndModSatisfyTheDivisionIdentity(int a, int b)
        => Assert.Equal(a, Ops.Add(Ops.Mul(Ops.Div(a, b), b), Ops.Mod(a, b)));

    [Fact]
    public void SmallestValueOverMinusOneWrapsRatherThanThrowing()
    {
        // Written as a raw `int.MinValue / -1` this line raises OverflowException, which
        // would surface as a stack trace and exit 1 instead of the spec's wrapped result.
        Assert.Equal(int.MinValue, Ops.Div(int.MinValue, -1));
    }

    [Fact]
    public void SmallestValueModuloMinusOneIsZeroRatherThanThrowing()
        => Assert.Equal(0, Ops.Mod(int.MinValue, -1));

    [Fact]
    public void TheDivisionIdentityStillHoldsAtTheOverflowCase()
        => Assert.Equal(
            int.MinValue,
            Ops.Add(Ops.Mul(Ops.Div(int.MinValue, -1), -1), Ops.Mod(int.MinValue, -1)));

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 31, int.MinValue)]
    [InlineData(1, 32, 1)]
    [InlineData(1, 33, 2)]
    [InlineData(1, -1, int.MinValue)]
    [InlineData(1, 65, 2)]
    public void ShlMasksTheShiftCountToFiveBits(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Shl(a, b));

    [Theory]
    [InlineData(-16, 2, -4)]
    [InlineData(-1, 31, -1)]
    [InlineData(int.MinValue, 31, -1)]
    [InlineData(-1, 32, -1)]
    [InlineData(int.MaxValue, 30, 1)]
    public void ShrPropagatesTheSignBit(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Shr(a, b));

    [Theory]
    [InlineData(-1, 0, -1)]
    [InlineData(-1, 1, int.MaxValue)]
    [InlineData(-1, 31, 1)]
    [InlineData(-1, 32, -1)]
    [InlineData(int.MinValue, 31, 1)]
    [InlineData(-16, 2, 1073741820)]
    public void UshrFillsWithZeroRatherThanTheSignBit(int a, int b, int expected)
        => Assert.Equal(expected, Ops.Ushr(a, b));

    [Fact]
    public void ShrAndUshrDifferExactlyWhenTheValueIsNegative()
    {
        Assert.Equal(Ops.Shr(16, 2), Ops.Ushr(16, 2));
        Assert.NotEqual(Ops.Shr(-16, 2), Ops.Ushr(-16, 2));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(-1, "-1")]
    [InlineData(10, "10")]
    [InlineData(-100, "-100")]
    [InlineData(int.MaxValue, "2147483647")]
    [InlineData(int.MinValue, "-2147483648")]
    public void FormatDecimalMatchesTheShortestSignedRendering(int value, string expected)
        => Assert.Equal(expected, Format(value));

    [Fact]
    public void FormatDecimalNeverPadsOrSignsAPositiveValue()
    {
        Assert.Equal("7", Format(7));
        Assert.Equal("1000000", Format(1000000));
    }

    private static string Format(int value)
    {
        byte[] buffer = new byte[Ops.MaxDecimalLength];
        ReadOnlySpan<byte> text = Ops.FormatDecimal(value, buffer);
        return System.Text.Encoding.ASCII.GetString(text);
    }
}
