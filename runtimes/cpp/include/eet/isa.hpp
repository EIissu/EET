// The EET instruction set and machine limits, as declared by spec/eet-vm.md sections 1-4.
//
// Nothing here executes: `ops.hpp` holds the value semantics and `vm.cpp` the interpreter.
// Keeping the numbers in one place means a spec revision touches exactly one header.

#ifndef EET_ISA_HPP
#define EET_ISA_HPP

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace eet {

// --- Container format (spec section 2) -----------------------------------------------

inline constexpr std::string_view kMagic = "EETB";
inline constexpr std::uint16_t kVersion = 1;
inline constexpr std::size_t kHeaderSize = 20;   // magic..code_len; data_len follows code
inline constexpr std::size_t kMinFileSize = 24;

// --- Machine limits (spec section 1) --------------------------------------------------

inline constexpr std::size_t kMaxOperandStack = 1024;
inline constexpr std::size_t kMaxCallDepth = 256;
inline constexpr std::size_t kMaxLocals = 256;

// --- Exit statuses (spec section 5.3) -------------------------------------------------
//
// These are the sysexits.h values the spec borrows: EX_USAGE, EX_DATAERR and EX_SOFTWARE.
// The spec pins 0, 65 and 70; a malformed command line is outside its scope, so it gets
// the conventional EX_USAGE rather than one of the statuses a program can produce.

inline constexpr int kExitOk = 0;
inline constexpr int kExitUsage = 64;
inline constexpr int kExitLoadError = 65;
inline constexpr int kExitTrap = 70;

/// Every opcode in v1, in the order of the specification's tables (section 4).
enum class Op : std::uint8_t {
    // 4.1 stack and control
    Halt = 0x00,
    Nop = 0x01,
    Push = 0x02,
    Pop = 0x03,
    Dup = 0x04,
    Swap = 0x05,
    Over = 0x06,
    Rot = 0x07,
    // 4.2 arithmetic and bitwise
    Add = 0x10,
    Sub = 0x11,
    Mul = 0x12,
    Div = 0x13,
    Mod = 0x14,
    Neg = 0x15,
    And = 0x16,
    Or = 0x17,
    Xor = 0x18,
    Not = 0x19,
    Shl = 0x1A,
    Shr = 0x1B,
    Ushr = 0x1C,
    // 4.3 comparison
    Eq = 0x20,
    Ne = 0x21,
    Lt = 0x22,
    Le = 0x23,
    Gt = 0x24,
    Ge = 0x25,
    // 4.4 branching and calls
    Jmp = 0x30,
    Jz = 0x31,
    Jnz = 0x32,
    Call = 0x33,
    Ret = 0x34,
    // 4.5 memory
    Load = 0x40,
    Store = 0x41,
    Gload = 0x42,
    Gstore = 0x43,
    Dload = 0x44,
    Gloadx = 0x45,
    Gstorex = 0x46,
    // 4.6 output
    Print = 0x50,
    Printc = 0x51,
    Prints = 0x52,
    // 4.7 diagnostics
    Trap = 0x60,
};

}  // namespace eet

#endif  // EET_ISA_HPP
