#include "eet/module.hpp"

#include <cstddef>
#include <cstring>
#include <string>

#include "eet/format.hpp"
#include "eet/isa.hpp"

namespace eet {
namespace {

// Every multi-byte field is little-endian (spec section 2). The bytes are assembled by
// hand rather than memcpy'd into an integer so the loader reads the same on a big-endian
// host, and so no alignment is assumed of the image buffer.

[[nodiscard]] std::uint16_t readU16(std::span<const std::uint8_t> image,
                                    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(static_cast<unsigned>(image[offset]) |
                                      (static_cast<unsigned>(image[offset + 1]) << 8));
}

[[nodiscard]] std::uint32_t readU32(std::span<const std::uint8_t> image,
                                    std::size_t offset) noexcept {
    return static_cast<std::uint32_t>(image[offset]) |
           (static_cast<std::uint32_t>(image[offset + 1]) << 8) |
           (static_cast<std::uint32_t>(image[offset + 2]) << 16) |
           (static_cast<std::uint32_t>(image[offset + 3]) << 24);
}

[[nodiscard]] std::vector<std::uint8_t> copySection(std::span<const std::uint8_t> image,
                                                    std::size_t offset, std::size_t length) {
    const auto section = image.subspan(offset, length);
    return {section.begin(), section.end()};
}

}  // namespace

Module loadModule(std::span<const std::uint8_t> image) {
    // The checks run in the order the fields appear, so the diagnostic always names the
    // first thing that is wrong (spec section 2).
    if (image.size() < kMinFileSize) {
        throw LoadError("file too short");
    }
    if (std::memcmp(image.data(), kMagic.data(), kMagic.size()) != 0) {
        throw LoadError("bad magic");
    }

    const std::uint16_t version = readU16(image, 4);
    const std::uint16_t flags = readU16(image, 6);
    const std::uint16_t nglobals = readU16(image, 8);
    const std::uint16_t entryLocals = readU16(image, 10);
    const std::uint32_t entry = readU32(image, 12);
    const std::uint32_t codeLen = readU32(image, 16);

    if (version != kVersion) {
        DecimalBuffer scratch;
        std::string reason = "unsupported version ";
        reason += formatDecimal(version, scratch);
        throw LoadError(reason);
    }
    if (flags != 0) {
        // Reserved for v2 feature bits; a v1 loader must refuse anything it cannot honour.
        std::string reason = "unsupported flags 0x";
        appendHexUpper(reason, flags, 4);
        throw LoadError(reason);
    }
    if (static_cast<std::size_t>(entryLocals) > kMaxLocals) {
        throw LoadError("entry_locals out of range");
    }

    // Both section ends are computed in std::size_t, which is wider than the u32 lengths
    // on every supported target, so neither addition can wrap past the size check.
    const std::size_t codeEnd = kHeaderSize + static_cast<std::size_t>(codeLen);
    if (codeEnd > image.size()) {
        throw LoadError("code section runs past end of file");
    }
    if (entry >= codeLen) {
        throw LoadError("entry past end of code");
    }
    if (image.size() - codeEnd < 4) {
        throw LoadError("missing data length");
    }

    const std::uint32_t dataLen = readU32(image, codeEnd);
    const std::size_t dataStart = codeEnd + 4;
    const std::size_t dataEnd = dataStart + static_cast<std::size_t>(dataLen);
    if (dataEnd > image.size()) {
        throw LoadError("data section runs past end of file");
    }
    if (dataEnd != image.size()) {
        throw LoadError("trailing bytes after data section");
    }

    Module module;
    module.nglobals = nglobals;
    module.entryLocals = entryLocals;
    module.entry = entry;
    module.code = copySection(image, kHeaderSize, codeLen);
    module.data = copySection(image, dataStart, dataLen);
    return module;
}

}  // namespace eet
