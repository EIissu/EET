// Traps: the deterministic runtime faults of spec section 6.

#ifndef EET_TRAP_HPP
#define EET_TRAP_HPP

#include <cstdint>
#include <string>

namespace eet {

/// The ten trap identifiers. The enumerators are indices into the message table in
/// trap.cpp, so their order is the specification's order and must stay that way.
enum class TrapId : std::uint8_t {
    T01,  // stack underflow
    T02,  // stack overflow
    T03,  // call depth exceeded
    T04,  // division by zero
    T05,  // invalid opcode
    T06,  // local index out of range
    T07,  // global index out of range
    T08,  // data access out of range
    T09,  // jump out of range
    T10,  // trap instruction
};

/// A trap in flight.
///
/// This is thrown out of the interpreter loop and caught in exactly one place, so it is a
/// plain aggregate rather than a std::exception: it is control flow, not an error report,
/// and it must never be swallowed by a generic handler.
struct Trap {
    TrapId id;
    /// Address of the first byte of the trapping instruction (section 6).
    std::uint32_t pc;
    /// The operand of the `trap` instruction. Meaningful for T10 only.
    std::uint8_t userCode = 0;
};

/// The single line a trap writes to stderr, terminated by one LF and nothing else.
[[nodiscard]] std::string trapLine(const Trap& trap);

}  // namespace eet

#endif  // EET_TRAP_HPP
