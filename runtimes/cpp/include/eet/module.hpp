// The .eetb container (spec section 2).

#ifndef EET_MODULE_HPP
#define EET_MODULE_HPP

#include <cstdint>
#include <span>
#include <stdexcept>
#include <vector>

namespace eet {

/// The bytes are not a valid EET binary. This is a load error, never a trap: it exits 65
/// with `eet: bad binary: <reason>` and the program never starts.
class LoadError : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

/// A loaded program: immutable code and data, plus the entry conditions.
struct Module {
    std::uint16_t nglobals = 0;
    std::uint16_t entryLocals = 0;
    std::uint32_t entry = 0;
    std::vector<std::uint8_t> code;
    std::vector<std::uint8_t> data;
};

/// Parses and validates a container image. Throws LoadError, naming the first fault.
[[nodiscard]] Module loadModule(std::span<const std::uint8_t> image);

}  // namespace eet

#endif  // EET_MODULE_HPP
