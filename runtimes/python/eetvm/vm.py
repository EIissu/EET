"""The EET interpreter -- the reference implementation (spec section 5).

Correctness beats speed here on purpose. Where a faster shape would obscure the
correspondence with the specification, the slower and more literal shape wins, because
four other runtimes are read against this one.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from typing import BinaryIO, List

from . import isa, ops
from .binary import Module


class Trap(Exception):
    """A deterministic runtime fault (spec section 6)."""

    def __init__(self, trap_id: str, pc: int, detail: str | None = None) -> None:
        super().__init__(trap_id)
        self.trap_id = trap_id
        self.pc = pc
        self.detail = detail

    def line(self) -> bytes:
        return isa.trap_line(self.trap_id, self.pc, self.detail)


@dataclass
class Frame:
    """One activation record: private locals, private operand stack, return address."""

    locals: List[int]
    return_pc: int
    stack: List[int] = field(default_factory=list)


class VM:
    """A single EET machine bound to one output stream."""

    def __init__(self, module: Module, stdout: BinaryIO) -> None:
        self.module = module
        self.code = module.code
        self.data = module.data
        self.code_len = len(module.code)
        self.stdout = stdout
        self.globals: List[int] = [0] * module.nglobals
        self.frames: List[Frame] = [Frame([0] * module.entry_locals, -1)]
        self.pc = module.entry
        #: Address of the instruction currently executing; every trap reports this.
        self.op_pc = module.entry

    # -- frame and stack helpers --------------------------------------------------------

    @property
    def frame(self) -> Frame:
        return self.frames[-1]

    def push(self, value: int) -> None:
        stack = self.frames[-1].stack
        if len(stack) >= isa.MAX_OPERAND_STACK:
            raise Trap("T02", self.op_pc)
        stack.append(value)

    def pop(self) -> int:
        stack = self.frames[-1].stack
        if not stack:
            raise Trap("T01", self.op_pc)
        return stack.pop()

    # -- instruction decoding -----------------------------------------------------------

    def _fetch(self, fmt: str, size: int) -> int:
        if self.pc + size > self.code_len:
            raise Trap("T05", self.op_pc)
        (value,) = struct.unpack_from(fmt, self.code, self.pc)
        self.pc += size
        return value

    def _u8(self) -> int:
        return self._fetch("<B", 1)

    def _u16(self) -> int:
        return self._fetch("<H", 2)

    def _u32(self) -> int:
        return self._fetch("<I", 4)

    def _i32(self) -> int:
        return self._fetch("<i", 4)

    def _jump(self, target: int) -> None:
        if target >= self.code_len:
            raise Trap("T09", self.op_pc)
        self.pc = target

    # -- the main loop ------------------------------------------------------------------

    def run(self) -> int:
        """Execute until termination. Returns the process exit status.

        Raises :class:`Trap`; the caller is responsible for flushing stdout and printing
        the trap line, because the spec requires prior output to survive a trap.
        """
        while True:
            if self.pc >= self.code_len:
                # Falling off the end of the code section, spec section 5.4.
                raise Trap("T09", self.pc)
            self.op_pc = self.pc
            op = self.code[self.pc]
            self.pc += 1

            result = self._execute(op)
            if result is not None:
                return result

    def _execute(self, op: int) -> int | None:
        """Run one instruction. Returns an exit status only when the program terminates."""
        # -- 4.1 stack and control
        if op == 0x00:  # halt
            return isa.EXIT_OK
        if op == 0x01:  # nop
            return None
        if op == 0x02:  # push
            self.push(self._i32())
            return None
        if op == 0x03:  # pop
            self.pop()
            return None
        if op == 0x04:  # dup
            a = self.pop()
            self.push(a)
            self.push(a)
            return None
        if op == 0x05:  # swap
            b, a = self.pop(), self.pop()
            self.push(b)
            self.push(a)
            return None
        if op == 0x06:  # over: a b -> a b a
            b, a = self.pop(), self.pop()
            self.push(a)
            self.push(b)
            self.push(a)
            return None
        if op == 0x07:  # rot: a b c -> b c a
            c, b, a = self.pop(), self.pop(), self.pop()
            self.push(b)
            self.push(c)
            self.push(a)
            return None

        # -- 4.2 arithmetic and bitwise
        if 0x10 <= op <= 0x1C:
            return self._arith(op)

        # -- 4.3 comparison
        if 0x20 <= op <= 0x25:
            b, a = self.pop(), self.pop()
            self.push(1 if _COMPARE[op](a, b) else 0)
            return None

        # -- 4.4 branching and calls
        if op == 0x30:  # jmp
            self._jump(self._u32())
            return None
        if op == 0x31:  # jz
            target = self._u32()
            if self.pop() == 0:
                self._jump(target)
            return None
        if op == 0x32:  # jnz
            target = self._u32()
            if self.pop() != 0:
                self._jump(target)
            return None
        if op == 0x33:  # call
            return self._call()
        if op == 0x34:  # ret
            return self._ret()

        # -- 4.5 memory
        if op == 0x40:  # load
            idx = self._u8()
            slots = self.frame.locals
            if idx >= len(slots):
                raise Trap("T06", self.op_pc)
            self.push(slots[idx])
            return None
        if op == 0x41:  # store
            idx = self._u8()
            slots = self.frame.locals
            if idx >= len(slots):
                raise Trap("T06", self.op_pc)
            slots[idx] = self.pop()
            return None
        if op == 0x42:  # gload
            idx = self._u16()
            if idx >= len(self.globals):
                raise Trap("T07", self.op_pc)
            self.push(self.globals[idx])
            return None
        if op == 0x43:  # gstore
            idx = self._u16()
            if idx >= len(self.globals):
                raise Trap("T07", self.op_pc)
            self.globals[idx] = self.pop()
            return None
        if op == 0x44:  # dload
            addr = self.pop()
            if addr < 0 or addr >= len(self.data):
                raise Trap("T08", self.op_pc)
            self.push(self.data[addr])
            return None
        if op == 0x45:  # gloadx
            idx = self.pop()
            if idx < 0 or idx >= len(self.globals):
                raise Trap("T07", self.op_pc)
            self.push(self.globals[idx])
            return None
        if op == 0x46:  # gstorex
            idx = self.pop()
            value = self.pop()
            if idx < 0 or idx >= len(self.globals):
                raise Trap("T07", self.op_pc)
            self.globals[idx] = value
            return None

        # -- 4.6 output
        if op == 0x50:  # print
            self.stdout.write(ops.format_decimal(self.pop()))
            return None
        if op == 0x51:  # printc
            self.stdout.write(bytes((self.pop() & 0xFF,)))
            return None
        if op == 0x52:  # prints
            length = self.pop()
            addr = self.pop()
            if length < 0 or addr < 0 or addr + length > len(self.data):
                raise Trap("T08", self.op_pc)
            self.stdout.write(self.data[addr : addr + length])
            return None

        # -- 4.7 diagnostics
        if op == 0x60:  # trap
            code = self._u8()
            raise Trap("T10", self.op_pc, f"trap instruction (code={code})")

        raise Trap("T05", self.op_pc)

    def _arith(self, op: int) -> None:
        if op == 0x15:  # neg
            self.push(ops.neg(self.pop()))
            return None
        if op == 0x19:  # not
            self.push(ops.bit_not(self.pop()))
            return None

        b, a = self.pop(), self.pop()
        if op in (0x13, 0x14) and b == 0:
            raise Trap("T04", self.op_pc)
        self.push(_BINARY[op](a, b))
        return None

    def _call(self) -> None:
        target = self._u32()
        nargs = self._u8()
        nlocals = self._u8()

        if nargs > nlocals:
            raise Trap("T06", self.op_pc)
        if len(self.frames) >= isa.MAX_CALL_DEPTH:
            raise Trap("T03", self.op_pc)

        # Pop into locals back-to-front so the first value pushed lands in locals[0].
        slots = [0] * nlocals
        for i in range(nargs - 1, -1, -1):
            slots[i] = self.pop()

        if target >= self.code_len:
            raise Trap("T09", self.op_pc)

        self.frames.append(Frame(slots, self.pc))
        self.pc = target
        return None

    def _ret(self) -> int | None:
        value = self.pop()
        finished = self.frames.pop()
        if not self.frames:
            # Returning from the entry frame terminates the program (spec section 5.3).
            return value & 0xFF
        self.pc = finished.return_pc
        self.push(value)
        return None


_BINARY = {
    0x10: ops.add,
    0x11: ops.sub,
    0x12: ops.mul,
    0x13: ops.div,
    0x14: ops.mod,
    0x16: ops.bit_and,
    0x17: ops.bit_or,
    0x18: ops.bit_xor,
    0x1A: ops.shl,
    0x1B: ops.shr,
    0x1C: ops.ushr,
}

_COMPARE = {
    0x20: lambda a, b: a == b,
    0x21: lambda a, b: a != b,
    0x22: lambda a, b: a < b,
    0x23: lambda a, b: a <= b,
    0x24: lambda a, b: a > b,
    0x25: lambda a, b: a >= b,
}


def execute(module: Module, stdout: BinaryIO, stderr: BinaryIO) -> int:
    """Run ``module``, returning the process exit status.

    Handles the trap protocol: prior stdout is flushed first, then the single trap line
    goes to stderr, then the status is 70.
    """
    machine = VM(module, stdout)
    try:
        status = machine.run()
    except Trap as trap:
        stdout.flush()
        stderr.write(trap.line())
        stderr.flush()
        return isa.EXIT_TRAP
    except RecursionError:  # pragma: no cover - defensive; the VM loop is iterative
        stdout.flush()
        stderr.write(isa.trap_line("T03", machine.op_pc))
        stderr.flush()
        return isa.EXIT_TRAP
    stdout.flush()
    return status
