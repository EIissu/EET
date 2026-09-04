"""Disassembler: turn a ``.eetb`` module back into readable text.

Used by ``eet dis`` and, more importantly, when a runtime disagrees with the goldens and
you need to see what the bytes actually say.
"""

from __future__ import annotations

import struct
from typing import Iterator, List

from . import isa
from .binary import Module

_UNPACK = {"u8": ("<B", 1), "u16": ("<H", 2), "u32": ("<I", 4), "i32": ("<i", 4)}


def disassemble(module: Module) -> str:
    """Render the whole module: a header comment, the code, then the data section."""
    lines: List[str] = [
        "; EET module",
        f";   entry        = 0x{module.entry:08X}",
        f";   entry_locals = {module.entry_locals}",
        f";   globals      = {module.nglobals}",
        f";   code         = {len(module.code)} bytes",
        f";   data         = {len(module.data)} bytes",
        "",
    ]
    lines.extend(disassemble_code(module))
    if module.data:
        lines.append("")
        lines.extend(_hexdump(module.data))
    return "\n".join(lines) + "\n"


def disassemble_code(module: Module) -> List[str]:
    """One line per instruction, with a hex gutter and any known symbol names."""
    out: List[str] = []
    for pc, insn, operands, raw in _walk(module.code):
        if pc in module.symbols:
            out.append("")
            out.append(f"{module.symbols[pc]}:")

        if insn is None:
            out.append(f"  {pc:08X}  {raw.hex(' '):<20}  <bad opcode 0x{raw[0]:02X}>")
            continue

        text = insn.mnemonic
        if operands:
            text += " " + ", ".join(_format_operand(insn, i, v) for i, v in enumerate(operands))
        marker = ""
        if insn.mnemonic in ("jmp", "jz", "jnz", "call") and operands:
            target = operands[0]
            if target in module.symbols:
                marker = f"  ; -> {module.symbols[target]}"
        out.append(f"  {pc:08X}  {raw.hex(' '):<20}  {text}{marker}")
    return out


def _format_operand(insn: isa.Insn, index: int, value: int) -> str:
    if insn.operands[index].kind == "u32":
        return f"0x{value:08X}"
    return str(value)


def _walk(code: bytes) -> Iterator[tuple]:
    """Yield ``(pc, insn, operand_values, raw_bytes)``; ``insn`` is None for bad opcodes."""
    pc = 0
    while pc < len(code):
        start = pc
        op = code[pc]
        pc += 1
        insn = isa.BY_OPCODE.get(op)
        if insn is None:
            yield start, None, [], code[start:pc]
            continue
        values: List[int] = []
        truncated = False
        for operand in insn.operands:
            fmt, size = _UNPACK[operand.kind]
            if pc + size > len(code):
                truncated = True
                break
            (value,) = struct.unpack_from(fmt, code, pc)
            values.append(value)
            pc += size
        if truncated:
            yield start, None, [], code[start:]
            return
        yield start, insn, values, code[start:pc]


def _hexdump(data: bytes, width: int = 16) -> List[str]:
    lines = ["; data section"]
    for offset in range(0, len(data), width):
        chunk = data[offset : offset + width]
        hex_part = " ".join(f"{b:02x}" for b in chunk).ljust(width * 3 - 1)
        text = "".join(chr(b) if 0x20 <= b < 0x7F else "." for b in chunk)
        lines.append(f";   {offset:08X}  {hex_part}  |{text}|")
    return lines
