"""The EET assembler: ``.eet`` source text to a ``.eetb`` module.

The language is line-oriented and deliberately tiny. See ``docs/assembly.md`` for the
tutorial; this module is the normative implementation.

Assembly happens in two passes. The first parses every line into an :class:`_Item` and
lays out addresses, which requires nothing but instruction sizes. The second resolves
symbols and emits bytes. Forward references therefore just work.
"""

from __future__ import annotations

import re
import struct
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Tuple

from . import isa
from .binary import Module


class AsmError(Exception):
    """A source-level error, already formatted as ``file:line: message``."""


@dataclass
class _Item:
    """One assembled instruction, before symbols are resolved."""

    line_no: int
    mnemonic: str
    args: List["_Arg"]
    address: int = 0
    size: int = 0
    func: Optional["_Func"] = None


@dataclass
class _Arg:
    """An operand: either a literal integer or a symbol reference."""

    line_no: int
    value: Optional[int] = None
    symbol: Optional[str] = None


@dataclass
class _Func:
    name: str
    nargs: int
    nlocals: int
    line_no: int
    address: int = 0
    labels: Dict[str, int] = field(default_factory=dict)


# --- lexing ----------------------------------------------------------------------------

_IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_.]*")
_NUMBER = re.compile(r"[+-]?(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|[0-9][0-9_]*)")

_ESCAPES = {
    "n": 0x0A,
    "r": 0x0D,
    "t": 0x09,
    "0": 0x00,
    "\\": 0x5C,
    '"': 0x22,
    "'": 0x27,
    "e": 0x1B,
}


class _Token:
    __slots__ = ("kind", "text", "value")

    def __init__(self, kind: str, text: str, value: object = None) -> None:
        self.kind = kind  # ident | number | string | punct
        self.text = text
        self.value = value

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"<{self.kind} {self.text!r}>"


def _tokenize(line: str, where: str, line_no: int) -> List[_Token]:
    """Split one source line into tokens, stripping comments outside string literals."""
    tokens: List[_Token] = []
    i, n = 0, len(line)
    while i < n:
        ch = line[i]
        if ch in " \t\r\n":
            i += 1
            continue
        if ch in ";#":
            break
        if ch in ":,":
            tokens.append(_Token("punct", ch))
            i += 1
            continue
        if ch == '"':
            raw, i = _scan_string(line, i, '"', where, line_no)
            tokens.append(_Token("string", raw.decode("latin-1"), raw))
            continue
        if ch == "'":
            raw, i = _scan_string(line, i, "'", where, line_no)
            if len(raw) != 1:
                raise AsmError(
                    f"{where}:{line_no}: character literal must be exactly one byte"
                )
            tokens.append(_Token("number", repr(raw), raw[0]))
            continue
        m = _NUMBER.match(line, i)
        if m:
            # A leading sign is always part of the literal: the language has no infix
            # arithmetic, so `push -1` can only ever mean the immediate -1.
            tokens.append(_Token("number", m.group(), _parse_int(m.group())))
            i = m.end()
            continue
        m = _IDENT.match(line, i)
        if m:
            tokens.append(_Token("ident", m.group()))
            i = m.end()
            continue
        if ch == ".":
            m = _IDENT.match(line, i + 1)
            if m:
                tokens.append(_Token("ident", "." + m.group()))
                i = m.end()
                continue
        raise AsmError(f"{where}:{line_no}: unexpected character {ch!r}")
    return tokens


def _scan_string(line: str, i: int, quote: str, where: str, line_no: int) -> Tuple[bytes, int]:
    """Scan a quoted literal starting at ``line[i]``, returning its bytes and the next index."""
    out = bytearray()
    i += 1
    while True:
        if i >= len(line):
            raise AsmError(f"{where}:{line_no}: unterminated {quote}literal")
        ch = line[i]
        if ch == quote:
            return bytes(out), i + 1
        if ch == "\\":
            i += 1
            if i >= len(line):
                raise AsmError(f"{where}:{line_no}: unterminated escape")
            esc = line[i]
            if esc == "x":
                hex_digits = line[i + 1 : i + 3]
                if len(hex_digits) != 2 or not all(
                    c in "0123456789abcdefABCDEF" for c in hex_digits
                ):
                    raise AsmError(f"{where}:{line_no}: \\x needs two hex digits")
                out.append(int(hex_digits, 16))
                i += 3
                continue
            if esc not in _ESCAPES:
                raise AsmError(f"{where}:{line_no}: unknown escape \\{esc}")
            out.append(_ESCAPES[esc])
            i += 1
            continue
        out.extend(ch.encode("utf-8"))
        i += 1


def _parse_int(text: str) -> int:
    text = text.replace("_", "")
    negative = text.startswith("-")
    if text[0] in "+-":
        text = text[1:]
    base = 10
    if text[:2].lower() == "0x":
        base, text = 16, text[2:]
    elif text[:2].lower() == "0b":
        base, text = 2, text[2:]
    elif text[:2].lower() == "0o":
        base, text = 8, text[2:]
    value = int(text, base)
    return -value if negative else value


# --- assembling ------------------------------------------------------------------------

class Assembler:
    """Turns one source file into a :class:`~eetvm.binary.Module`."""

    def __init__(self, source: str, where: str = "<input>") -> None:
        self.source = source
        self.where = where
        self.nglobals = 0
        self.entry_name = "main"
        self.entry_line = 0
        self.data = bytearray()
        self.data_symbols: Dict[str, Tuple[int, int]] = {}
        self.funcs: Dict[str, _Func] = {}
        self.items: List[_Item] = []

    # -- pass 1 -------------------------------------------------------------------------

    def parse(self) -> None:
        current: Optional[_Func] = None
        address = 0

        for line_no, raw in enumerate(self.source.splitlines(), start=1):
            tokens = _tokenize(raw, self.where, line_no)
            if not tokens:
                continue

            head = tokens[0]

            # label:
            if head.kind == "ident" and len(tokens) >= 2 and tokens[1].text == ":":
                if current is None:
                    raise AsmError(
                        f"{self.where}:{line_no}: label {head.text!r} outside a .func"
                    )
                if head.text in current.labels:
                    raise AsmError(
                        f"{self.where}:{line_no}: duplicate label {head.text!r}"
                    )
                current.labels[head.text] = address
                tokens = tokens[2:]
                if not tokens:
                    continue
                head = tokens[0]

            if head.kind == "ident" and head.text.startswith("."):
                current, address = self._directive(
                    head.text, tokens[1:], line_no, current, address
                )
                continue

            if current is None:
                raise AsmError(
                    f"{self.where}:{line_no}: instruction outside a .func"
                )
            address = self._instruction(tokens, line_no, current, address)

        if current is not None:
            raise AsmError(
                f"{self.where}:{current.line_no}: .func {current.name!r} is missing .end"
            )
        if self.entry_name not in self.funcs:
            raise AsmError(
                f"{self.where}:{self.entry_line or 1}: entry point "
                f"{self.entry_name!r} is not defined"
            )

    def _directive(
        self,
        name: str,
        rest: List[_Token],
        line_no: int,
        current: Optional[_Func],
        address: int,
    ) -> Tuple[Optional[_Func], int]:
        if name == ".globals":
            if current is not None:
                raise AsmError(f"{self.where}:{line_no}: .globals must be at top level")
            if len(rest) != 1 or rest[0].kind != "number":
                raise AsmError(f"{self.where}:{line_no}: .globals needs one count")
            count = int(rest[0].value)
            if not 0 <= count <= 0xFFFF:
                raise AsmError(f"{self.where}:{line_no}: .globals out of range")
            self.nglobals = count
            return current, address

        if name == ".entry":
            if len(rest) != 1 or rest[0].kind != "ident":
                raise AsmError(f"{self.where}:{line_no}: .entry needs a function name")
            self.entry_name = rest[0].text
            self.entry_line = line_no
            return current, address

        if name == ".data":
            if current is not None:
                raise AsmError(f"{self.where}:{line_no}: .data must be at top level")
            if not rest or rest[0].kind != "ident":
                raise AsmError(f"{self.where}:{line_no}: .data needs a name")
            label = rest[0].text
            if label in self.data_symbols:
                raise AsmError(f"{self.where}:{line_no}: duplicate .data {label!r}")
            start = len(self.data)
            for token in rest[1:]:
                if token.kind == "punct" and token.text == ",":
                    continue
                if token.kind == "string":
                    self.data.extend(token.value)
                elif token.kind == "number":
                    self.data.append(int(token.value) & 0xFF)
                else:
                    raise AsmError(
                        f"{self.where}:{line_no}: .data takes strings and byte values"
                    )
            self.data_symbols[label] = (start, len(self.data) - start)
            return current, address

        if name == ".func":
            if current is not None:
                raise AsmError(f"{self.where}:{line_no}: nested .func")
            if len(rest) != 3 or rest[0].kind != "ident":
                raise AsmError(
                    f"{self.where}:{line_no}: usage: .func NAME nargs nlocals"
                )
            fname = rest[0].text
            if fname in self.funcs:
                raise AsmError(f"{self.where}:{line_no}: duplicate .func {fname!r}")
            nargs, nlocals = int(rest[1].value), int(rest[2].value)
            # A frame may hold up to MAX_LOCALS slots, but `call` encodes nargs and
            # nlocals as u8, so a callable function tops out at 255. Reject that here
            # rather than letting the encoder fail with a struct error later.
            if not 0 <= nargs <= 255 or not 0 <= nlocals <= 255:
                raise AsmError(
                    f"{self.where}:{line_no}: .func {fname!r} counts must be 0..255 "
                    f"(call encodes them as u8)"
                )
            if nargs > nlocals:
                raise AsmError(
                    f"{self.where}:{line_no}: .func {fname!r} has nargs > nlocals"
                )
            func = _Func(fname, nargs, nlocals, line_no, address)
            self.funcs[fname] = func
            return func, address

        if name == ".end":
            if current is None:
                raise AsmError(f"{self.where}:{line_no}: .end without .func")
            return None, address

        raise AsmError(f"{self.where}:{line_no}: unknown directive {name!r}")

    def _instruction(
        self, tokens: List[_Token], line_no: int, func: _Func, address: int
    ) -> int:
        mnemonic = tokens[0].text.lower()
        args: List[_Arg] = []
        for token in tokens[1:]:
            if token.kind == "punct" and token.text == ",":
                continue
            if token.kind == "number":
                args.append(_Arg(line_no, value=int(token.value)))
            elif token.kind == "ident":
                args.append(_Arg(line_no, symbol=token.text))
            else:
                raise AsmError(f"{self.where}:{line_no}: bad operand {token.text!r}")

        item = _Item(line_no, mnemonic, args, address=address, func=func)

        if mnemonic == "pushs":
            # Sugar: `pushs msg` becomes `push <addr>` + `push <len>`, ready for `prints`.
            if len(args) != 1 or args[0].symbol is None:
                raise AsmError(f"{self.where}:{line_no}: pushs needs one .data name")
            item.size = 10
        elif mnemonic == "call":
            item.size = isa.BY_MNEMONIC["call"].size
        elif mnemonic in isa.BY_MNEMONIC:
            insn = isa.BY_MNEMONIC[mnemonic]
            if len(args) != len(insn.operands):
                raise AsmError(
                    f"{self.where}:{line_no}: {mnemonic} takes "
                    f"{len(insn.operands)} operand(s), got {len(args)}"
                )
            item.size = insn.size
        else:
            raise AsmError(f"{self.where}:{line_no}: unknown instruction {mnemonic!r}")

        self.items.append(item)
        return address + item.size

    # -- pass 2 -------------------------------------------------------------------------

    def emit(self) -> Module:
        code = bytearray()
        for item in self.items:
            assert len(code) == item.address, "layout drifted between passes"
            code.extend(self._encode(item))

        entry = self.funcs[self.entry_name]
        module = Module(
            nglobals=self.nglobals,
            entry_locals=entry.nlocals,
            entry=entry.address,
            code=bytes(code),
            data=bytes(self.data),
        )
        module.symbols = {f.address: f.name for f in self.funcs.values()}
        return module

    def _encode(self, item: _Item) -> bytes:
        where, line_no = self.where, item.line_no

        if item.mnemonic == "pushs":
            name = item.args[0].symbol or ""
            if name not in self.data_symbols:
                raise AsmError(f"{where}:{line_no}: unknown .data name {name!r}")
            addr, length = self.data_symbols[name]
            return struct.pack("<BiBi", 0x02, addr, 0x02, length)

        if item.mnemonic == "call":
            return self._encode_call(item)

        insn = isa.BY_MNEMONIC[item.mnemonic]
        out = bytearray((insn.opcode,))
        for operand, arg in zip(insn.operands, item.args):
            value = self._resolve(arg, item)
            out.extend(self._pack(operand, value, item))
        self._static_check(item, [self._resolve(a, item) for a in item.args])
        return bytes(out)

    def _encode_call(self, item: _Item) -> bytes:
        args = item.args
        if len(args) == 1:
            name = args[0].symbol
            if name is None or name not in self.funcs:
                raise AsmError(
                    f"{self.where}:{item.line_no}: call needs a known function name, "
                    f"or an explicit target, nargs, nlocals"
                )
            callee = self.funcs[name]
            return struct.pack("<BIBB", 0x33, callee.address, callee.nargs, callee.nlocals)
        if len(args) == 3:
            target = self._resolve(args[0], item)
            nargs = self._resolve(args[1], item)
            nlocals = self._resolve(args[2], item)
            if nargs > nlocals:
                raise AsmError(f"{self.where}:{item.line_no}: call has nargs > nlocals")
            for value, label in ((nargs, "nargs"), (nlocals, "nlocals")):
                if not 0 <= value <= 255:
                    raise AsmError(
                        f"{self.where}:{item.line_no}: call {label} out of range"
                    )
            return struct.pack("<BIBB", 0x33, target & 0xFFFFFFFF, nargs, nlocals)
        raise AsmError(
            f"{self.where}:{item.line_no}: call takes 1 or 3 operands, got {len(args)}"
        )

    def _resolve(self, arg: _Arg, item: _Item) -> int:
        if arg.value is not None:
            return arg.value
        name = arg.symbol or ""
        func = item.func
        assert func is not None
        if name in func.labels:
            return func.labels[name]
        if name in self.funcs:
            return self.funcs[name].address
        if name.endswith(".len") and name[:-4] in self.data_symbols:
            return self.data_symbols[name[:-4]][1]
        if name in self.data_symbols:
            return self.data_symbols[name][0]
        raise AsmError(f"{self.where}:{arg.line_no}: undefined symbol {name!r}")

    def _pack(self, operand: isa.Operand, value: int, item: _Item) -> bytes:
        where, line_no = self.where, item.line_no
        if operand.kind == "i32":
            if not -0x8000_0000 <= value <= 0xFFFF_FFFF:
                raise AsmError(f"{where}:{line_no}: {value} does not fit in i32")
            return struct.pack("<i", value if value <= 0x7FFF_FFFF else value - 0x1_0000_0000)
        limit = {"u8": 0xFF, "u16": 0xFFFF, "u32": 0xFFFF_FFFF}[operand.kind]
        if not 0 <= value <= limit:
            raise AsmError(
                f"{where}:{line_no}: {operand.name}={value} out of range for {operand.kind}"
            )
        return struct.pack({"u8": "<B", "u16": "<H", "u32": "<I"}[operand.kind], value)

    def _static_check(self, item: _Item, values: List[int]) -> None:
        """Catch at assembly time what would otherwise be a trap at run time."""
        func = item.func
        assert func is not None
        if item.mnemonic in ("load", "store") and values[0] >= func.nlocals:
            raise AsmError(
                f"{self.where}:{item.line_no}: local {values[0]} is out of range for "
                f".func {func.name!r} (nlocals={func.nlocals})"
            )
        if item.mnemonic in ("gload", "gstore") and values[0] >= self.nglobals:
            raise AsmError(
                f"{self.where}:{item.line_no}: global {values[0]} is out of range "
                f"(.globals={self.nglobals})"
            )


def assemble(source: str, where: str = "<input>") -> Module:
    """Assemble ``source`` into a module, raising :class:`AsmError` on any problem."""
    assembler = Assembler(source, where)
    assembler.parse()
    return assembler.emit()
