"""The EET instruction set, as data.

This module is the machine-readable twin of ``spec/eet-vm.md`` section 4. The test
``tests/test_spec_sync.py`` parses the specification's markdown tables and asserts that
they agree with the tables below, so the two can never drift apart silently.

Nothing here executes; see :mod:`eetvm.ops` for semantics and :mod:`eetvm.vm` for the
interpreter loop.
"""

from __future__ import annotations

from typing import Dict, NamedTuple, Tuple

# --- Container format (spec section 2) -------------------------------------------------

MAGIC = b"EETB"
VERSION = 1
HEADER_SIZE = 20  # magic..code_len inclusive; data_len follows the code section
MIN_FILE_SIZE = 24

# --- Machine limits (spec section 1) ---------------------------------------------------

MAX_OPERAND_STACK = 1024
MAX_CALL_DEPTH = 256
MAX_LOCALS = 256

# --- Exit statuses (spec section 5.3) --------------------------------------------------

EXIT_OK = 0
EXIT_LOAD_ERROR = 65
EXIT_TRAP = 70


class Operand(NamedTuple):
    """One immediate operand of an instruction."""

    name: str
    kind: str  # one of: i32, u8, u16, u32
    size: int


def _op(name: str, kind: str) -> Operand:
    return Operand(name, kind, {"u8": 1, "u16": 2, "u32": 4, "i32": 4}[kind])


class Insn(NamedTuple):
    """A single entry in the instruction set."""

    opcode: int
    mnemonic: str
    operands: Tuple[Operand, ...]

    @property
    def size(self) -> int:
        """Total encoded size in bytes, opcode included."""
        return 1 + sum(o.size for o in self.operands)


def _insn(opcode: int, mnemonic: str, *operands: Operand) -> Insn:
    return Insn(opcode, mnemonic, tuple(operands))


#: Every instruction in v1, in opcode order. Keep this list sorted by opcode -- the spec
#: sync test relies on the ordering matching the specification's tables.
INSTRUCTIONS: Tuple[Insn, ...] = (
    # 4.1 stack and control
    _insn(0x00, "halt"),
    _insn(0x01, "nop"),
    _insn(0x02, "push", _op("value", "i32")),
    _insn(0x03, "pop"),
    _insn(0x04, "dup"),
    _insn(0x05, "swap"),
    _insn(0x06, "over"),
    _insn(0x07, "rot"),
    # 4.2 arithmetic and bitwise
    _insn(0x10, "add"),
    _insn(0x11, "sub"),
    _insn(0x12, "mul"),
    _insn(0x13, "div"),
    _insn(0x14, "mod"),
    _insn(0x15, "neg"),
    _insn(0x16, "and"),
    _insn(0x17, "or"),
    _insn(0x18, "xor"),
    _insn(0x19, "not"),
    _insn(0x1A, "shl"),
    _insn(0x1B, "shr"),
    _insn(0x1C, "ushr"),
    # 4.3 comparison
    _insn(0x20, "eq"),
    _insn(0x21, "ne"),
    _insn(0x22, "lt"),
    _insn(0x23, "le"),
    _insn(0x24, "gt"),
    _insn(0x25, "ge"),
    # 4.4 branching and calls
    _insn(0x30, "jmp", _op("target", "u32")),
    _insn(0x31, "jz", _op("target", "u32")),
    _insn(0x32, "jnz", _op("target", "u32")),
    _insn(0x33, "call", _op("target", "u32"), _op("nargs", "u8"), _op("nlocals", "u8")),
    _insn(0x34, "ret"),
    # 4.5 memory
    _insn(0x40, "load", _op("idx", "u8")),
    _insn(0x41, "store", _op("idx", "u8")),
    _insn(0x42, "gload", _op("idx", "u16")),
    _insn(0x43, "gstore", _op("idx", "u16")),
    _insn(0x44, "dload"),
    _insn(0x45, "gloadx"),
    _insn(0x46, "gstorex"),
    # 4.6 output
    _insn(0x50, "print"),
    _insn(0x51, "printc"),
    _insn(0x52, "prints"),
    # 4.7 diagnostics
    _insn(0x60, "trap", _op("code", "u8")),
)

BY_OPCODE: Dict[int, Insn] = {i.opcode: i for i in INSTRUCTIONS}
BY_MNEMONIC: Dict[str, Insn] = {i.mnemonic: i for i in INSTRUCTIONS}

assert len(BY_OPCODE) == len(INSTRUCTIONS), "duplicate opcode in INSTRUCTIONS"
assert len(BY_MNEMONIC) == len(INSTRUCTIONS), "duplicate mnemonic in INSTRUCTIONS"

# --- Traps (spec section 6) ------------------------------------------------------------

#: Trap identifier -> the exact message text the runtime must print.
TRAPS: Dict[str, str] = {
    "T01": "stack underflow",
    "T02": "stack overflow",
    "T03": "call depth exceeded",
    "T04": "division by zero",
    "T05": "invalid opcode",
    "T06": "local index out of range",
    "T07": "global index out of range",
    "T08": "data access out of range",
    "T09": "jump out of range",
    "T10": "trap instruction",
}


def trap_line(trap_id: str, pc: int, detail: str | None = None) -> bytes:
    """Render the single stderr line a trap emits (spec section 6).

    ``pc`` is the address of the *first byte of the trapping instruction*, printed as
    exactly eight uppercase hex digits. The line always ends with a bare ``\\n``.
    """
    message = TRAPS[trap_id] if detail is None else detail
    return f"eet: trap {trap_id}: {message} at pc={pc & 0xFFFFFFFF:08X}\n".encode("ascii")
