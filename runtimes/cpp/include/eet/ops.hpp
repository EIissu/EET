// Exact i32 semantics (spec section 7).
//
// Signed overflow is undefined behaviour in C++, and at -O2 the optimiser acts on that:
// it may assume `a + b` never wraps and delete the very checks a naive implementation
// would rely on. So every operation that can overflow is computed on uint32_t, where the
// standard defines arithmetic as modulo 2^32, and converted back. Since C++20 that
// conversion is guaranteed to reinterpret the bits as two's complement, which is exactly
// the spec's wrap(x) = ((x + 2^31) mod 2^32) - 2^31.

#ifndef EET_OPS_HPP
#define EET_OPS_HPP

#include <cstdint>
#include <limits>

namespace eet::ops {

inline constexpr std::int32_t kMin = std::numeric_limits<std::int32_t>::min();

/// The spec's wrap(): reinterpret 32 raw bits as a signed value.
[[nodiscard]] constexpr std::int32_t wrap(std::uint32_t value) noexcept {
    return static_cast<std::int32_t>(value);
}

/// The inverse: the same bits viewed as unsigned, where overflow is defined.
[[nodiscard]] constexpr std::uint32_t bits(std::int32_t value) noexcept {
    return static_cast<std::uint32_t>(value);
}

// --- 7.1 wrapping arithmetic ----------------------------------------------------------

[[nodiscard]] constexpr std::int32_t add(std::int32_t a, std::int32_t b) noexcept {
    return wrap(bits(a) + bits(b));
}

[[nodiscard]] constexpr std::int32_t sub(std::int32_t a, std::int32_t b) noexcept {
    return wrap(bits(a) - bits(b));
}

[[nodiscard]] constexpr std::int32_t mul(std::int32_t a, std::int32_t b) noexcept {
    // The product of two u32 values is computed modulo 2^32, which is the same bit
    // pattern an infinite-precision signed multiply would leave in the low 32 bits.
    return wrap(bits(a) * bits(b));
}

[[nodiscard]] constexpr std::int32_t neg(std::int32_t a) noexcept {
    // Negating in unsigned space is what makes neg(INT32_MIN) == INT32_MIN instead of UB.
    return wrap(0U - bits(a));
}

// --- 7.2 division and remainder -------------------------------------------------------

/// Truncating division. The caller must reject a zero divisor first (trap T04).
[[nodiscard]] constexpr std::int32_t div(std::int32_t a, std::int32_t b) noexcept {
    // INT32_MIN / -1 has no i32 result, and on x86 the idiv instruction raises SIGFPE and
    // kills the process rather than wrapping. Section 7.2 says the value wraps back to
    // INT32_MIN, so the case never reaches the hardware.
    if (a == kMin && b == -1) {
        return kMin;
    }
    // Every other pair is safe, and C++ has truncated toward zero since C++11 -- the same
    // rule section 7.2 states.
    return a / b;
}

/// Remainder whose sign follows the dividend. The caller must reject a zero divisor.
[[nodiscard]] constexpr std::int32_t mod(std::int32_t a, std::int32_t b) noexcept {
    // Same fault as div(), and the identity (a/b)*b + (a%b) == a forces the result to 0.
    if (a == kMin && b == -1) {
        return 0;
    }
    return a % b;
}

// --- 4.2 bitwise ----------------------------------------------------------------------

[[nodiscard]] constexpr std::int32_t bitAnd(std::int32_t a, std::int32_t b) noexcept {
    return wrap(bits(a) & bits(b));
}

[[nodiscard]] constexpr std::int32_t bitOr(std::int32_t a, std::int32_t b) noexcept {
    return wrap(bits(a) | bits(b));
}

[[nodiscard]] constexpr std::int32_t bitXor(std::int32_t a, std::int32_t b) noexcept {
    return wrap(bits(a) ^ bits(b));
}

[[nodiscard]] constexpr std::int32_t bitNot(std::int32_t a) noexcept {
    return wrap(~bits(a));
}

// --- 7.3 shifts -----------------------------------------------------------------------
//
// The count is masked to five bits by the spec, which conveniently also keeps every shift
// below the width of the type: shifting by 32 or more is undefined behaviour in C++.

[[nodiscard]] constexpr std::int32_t shl(std::int32_t a, std::int32_t b) noexcept {
    // Shifting a negative value left is UB even without overflow, hence the u32 detour.
    return wrap(bits(a) << (bits(b) & 31U));
}

[[nodiscard]] constexpr std::int32_t shr(std::int32_t a, std::int32_t b) noexcept {
    // Arithmetic, sign-propagating. C++20 defines >> on a negative signed value this way;
    // before C++20 it was implementation-defined, which is why the language standard this
    // runtime is compiled with is part of its correctness argument.
    return a >> (bits(b) & 31U);
}

[[nodiscard]] constexpr std::int32_t ushr(std::int32_t a, std::int32_t b) noexcept {
    // Logical, zero-filling: the shift happens on the unsigned view. With a count of zero
    // no fill occurs, so ushr(-1, 0) is -1.
    return wrap(bits(a) >> (bits(b) & 31U));
}

}  // namespace eet::ops

#endif  // EET_OPS_HPP
