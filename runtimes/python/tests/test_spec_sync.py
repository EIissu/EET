"""The specification is a test fixture.

``spec/eet-vm.md`` is the authority for this project, which is only true if nothing can
quietly drift away from it. This module parses the specification's own markdown tables and
asserts that :mod:`eetvm.isa` agrees with them -- opcode by opcode, operand by operand,
trap by trap.

If you add an instruction, you must edit both the spec and the ISA table, and they must
agree, or this test fails. That is the intended friction.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from eetvm import isa

SPEC = Path(__file__).resolve().parents[3] / "spec" / "eet-vm.md"

_CODE = re.compile(r"`([^`]+)`")
_WIDTH = re.compile(r"\b(u8|u16|u32|i32)\b")


def _cells(line: str) -> List[str]:
    """Split a markdown table row into its cells."""
    return [c.strip() for c in line.strip().strip("|").split("|")]


def _is_separator(line: str) -> bool:
    return bool(re.fullmatch(r"\|[\s:|-]+\|", line.strip()))


class _Row:
    def __init__(self, opcode: int, mnemonic: str, widths: Tuple[str, ...]) -> None:
        self.opcode = opcode
        self.mnemonic = mnemonic
        self.widths = widths


def parse_instruction_tables(text: str) -> List[_Row]:
    """Pull every instruction row out of the spec, in document order.

    Only tables whose header names an ``Operands`` column contribute operand widths; the
    arithmetic and comparison tables have no such column and their instructions are
    correctly read as taking none.
    """
    rows: List[_Row] = []
    operand_column: Optional[int] = None
    in_table = False

    for line in text.splitlines():
        stripped = line.strip()
        if not stripped.startswith("|"):
            in_table = False
            operand_column = None
            continue

        cells = _cells(stripped)
        if _is_separator(stripped):
            in_table = True
            continue

        if not in_table:
            # This is a header row; remember where the operands live, if anywhere.
            lowered = [c.lower() for c in cells]
            operand_column = lowered.index("operands") if "operands" in lowered else None
            continue

        codes = _CODE.findall(cells[0]) if cells else []
        if not codes or not re.fullmatch(r"0x[0-9A-Fa-f]{2}", codes[0]):
            continue

        opcode = int(codes[0], 16)
        mnemonic_codes = _CODE.findall(cells[1])
        if not mnemonic_codes:
            continue
        mnemonic = mnemonic_codes[0]

        widths: Tuple[str, ...] = ()
        if operand_column is not None and operand_column < len(cells):
            widths = tuple(_WIDTH.findall(cells[operand_column]))
        rows.append(_Row(opcode, mnemonic, widths))

    return rows


def parse_trap_table(text: str) -> Dict[str, str]:
    traps: Dict[str, str] = {}
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped.startswith("|") or _is_separator(stripped):
            continue
        cells = _cells(stripped)
        if len(cells) < 2:
            continue
        ids = _CODE.findall(cells[0])
        messages = _CODE.findall(cells[1])
        if ids and messages and re.fullmatch(r"T\d{2}", ids[0]):
            traps[ids[0]] = messages[0]
    return traps


class TestSpecIsPresent(unittest.TestCase):
    def test_spec_file_exists(self):
        self.assertTrue(SPEC.is_file(), f"specification missing at {SPEC}")


class TestInstructionTable(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = SPEC.read_text(encoding="utf-8")
        cls.rows = parse_instruction_tables(cls.text)

    def test_parser_found_a_plausible_table(self):
        # A guard on the test itself: if the markdown shape changes and the parser starts
        # silently finding nothing, this fails loudly instead of passing vacuously.
        self.assertGreaterEqual(len(self.rows), 40)

    def test_same_opcodes_in_the_same_order(self):
        self.assertEqual(
            [(r.opcode, r.mnemonic) for r in self.rows],
            [(i.opcode, i.mnemonic) for i in isa.INSTRUCTIONS],
        )

    def test_no_duplicate_opcodes_in_the_spec(self):
        codes = [r.opcode for r in self.rows]
        self.assertEqual(len(codes), len(set(codes)))

    def test_operand_widths_match(self):
        for row in self.rows:
            insn = isa.BY_OPCODE[row.opcode]
            with self.subTest(mnemonic=row.mnemonic):
                self.assertEqual(
                    row.widths,
                    tuple(o.kind for o in insn.operands),
                    f"{row.mnemonic}: spec says {row.widths}, isa.py says "
                    f"{tuple(o.kind for o in insn.operands)}",
                )

    def test_reserved_ranges_are_not_used(self):
        # Spec section 9 reserves 0x70..0x9F for v2.
        for insn in isa.INSTRUCTIONS:
            self.assertFalse(0x70 <= insn.opcode <= 0x9F, f"{insn.mnemonic} squats a v2 range")


class TestTrapTable(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.traps = parse_trap_table(SPEC.read_text(encoding="utf-8"))

    def test_same_ids_and_messages(self):
        self.assertEqual(self.traps, isa.TRAPS)

    def test_trap_line_format(self):
        line = isa.trap_line("T04", 0x1A)
        self.assertEqual(line, b"eet: trap T04: division by zero at pc=0000001A\n")

    def test_trap_line_is_uppercase_and_eight_digits(self):
        self.assertIn(b"pc=00000000\n", isa.trap_line("T01", 0))
        self.assertIn(b"pc=FFFFFFFF\n", isa.trap_line("T01", 0xFFFFFFFF))
        self.assertIn(b"pc=0000ABCD\n", isa.trap_line("T01", 0xABCD))

    def test_trap_line_never_uses_crlf(self):
        for trap_id in isa.TRAPS:
            self.assertFalse(isa.trap_line(trap_id, 0).endswith(b"\r\n"))


class TestLimitsMatchTheProse(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = SPEC.read_text(encoding="utf-8")

    def test_stack_and_depth_limits_appear_in_the_spec(self):
        self.assertIn(f"**{isa.MAX_OPERAND_STACK}**", self.text)
        self.assertIn(f"**{isa.MAX_CALL_DEPTH}**", self.text)

    def test_exit_statuses_appear_in_the_spec(self):
        self.assertIn(f"**{isa.EXIT_TRAP}**", self.text)
        self.assertIn(f"**{isa.EXIT_LOAD_ERROR}**", self.text)


if __name__ == "__main__":
    unittest.main()
