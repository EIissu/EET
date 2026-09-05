#include "eet/vm.hpp"

#include <cstddef>
#include <cstdint>

#include "eet/format.hpp"
#include "eet/ops.hpp"

namespace eet {

// --- Frame ----------------------------------------------------------------------------

void Frame::enter(std::size_t nlocals, std::size_t returnPc) {
    // assign() and clear() keep the vectors' capacity, so a hot recursive program pays for
    // its locals once rather than on every call.
    locals_.assign(nlocals, 0);
    stack_.clear();
    returnPc_ = returnPc;
}

std::int32_t Frame::pop() noexcept {
    const std::int32_t value = stack_.back();
    stack_.pop_back();
    return value;
}

// --- Vm construction ------------------------------------------------------------------

Vm::Vm(const Module& module, ByteSink& out)
    : code_(module.code),
      data_(module.data),
      out_(out),
      globals_(module.nglobals, 0),
      frames_(kMaxCallDepth),
      depth_(1),
      pc_(module.entry),
      opPc_(module.entry) {
    // Startup, section 5.1: one frame with entry_locals zeroed slots and an empty operand
    // stack. Its return address is the "exit" sentinel, which ret never reads, because
    // returning from the entry frame terminates the program instead.
    frames_[0].enter(module.entryLocals, 0);
}

void Vm::raise(TrapId id) const {
    throw Trap{id, static_cast<std::uint32_t>(opPc_), 0};
}

// --- Decoding (section 5.2) -----------------------------------------------------------
//
// The invariant pc_ <= code_.size() holds on entry to every fetch, so the remaining-bytes
// test is written as a subtraction and cannot itself overflow.

std::uint8_t Vm::fetchU8() {
    if (code_.size() - pc_ < 1) {
        raise(TrapId::T05);
    }
    return code_[pc_++];
}

std::uint16_t Vm::fetchU16() {
    if (code_.size() - pc_ < 2) {
        raise(TrapId::T05);
    }
    const auto value = static_cast<std::uint16_t>(static_cast<unsigned>(code_[pc_]) |
                                                 (static_cast<unsigned>(code_[pc_ + 1]) << 8));
    pc_ += 2;
    return value;
}

std::uint32_t Vm::fetchU32() {
    if (code_.size() - pc_ < 4) {
        raise(TrapId::T05);
    }
    const std::uint32_t value = static_cast<std::uint32_t>(code_[pc_]) |
                                (static_cast<std::uint32_t>(code_[pc_ + 1]) << 8) |
                                (static_cast<std::uint32_t>(code_[pc_ + 2]) << 16) |
                                (static_cast<std::uint32_t>(code_[pc_ + 3]) << 24);
    pc_ += 4;
    return value;
}

std::int32_t Vm::fetchI32() {
    return ops::wrap(fetchU32());
}

// --- Operand stack --------------------------------------------------------------------

void Vm::push(std::int32_t value) {
    Frame& frame = current();
    if (frame.stackDepth() >= kMaxOperandStack) {
        raise(TrapId::T02);
    }
    frame.push(value);
}

std::int32_t Vm::pop() {
    Frame& frame = current();
    if (frame.stackEmpty()) {
        raise(TrapId::T01);
    }
    return frame.pop();
}

template <typename BinaryFn>
void Vm::binaryOp(BinaryFn&& fn) {
    // a is the deeper value, so it is popped second (section 4.2).
    const std::int32_t b = pop();
    const std::int32_t a = pop();
    push(fn(a, b));
}

// --- Control flow (section 5.4) -------------------------------------------------------

void Vm::jump(std::uint32_t target) {
    // Checked when the branch is taken, not when it is decoded (section 4.4).
    if (target >= code_.size()) {
        raise(TrapId::T09);
    }
    pc_ = target;
}

void Vm::doCall() {
    const std::uint32_t target = fetchU32();
    const std::uint8_t nargs = fetchU8();
    const std::uint8_t nlocals = fetchU8();

    // The assembler rejects nargs > nlocals statically; a loader need not, and section 5.4
    // pins what happens when such a module is executed anyway.
    if (nargs > nlocals) {
        raise(TrapId::T06);
    }
    if (depth_ >= kMaxCallDepth) {
        raise(TrapId::T03);
    }

    // The callee's record is prepared before the arguments move, but depth_ only advances
    // once they have: until then current() must still be the caller, because that is the
    // stack the arguments come off.
    Frame& callee = frames_[depth_];
    callee.enter(nlocals, pc_);
    for (std::size_t i = nargs; i-- > 0;) {
        // Popped back to front, so the first value the caller pushed lands in locals[0].
        callee.setLocal(i, pop());
    }

    if (target >= code_.size()) {
        raise(TrapId::T09);
    }
    ++depth_;
    pc_ = target;
}

std::optional<int> Vm::doRet() {
    const std::int32_t value = pop();
    if (depth_ == 1) {
        // Returning from the entry frame terminates the program (section 5.3). The status
        // is the low byte of the value, taken unsigned so that -1 becomes 255.
        return static_cast<int>(ops::bits(value) & 0xFFU);
    }
    pc_ = current().returnPc();
    --depth_;
    push(value);
    return std::nullopt;
}

// --- The cycle (section 5.2) ----------------------------------------------------------

int Vm::run() {
    for (;;) {
        // Recorded before the bounds test so that falling off the end of the code section
        // reports the address reached, just as every other trap reports its instruction.
        opPc_ = pc_;
        if (pc_ >= code_.size()) {
            raise(TrapId::T09);
        }
        const auto op = static_cast<Op>(code_[pc_++]);

        switch (op) {
        // -- 4.1 stack and control
        case Op::Halt:
            return kExitOk;
        case Op::Nop:
            break;
        case Op::Push:
            push(fetchI32());
            break;
        case Op::Pop:
            static_cast<void>(pop());
            break;
        case Op::Dup: {
            const std::int32_t a = pop();
            push(a);
            push(a);
            break;
        }
        case Op::Swap: {
            const std::int32_t b = pop();
            const std::int32_t a = pop();
            push(b);
            push(a);
            break;
        }
        case Op::Over: {  // a b -> a b a
            const std::int32_t b = pop();
            const std::int32_t a = pop();
            push(a);
            push(b);
            push(a);
            break;
        }
        case Op::Rot: {  // a b c -> b c a
            const std::int32_t c = pop();
            const std::int32_t b = pop();
            const std::int32_t a = pop();
            push(b);
            push(c);
            push(a);
            break;
        }

        // -- 4.2 arithmetic and bitwise
        case Op::Add:
            binaryOp(ops::add);
            break;
        case Op::Sub:
            binaryOp(ops::sub);
            break;
        case Op::Mul:
            binaryOp(ops::mul);
            break;
        case Op::Div: {
            const std::int32_t b = pop();
            const std::int32_t a = pop();
            if (b == 0) {
                raise(TrapId::T04);
            }
            push(ops::div(a, b));
            break;
        }
        case Op::Mod: {
            const std::int32_t b = pop();
            const std::int32_t a = pop();
            if (b == 0) {
                raise(TrapId::T04);
            }
            push(ops::mod(a, b));
            break;
        }
        case Op::Neg:
            push(ops::neg(pop()));
            break;
        case Op::And:
            binaryOp(ops::bitAnd);
            break;
        case Op::Or:
            binaryOp(ops::bitOr);
            break;
        case Op::Xor:
            binaryOp(ops::bitXor);
            break;
        case Op::Not:
            push(ops::bitNot(pop()));
            break;
        case Op::Shl:
            binaryOp(ops::shl);
            break;
        case Op::Shr:
            binaryOp(ops::shr);
            break;
        case Op::Ushr:
            binaryOp(ops::ushr);
            break;

        // -- 4.3 comparison: signed throughout, 1 for true and 0 for false
        case Op::Eq:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a == b ? 1 : 0; });
            break;
        case Op::Ne:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a != b ? 1 : 0; });
            break;
        case Op::Lt:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a < b ? 1 : 0; });
            break;
        case Op::Le:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a <= b ? 1 : 0; });
            break;
        case Op::Gt:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a > b ? 1 : 0; });
            break;
        case Op::Ge:
            binaryOp([](std::int32_t a, std::int32_t b) -> std::int32_t { return a >= b ? 1 : 0; });
            break;

        // -- 4.4 branching and calls
        case Op::Jmp:
            jump(fetchU32());
            break;
        case Op::Jz: {
            // The target is decoded first and the condition popped second, so an empty
            // stack here is T01 rather than a decoding fault.
            const std::uint32_t target = fetchU32();
            if (pop() == 0) {
                jump(target);
            }
            break;
        }
        case Op::Jnz: {
            const std::uint32_t target = fetchU32();
            if (pop() != 0) {
                jump(target);
            }
            break;
        }
        case Op::Call:
            doCall();
            break;
        case Op::Ret:
            if (const std::optional<int> status = doRet()) {
                return *status;
            }
            break;

        // -- 4.5 memory
        case Op::Load: {
            const std::uint8_t index = fetchU8();
            const Frame& frame = current();
            if (static_cast<std::size_t>(index) >= frame.localCount()) {
                raise(TrapId::T06);
            }
            push(frame.local(index));
            break;
        }
        case Op::Store: {
            const std::uint8_t index = fetchU8();
            if (static_cast<std::size_t>(index) >= current().localCount()) {
                raise(TrapId::T06);
            }
            const std::int32_t value = pop();
            current().setLocal(index, value);
            break;
        }
        case Op::Gload: {
            const std::uint16_t index = fetchU16();
            if (static_cast<std::size_t>(index) >= globals_.size()) {
                raise(TrapId::T07);
            }
            push(globals_[index]);
            break;
        }
        case Op::Gstore: {
            const std::uint16_t index = fetchU16();
            if (static_cast<std::size_t>(index) >= globals_.size()) {
                raise(TrapId::T07);
            }
            globals_[index] = pop();
            break;
        }
        case Op::Dload: {
            const std::int32_t address = pop();
            if (address < 0 || static_cast<std::size_t>(address) >= data_.size()) {
                raise(TrapId::T08);
            }
            // Zero-extended: the pushed value is always in 0..255 (section 4.5).
            push(static_cast<std::int32_t>(data_[static_cast<std::size_t>(address)]));
            break;
        }
        case Op::Gloadx: {
            const std::int32_t index = pop();
            if (index < 0 || static_cast<std::size_t>(index) >= globals_.size()) {
                raise(TrapId::T07);
            }
            push(globals_[static_cast<std::size_t>(index)]);
            break;
        }
        case Op::Gstorex: {
            // The index is on top, the value beneath it (section 4.5).
            const std::int32_t index = pop();
            const std::int32_t value = pop();
            if (index < 0 || static_cast<std::size_t>(index) >= globals_.size()) {
                raise(TrapId::T07);
            }
            globals_[static_cast<std::size_t>(index)] = value;
            break;
        }

        // -- 4.6 output
        case Op::Print: {
            DecimalBuffer scratch;
            out_.write(formatDecimal(pop(), scratch));
            break;
        }
        case Op::Printc:
            out_.writeByte(static_cast<std::uint8_t>(ops::bits(pop()) & 0xFFU));
            break;
        case Op::Prints: {
            const std::int32_t length = pop();
            const std::int32_t address = pop();
            // The end of the range is computed in 64 bits, because a hostile module can
            // pick an address and a length whose i32 sum wraps back into the data section.
            const std::int64_t end = static_cast<std::int64_t>(address) + length;
            if (length < 0 || address < 0 || end > static_cast<std::int64_t>(data_.size())) {
                raise(TrapId::T08);
            }
            out_.write(data_.subspan(static_cast<std::size_t>(address),
                                     static_cast<std::size_t>(length)));
            break;
        }

        // -- 4.7 diagnostics
        case Op::Trap: {
            const std::uint8_t code = fetchU8();
            throw Trap{TrapId::T10, static_cast<std::uint32_t>(opPc_), code};
        }

        default:
            // Any byte that is not an opcode listed in section 4.
            raise(TrapId::T05);
        }
    }
}

// --- Termination (section 5.3) --------------------------------------------------------

int execute(const Module& module, ByteSink& out, ByteSink& err) {
    Vm machine(module, out);
    try {
        const int status = machine.run();
        out.flush();
        return status;
    } catch (const Trap& trap) {
        // A trap does not discard prior output: stdout is flushed before the single
        // diagnostic line is written, so the two streams stay in the order they were
        // produced even when a terminal merges them.
        out.flush();
        err.write(trapLine(trap));
        err.flush();
        return kExitTrap;
    }
}

}  // namespace eet
