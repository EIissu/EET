// The two textual forms the spec pins to the byte: decimal for `print` (section 7.4) and
// uppercase hex for the pc in a trap line (section 6).
//
// Both are built by hand rather than with iostreams or printf, because the spec's output
// is a byte stream with no locale: a runtime that inherited a grouping separator or a
// different digit set from the environment would stop matching the goldens.

#ifndef EET_FORMAT_HPP
#define EET_FORMAT_HPP

#include <array>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace eet {

/// Scratch space for one formatted i32. The longest is "-2147483648", eleven bytes.
using DecimalBuffer = std::array<char, 11>;

/// Renders `value` exactly as spec section 7.4 demands, into caller-owned storage.
///
/// std::to_chars is the only standard conversion that is locale-independent by
/// construction, and its output is already the required shortest form: a '-' if and only
/// if the value is negative, no leading zeros, a bare "0" for zero. It also formats
/// INT32_MIN without ever negating it, which is the trap the spec calls out.
[[nodiscard]] inline std::string_view formatDecimal(std::int32_t value,
                                                    DecimalBuffer& scratch) noexcept {
    const std::to_chars_result result =
        std::to_chars(scratch.data(), scratch.data() + scratch.size(), value);
    // Cannot fail: every i32 fits in eleven characters.
    return {scratch.data(), static_cast<std::size_t>(result.ptr - scratch.data())};
}

/// Appends `value` as exactly `digits` uppercase, zero-padded hex characters.
inline void appendHexUpper(std::string& out, std::uint32_t value, int digits) {
    constexpr std::string_view kDigits = "0123456789ABCDEF";
    for (int shift = (digits - 1) * 4; shift >= 0; shift -= 4) {
        out.push_back(kDigits[(value >> shift) & 0xFU]);
    }
}

}  // namespace eet

#endif  // EET_FORMAT_HPP
