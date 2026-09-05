// A raw byte sink (spec section 4.6).

#ifndef EET_SINK_HPP
#define EET_SINK_HPP

#include <cstdint>
#include <cstdio>
#include <span>
#include <string_view>
#include <vector>

namespace eet {

/// A buffered, byte-exact wrapper around a C stdio stream.
///
/// Output is a byte stream with no encoding and no newline translation, so nothing here
/// ever inspects what it is given. The buffer exists because `printc` writes a single
/// byte at a time and the programs in this repository emit tens of thousands of them;
/// going through std::fwrite for each one would pay a stream lock per character.
class ByteSink {
public:
    explicit ByteSink(std::FILE* file);

    ByteSink(const ByteSink&) = delete;
    ByteSink& operator=(const ByteSink&) = delete;

    /// Flushes what is left. Nothing may be lost on the way out, including after a trap.
    ~ByteSink();

    void write(std::span<const std::uint8_t> bytes);
    void write(std::string_view text);
    void writeByte(std::uint8_t value);

    void flush();

private:
    std::FILE* file_;
    std::vector<std::uint8_t> buffer_;
};

}  // namespace eet

#endif  // EET_SINK_HPP
