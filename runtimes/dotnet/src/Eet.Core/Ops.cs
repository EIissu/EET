using System.Runtime.CompilerServices;

namespace Eet.Core;

/// <summary>
/// The exact <c>i32</c> semantics of specification section 7.
/// </summary>
/// <remarks>
/// <para>
/// C#'s <see cref="int"/> is already a 32-bit two's-complement integer, so most of section 7
/// is free. Three places where it is not, and where a naive translation throws instead of
/// producing a value:
/// </para>
/// <list type="bullet">
///   <item><c>int.MinValue / -1</c> raises <see cref="OverflowException"/>.</item>
///   <item><c>int.MinValue % -1</c> raises it too, on x64, for the same hardware reason.</item>
///   <item><c>Math.Abs(int.MinValue)</c> raises it, which rules out the divide-magnitudes
///   formulation the Python reference uses.</item>
/// </list>
/// <para>
/// Every operation below is written <c>unchecked</c> even where the enclosing project
/// already compiles that way, so the semantics survive someone turning
/// <c>CheckForOverflowUnderflow</c> on.
/// </para>
/// </remarks>
public static class Ops
{
    /// <summary>
    /// Bytes needed by the longest output <see cref="FormatDecimal"/> can produce, which is
    /// <c>-2147483648</c>.
    /// </summary>
    public const int MaxDecimalLength = 11;

    /// <summary>Wrapping addition.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Add(int a, int b) => unchecked(a + b);

    /// <summary>Wrapping subtraction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sub(int a, int b) => unchecked(a - b);

    /// <summary>Wrapping multiplication.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mul(int a, int b) => unchecked(a * b);

    /// <summary>Wrapping negation; <c>Neg(int.MinValue)</c> is <c>int.MinValue</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Neg(int a) => unchecked(-a);

    /// <summary>
    /// Division truncating toward zero (section 7.2). The caller must already have rejected
    /// <paramref name="b"/> == 0 as <see cref="TrapId.T04"/>.
    /// </summary>
    /// <remarks>
    /// The <c>-1</c> arm exists solely for <c>int.MinValue / -1</c>, which the spec pins to
    /// <c>int.MinValue</c> but which C# turns into an <see cref="OverflowException"/> (the
    /// x64 <c>idiv</c> instruction faults). For every other dividend <c>a / -1</c> and
    /// <c>-a</c> agree, so the special case costs one comparison and no correctness.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Div(int a, int b) => b == -1 ? unchecked(-a) : a / b;

    /// <summary>
    /// Remainder whose sign follows the dividend (section 7.2). The caller must already have
    /// rejected <paramref name="b"/> == 0 as <see cref="TrapId.T04"/>.
    /// </summary>
    /// <remarks>
    /// <c>x % -1</c> is mathematically zero for every <c>x</c>, so returning 0 directly is
    /// both correct and the fix for <c>int.MinValue % -1</c>, which otherwise throws.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mod(int a, int b) => b == -1 ? 0 : a % b;

    /// <summary>Bitwise AND.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int And(int a, int b) => a & b;

    /// <summary>Bitwise OR.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Or(int a, int b) => a | b;

    /// <summary>Bitwise XOR.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Xor(int a, int b) => a ^ b;

    /// <summary>Bitwise complement.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Not(int a) => ~a;

    /// <summary>Left shift by <c>b AND 31</c>.</summary>
    /// <remarks>
    /// C# masks shift counts for <see cref="int"/> already, but the mask is written out
    /// because section 7.3 makes it part of the semantics rather than a host detail.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Shl(int a, int b) => unchecked(a << (b & 31));

    /// <summary>Arithmetic (sign-propagating) right shift by <c>b AND 31</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Shr(int a, int b) => a >> (b & 31);

    /// <summary>Logical (zero-filling) right shift by <c>b AND 31</c>.</summary>
    /// <remarks>
    /// Shifting through <see cref="uint"/> is what makes the fill bit zero; <c>&gt;&gt;</c>
    /// on a signed <see cref="int"/> would copy the sign. <c>Ushr(-1, 0)</c> is <c>-1</c>,
    /// because the mask leaves nothing to fill.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ushr(int a, int b) => unchecked((int)((uint)a >> (b & 31)));

    /// <summary>
    /// Renders <paramref name="value"/> as the ASCII bytes <c>print</c> emits (section 7.4):
    /// a <c>-</c> if and only if the value is negative, then the magnitude's digits with no
    /// leading zeros, and a bare <c>0</c> for zero.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <param name="destination">
    /// A buffer of at least <see cref="MaxDecimalLength"/> bytes. The digits are written
    /// right-aligned into it, which is why the result is returned as a slice.
    /// </param>
    /// <returns>The portion of <paramref name="destination"/> holding the text.</returns>
    /// <remarks>
    /// Digits are accumulated with the value held <em>negative</em>. The obvious
    /// alternative - negate, then divide - cannot render <c>int.MinValue</c>, whose
    /// magnitude has no representation in <see cref="int"/>. The negative half of the range
    /// is the larger one, so working there is total.
    /// </remarks>
    public static ReadOnlySpan<byte> FormatDecimal(int value, Span<byte> destination)
    {
        int index = destination.Length;
        int remaining = value > 0 ? unchecked(-value) : value;

        do
        {
            // remaining is <= 0, so remaining % 10 is <= 0 and subtracting it adds a digit.
            destination[--index] = (byte)('0' - remaining % 10);
            remaining /= 10;
        }
        while (remaining != 0);

        if (value < 0)
        {
            destination[--index] = (byte)'-';
        }

        return destination[index..];
    }
}
