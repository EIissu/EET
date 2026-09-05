// The interpreter (spec section 5).

#ifndef EET_VM_HPP
#define EET_VM_HPP

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

#include "eet/isa.hpp"
#include "eet/module.hpp"
#include "eet/sink.hpp"
#include "eet/trap.hpp"

namespace eet {

/// One activation record: private locals, a private operand stack and a return address.
///
/// Section 1.2 makes the operand stack frame-local, so a callee cannot see or corrupt its
/// caller's values; the only ways across the boundary are `call` arguments and the `ret`
/// value.
class Frame {
public:
    /// Reuses this record for a new activation: `nlocals` zeroed slots, an empty stack.
    void enter(std::size_t nlocals, std::size_t returnPc);

    [[nodiscard]] std::size_t stackDepth() const noexcept { return stack_.size(); }
    [[nodiscard]] bool stackEmpty() const noexcept { return stack_.empty(); }

    /// Precondition: the caller has checked the section 6 limits (T01 / T02).
    void push(std::int32_t value) { stack_.push_back(value); }
    std::int32_t pop() noexcept;

    [[nodiscard]] std::size_t localCount() const noexcept { return locals_.size(); }
    [[nodiscard]] std::int32_t local(std::size_t index) const noexcept { return locals_[index]; }
    void setLocal(std::size_t index, std::int32_t value) noexcept { locals_[index] = value; }

    [[nodiscard]] std::size_t returnPc() const noexcept { return returnPc_; }

private:
    std::vector<std::int32_t> locals_;
    std::vector<std::int32_t> stack_;
    std::size_t returnPc_ = 0;
};

/// A single EET machine bound to one output stream.
class Vm {
public:
    /// `module` and `out` must outlive the machine.
    Vm(const Module& module, ByteSink& out);

    /// Runs to termination and returns the process exit status. Throws Trap.
    int run();

private:
    // -- decoding (section 5.2)
    std::uint8_t fetchU8();
    std::uint16_t fetchU16();
    std::uint32_t fetchU32();
    std::int32_t fetchI32();

    // -- frame and stack helpers
    [[nodiscard]] Frame& current() noexcept { return frames_[depth_ - 1]; }
    void push(std::int32_t value);
    std::int32_t pop();

    template <typename BinaryFn>
    void binaryOp(BinaryFn&& fn);

    // -- instructions with enough shape to deserve a name (section 5.4)
    void jump(std::uint32_t target);
    void doCall();
    std::optional<int> doRet();

    [[noreturn]] void raise(TrapId id) const;

    std::span<const std::uint8_t> code_;
    std::span<const std::uint8_t> data_;
    ByteSink& out_;
    std::vector<std::int32_t> globals_;
    /// Pre-allocated frame pool. The depth ceiling is 256 (section 1), so the whole call
    /// stack is allocated once and each activation reuses a record's storage instead of
    /// churning the heap on every call and return.
    std::vector<Frame> frames_;
    std::size_t depth_ = 0;
    std::size_t pc_ = 0;
    /// First byte of the instruction being executed; every trap reports this (section 6).
    std::size_t opPc_ = 0;
};

/// Runs `module` and returns the process exit status, honouring the termination protocol
/// of section 5.3: stdout is flushed before any trap line reaches stderr, so output
/// produced before a fault still arrives.
[[nodiscard]] int execute(const Module& module, ByteSink& out, ByteSink& err);

}  // namespace eet

#endif  // EET_VM_HPP
