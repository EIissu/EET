#include "eet/sink.hpp"

#include <cstddef>

namespace eet {
namespace {

constexpr std::size_t kBufferSize = 64 * 1024;

}  // namespace

ByteSink::ByteSink(std::FILE* file) : file_(file) {
    // Reserved once; clear() keeps the capacity, so steady-state output never allocates.
    buffer_.reserve(kBufferSize);
}

ByteSink::~ByteSink() {
    flush();
}

void ByteSink::write(std::span<const std::uint8_t> bytes) {
    if (bytes.size() >= kBufferSize) {
        // Too big to be worth copying: drain what is queued and hand it straight over,
        // keeping the byte order of the stream intact.
        flush();
        std::fwrite(bytes.data(), 1, bytes.size(), file_);
        return;
    }
    if (buffer_.size() + bytes.size() > kBufferSize) {
        flush();
    }
    buffer_.insert(buffer_.end(), bytes.begin(), bytes.end());
}

void ByteSink::write(std::string_view text) {
    write(std::span(reinterpret_cast<const std::uint8_t*>(text.data()), text.size()));
}

void ByteSink::writeByte(std::uint8_t value) {
    if (buffer_.size() == kBufferSize) {
        flush();
    }
    buffer_.push_back(value);
}

void ByteSink::flush() {
    if (!buffer_.empty()) {
        std::fwrite(buffer_.data(), 1, buffer_.size(), file_);
        buffer_.clear();
    }
    std::fflush(file_);
}

}  // namespace eet
