"""Assembler behaviour, including the errors it is supposed to catch before run time."""

from __future__ import annotations

import struct
import unittest

from eetvm import AsmError, assemble


def asm(source: str):
    return assemble(source, "<test>")


class TestBasics(unittest.TestCase):
    def test_minimal_program(self):
        module = asm(".func main 0 0\n  push 0\n  ret\n.end\n")
        self.assertEqual(module.code, struct.pack("<Bi", 0x02, 0) + b"\x34")
        self.assertEqual(module.entry, 0)
        self.assertEqual(module.entry_locals, 0)

    def test_comments_and_blank_lines_are_ignored(self):
        plain = asm(".func main 0 0\n push 1\n ret\n.end\n")
        noisy = asm(
            "; leading comment\n"
            "\n"
            ".func main 0 0   # trailing comment\n"
            "   push 1        ; another\n"
            "\n"
            "   ret\n"
            ".end\n"
        )
        self.assertEqual(plain.code, noisy.code)

    def test_entry_defaults_to_main_and_can_be_overridden(self):
        module = asm(
            ".entry other\n"
            ".func main 0 0\n push 0\n ret\n.end\n"
            ".func other 0 3\n push 0\n ret\n.end\n"
        )
        self.assertEqual(module.entry_locals, 3)
        self.assertEqual(module.entry, 6)


class TestLiterals(unittest.TestCase):
    def test_number_bases(self):
        for text, value in (("255", 255), ("0xFF", 255), ("0b1111_1111", 255), ("0o377", 255)):
            with self.subTest(text=text):
                module = asm(f".func main 0 0\n push {text}\n ret\n.end\n")
                self.assertEqual(struct.unpack_from("<i", module.code, 1)[0], value)

    def test_negative_immediates(self):
        module = asm(".func main 0 0\n push -2147483648\n ret\n.end\n")
        self.assertEqual(struct.unpack_from("<i", module.code, 1)[0], -0x8000_0000)

    def test_character_literals(self):
        module = asm(".func main 0 0\n push 'A'\n ret\n.end\n")
        self.assertEqual(struct.unpack_from("<i", module.code, 1)[0], 65)

    def test_comment_character_inside_a_literal_is_not_a_comment(self):
        module = asm(".func main 0 0\n push '#'\n ret\n.end\n")
        self.assertEqual(struct.unpack_from("<i", module.code, 1)[0], 0x23)

    def test_string_escapes(self):
        module = asm('.data s "a\\nb\\t\\x41\\\\"\n.func main 0 0\n push 0\n ret\n.end\n')
        self.assertEqual(module.data, b"a\nb\tA\\")

    def test_data_accepts_bytes_and_strings_together(self):
        module = asm('.data s "hi", 0x21, 10\n.func main 0 0\n push 0\n ret\n.end\n')
        self.assertEqual(module.data, b"hi!\n")


class TestSymbols(unittest.TestCase):
    def test_forward_reference_to_a_label(self):
        module = asm(
            ".func main 0 0\n"
            "  push 0\n"
            "  jz done\n"
            "  push 1\n"
            "done:\n"
            "  push 0\n"
            "  ret\n"
            ".end\n"
        )
        target = struct.unpack_from("<I", module.code, 6)[0]
        self.assertEqual(target, 15)

    def test_labels_are_scoped_to_their_function(self):
        # `loop` means something different in each function, which is the whole point.
        module = asm(
            ".func a 0 0\nloop:\n  jmp loop\n.end\n"
            ".func main 0 0\nloop:\n  jmp loop\n.end\n"
        )
        self.assertEqual(struct.unpack_from("<I", module.code, 1)[0], 0)
        self.assertEqual(struct.unpack_from("<I", module.code, 6)[0], 5)

    def test_data_address_and_length(self):
        module = asm(
            '.data hello "hello"\n'
            ".func main 0 0\n  push hello\n  push hello.len\n  ret\n.end\n"
        )
        self.assertEqual(struct.unpack_from("<i", module.code, 1)[0], 0)
        self.assertEqual(struct.unpack_from("<i", module.code, 6)[0], 5)

    def test_pushs_expands_to_address_then_length(self):
        sugar = asm('.data m "abc"\n.func main 0 0\n pushs m\n prints\n ret\n.end\n')
        manual = asm(
            '.data m "abc"\n'
            ".func main 0 0\n push m\n push m.len\n prints\n ret\n.end\n"
        )
        self.assertEqual(sugar.code, manual.code)


class TestCalls(unittest.TestCase):
    def test_call_by_name_infers_the_signature(self):
        module = asm(
            ".func add2 2 2\n  load 0\n  load 1\n  add\n  ret\n.end\n"
            ".func main 0 0\n  push 1\n  push 2\n  call add2\n  ret\n.end\n"
        )
        index = module.code.index(b"\x33")
        _, target, nargs, nlocals = struct.unpack_from("<BIBB", module.code, index)
        self.assertEqual((target, nargs, nlocals), (0, 2, 2))

    def test_explicit_three_operand_call(self):
        module = asm(
            ".func f 0 4\n  push 0\n  ret\n.end\n"
            ".func main 0 0\n  call f, 0, 4\n  ret\n.end\n"
        )
        index = module.code.index(b"\x33")
        _, target, nargs, nlocals = struct.unpack_from("<BIBB", module.code, index)
        self.assertEqual((target, nargs, nlocals), (0, 0, 4))


class TestStaticErrors(unittest.TestCase):
    def assertRejects(self, source: str, fragment: str):
        with self.assertRaises(AsmError) as caught:
            asm(source)
        self.assertIn(fragment, str(caught.exception))

    def test_unknown_instruction(self):
        self.assertRejects(".func main 0 0\n frobnicate\n.end\n", "unknown instruction")

    def test_instruction_outside_a_function(self):
        self.assertRejects("push 1\n", "outside a .func")

    def test_missing_end(self):
        self.assertRejects(".func main 0 0\n push 0\n ret\n", "missing .end")

    def test_missing_entry_point(self):
        self.assertRejects(".func other 0 0\n push 0\n ret\n.end\n", "not defined")

    def test_duplicate_label(self):
        self.assertRejects(
            ".func main 0 0\nx:\n push 0\nx:\n ret\n.end\n", "duplicate label"
        )

    def test_undefined_symbol(self):
        self.assertRejects(".func main 0 0\n jmp nowhere\n.end\n", "undefined symbol")

    def test_wrong_operand_count(self):
        self.assertRejects(".func main 0 0\n push\n.end\n", "takes 1 operand")

    def test_nargs_greater_than_nlocals(self):
        self.assertRejects(".func f 3 1\n ret\n.end\n", "nargs > nlocals")

    def test_local_index_out_of_range_is_caught_at_assembly_time(self):
        # Better a compile error now than a T06 trap in four different languages later.
        self.assertRejects(
            ".func main 0 2\n load 5\n ret\n.end\n", "local 5 is out of range"
        )

    def test_global_index_out_of_range_is_caught_at_assembly_time(self):
        self.assertRejects(
            ".globals 2\n.func main 0 0\n gload 9\n ret\n.end\n", "global 9 is out of range"
        )

    def test_immediate_too_large(self):
        self.assertRejects(
            ".func main 0 0\n push 99999999999\n ret\n.end\n", "does not fit in i32"
        )

    def test_unterminated_string(self):
        self.assertRejects('.data s "oops\n.func main 0 0\n ret\n.end\n', "unterminated")

    def test_unknown_escape(self):
        self.assertRejects('.data s "\\q"\n.func main 0 0\n ret\n.end\n', "unknown escape")

    def test_unknown_directive(self):
        self.assertRejects(".sideways 1\n.func main 0 0\n ret\n.end\n", "unknown directive")

    def test_errors_name_the_line(self):
        with self.assertRaises(AsmError) as caught:
            asm(".func main 0 0\n push 0\n bogus\n ret\n.end\n")
        self.assertIn("<test>:3:", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
