#include "eet/trap.hpp"

#include <cstddef>
#include <iterator>
#include <string_view>

#include "eet/format.hpp"

namespace eet {
namespace {

/// The exact message text for each trap, spec section 6. Indexed by TrapId.
constexpr std::string_view kMessages[] = {
    "stack underflow",
    "stack overflow",
    "call depth exceeded",
    "division by zero",
    "invalid opcode",
    "local index out of range",
    "global index out of range",
    "data access out of range",
    "jump out of range",
    "trap instruction",
};

constexpr std::string_view kNames[] = {
    "T01", "T02", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10",
};

static_assert(std::size(kMessages) == std::size(kNames),
              "every trap needs both a name and a message");
static_assert(static_cast<std::size_t>(TrapId::T10) + 1 == std::size(kMessages),
              "TrapId and the message table have drifted apart");

}  // namespace

std::string trapLine(const Trap& trap) {
    const auto index = static_cast<std::size_t>(trap.id);

    std::string line = "eet: trap ";
    line += kNames[index];
    line += ": ";
    line += kMessages[index];

    if (trap.id == TrapId::T10) {
        // T10 alone carries a payload: the operand of the `trap` instruction, in decimal.
        DecimalBuffer scratch;
        line += " (code=";
        line += formatDecimal(trap.userCode, scratch);
        line += ')';
    }

    line += " at pc=";
    appendHexUpper(line, trap.pc, 8);
    // A single LF. The Windows streams are put into binary mode before anything is
    // written precisely so this does not become CRLF.
    line += '\n';
    return line;
}

}  // namespace eet
