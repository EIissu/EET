package eet;

import java.nio.charset.StandardCharsets;

/**
 * The {@code i32} semantics of spec section 7.
 *
 * <p>The Python reference needs a function per operator because Python integers are
 * arbitrary-precision and its {@code //} and {@code %} floor. Java needs no correction
 * code at all: {@code int} <em>is</em> the spec's value type, 32-bit two's complement
 * (JLS 4.2.1). That is worth stating precisely rather than assuming, so here is the whole
 * of section 7 mapped onto the clauses that guarantee it. Every claim below was checked
 * against a live JDK, not just against the language specification.
 *
 * <ul>
 *   <li><b>7.1, wrapping arithmetic.</b> {@code +}, {@code -}, {@code *} and unary
 *       {@code -} on {@code int} discard everything above the low 32 bits instead of
 *       overflowing (JLS 15.18.2, 15.17.1, 15.15.4). So {@code MAX_VALUE + 1} is
 *       {@code MIN_VALUE}, {@code 65536 * 65536} is {@code 0} and {@code -MIN_VALUE} is
 *       {@code MIN_VALUE} -- the spec's {@code wrap()} is the hardware's behaviour here.</li>
 *   <li><b>7.2, division and remainder.</b> See {@link #div} and {@link #mod}.</li>
 *   <li><b>7.3, shifts.</b> For an {@code int} left operand only the low five bits of the
 *       right operand are used as the distance (JLS 15.19), which is exactly the spec's
 *       {@code b AND 31}; {@code >>} propagates the sign and {@code >>>} zero-fills. No
 *       masking of our own is needed, and none is done.</li>
 *   <li><b>4.3, comparison.</b> The relational operators on {@code int} are signed.</li>
 *   <li><b>4.2, bitwise.</b> {@code &}, {@code |}, {@code ^} and {@code ~} are bit-for-bit
 *       operations on the two's-complement representation.</li>
 *   <li><b>7.4, decimal formatting.</b> See {@link #decimal}.</li>
 * </ul>
 *
 * <p>What is left is this class: the two operators whose Java behaviour a reader would
 * reasonably doubt, and the one operation that is genuinely code.
 */
final class Ops {

    private Ops() {
    }

    /**
     * Truncating division (spec section 7.2).
     *
     * <p>Java's {@code /} rounds toward zero, so {@code -7 / 2} is {@code -3}, not the
     * {@code -4} that Python's {@code //} would give. JLS 15.17.2 also pins the overflowing
     * case the spec calls out: {@code MIN_VALUE / -1} is {@code MIN_VALUE}, quietly wrapped,
     * where a C++ runtime would take a hardware fault.
     *
     * <p>The caller must have rejected a zero divisor as trap {@code T04} first: unlike the
     * overflow case, {@code b == 0} really does throw {@link ArithmeticException}.
     */
    static int div(int a, int b) {
        return a / b;
    }

    /**
     * Remainder whose sign follows the dividend (spec section 7.2).
     *
     * <p>JLS 15.17.3 defines {@code %} so that {@code (a / b) * b + (a % b) == a} holds for
     * every non-zero divisor, which is the identity the spec requires; the sign of the
     * dividend follows from it, and so does {@code MIN_VALUE % -1 == 0}. As with
     * {@link #div}, a zero divisor is the caller's job to reject.
     */
    static int mod(int a, int b) {
        return a % b;
    }

    /**
     * The exact bytes {@code print} writes for {@code v} (spec section 7.4).
     *
     * <p>{@link Integer#toString(int)} already produces the shortest signed decimal form:
     * a leading {@code -} only when negative, no padding, no leading zeros, and {@code 0}
     * for zero. It also renders {@code MIN_VALUE} as {@code -2147483648} without ever
     * negating it, which is the step where a hand-rolled formatter overflows.
     *
     * <p>The result is ASCII by construction, so the encoding cannot lose a byte.
     */
    static byte[] decimal(int v) {
        return Integer.toString(v).getBytes(StandardCharsets.US_ASCII);
    }
}
