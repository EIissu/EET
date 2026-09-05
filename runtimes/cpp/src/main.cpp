// eet -- the C++20 runtime for the EET virtual machine.
//
// Usage: eet run <program.eetb>
//
// The command line is deliberately tiny: this binary is one of four interchangeable
// implementations of spec/eet-vm.md, and the conformance harness invokes them all
// identically.

#include <cstdint>
#include <cstdio>
#include <exception>
#include <fstream>
#include <ios>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include "eet/isa.hpp"
#include "eet/module.hpp"
#include "eet/sink.hpp"
#include "eet/vm.hpp"

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

namespace {

void configureBinaryStreams() {
#ifdef _WIN32
    // Windows opens the standard handles in text mode, where the C runtime rewrites every
    // \n into \r\n on the way out. Spec section 4.6 forbids any newline translation -- the
    // output is a byte stream -- so both handles are switched to binary before a single
    // byte is written. Without this every multi-line program differs from the goldens by
    // one byte per line, and the trap line of section 6 ends in CRLF instead of LF.
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
#endif
}

/// Reads a file whole, in binary mode. Throws std::runtime_error if it cannot be read.
[[nodiscard]] std::vector<std::uint8_t> readFile(const std::string& path) {
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        throw std::runtime_error("cannot read " + path);
    }

    file.seekg(0, std::ios::end);
    const std::streamoff size = file.tellg();
    if (size < 0) {
        throw std::runtime_error("cannot read " + path);
    }
    file.seekg(0, std::ios::beg);

    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(size));
    if (size > 0) {
        file.read(reinterpret_cast<char*>(bytes.data()), size);
        if (!file) {
            throw std::runtime_error("cannot read " + path);
        }
    }
    return bytes;
}

void report(eet::ByteSink& err, std::string_view message) {
    err.write("eet: ");
    err.write(message);
    err.write("\n");
    err.flush();
}

}  // namespace

int main(int argc, char* argv[]) {
    configureBinaryStreams();

    eet::ByteSink out(stdout);
    eet::ByteSink err(stderr);

    try {
        if (argc != 3 || std::string_view(argv[1]) != "run") {
            report(err, "usage: eet run <program.eetb>");
            return eet::kExitUsage;
        }

        const std::vector<std::uint8_t> image = readFile(argv[2]);
        const eet::Module module = eet::loadModule(image);
        return eet::execute(module, out, err);
    } catch (const eet::LoadError& error) {
        // Section 2: a malformed container is a load error, not a trap. The program never
        // starts, so nothing has reached stdout.
        report(err, "bad binary: " + std::string(error.what()));
        return eet::kExitLoadError;
    } catch (const std::exception& error) {
        // Anything else that can go wrong before or during execution -- an unreadable
        // file, a failed allocation -- is still a refusal to run this module. No exception
        // may escape main: the process must exit with a status the spec defines, never
        // through a terminate handler and a stack trace.
        report(err, error.what());
        return eet::kExitLoadError;
    } catch (...) {
        report(err, "bad binary: unknown error");
        return eet::kExitLoadError;
    }
}
