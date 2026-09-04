"""Interpreter behaviour: stack discipline, the calling convention, and every trap."""

from __future__ import annotations

import io
import unittest

from eetvm import Module, Trap, assemble, execute
from eetvm.isa import MAX_CALL_DEPTH, MAX_OPERAND_STACK
from eetvm.vm import VM


def run(source: str):
    """Assemble and execute, returning ``(stdout, stderr, exit status)``."""
    module = assemble(source, "<test>")
    out, err = io.BytesIO(), io.BytesIO()
    status = execute(module, out, err)
    return out.getvalue(), err.getvalue(), status


def body(instructions: str, prologue: str = "", locals_: int = 4):
    return f"{prologue}.func main 0 {locals_}\n{instructions}\n.end\n"


class TestTermination(unittest.TestCase):
    def test_halt_exits_zero(self):
        self.assertEqual(run(body("  halt"))[2], 0)

    def test_ret_from_entry_frame_sets_the_exit_status(self):
        self.assertEqual(run(body("  push 3\n  ret"))[2], 3)

    def test_exit_status_is_masked_to_a_byte(self):
        self.assertEqual(run(body("  push 300\n  ret"))[2], 300 & 0xFF)
        self.assertEqual(run(body("  push -1\n  ret"))[2], 255)

    def test_falling_off_the_end_of_code_traps(self):
        out, err, status = run(body("  push 1\n  pop"))
        self.assertIn(b"T09", err)
        self.assertEqual(status, 70)


class TestStackOps(unittest.TestCase):
    def emit(self, instructions: str) -> bytes:
        out, err, status = run(body(instructions + "\n  push 0\n  ret"))
        self.assertEqual(err, b"")
        self.assertEqual(status, 0)
        return out

    def test_dup(self):
        self.assertEqual(self.emit("  push 7\n  dup\n  print\n  print"), b"77")

    def test_swap(self):
        self.assertEqual(self.emit("  push 1\n  push 2\n  swap\n  print\n  print"), b"12")

    def test_over_copies_the_second_item(self):
        # a b -> a b a, so printing three times yields a, b, a in reverse pop order.
        self.assertEqual(
            self.emit("  push 1\n  push 2\n  over\n  print\n  print\n  print"), b"121"
        )

    def test_rot_rotates_the_top_three_left(self):
        # a b c -> b c a
        self.assertEqual(
            self.emit("  push 1\n  push 2\n  push 3\n  rot\n  print\n  print\n  print"),
            b"132",
        )


class TestOutput(unittest.TestCase):
    def test_print_emits_no_newline_or_padding(self):
        out, _, _ = run(body("  push 42\n  print\n  push -7\n  print\n  push 0\n  ret"))
        self.assertEqual(out, b"42-7")

    def test_printc_writes_the_low_byte(self):
        out, _, _ = run(body("  push 0x141\n  printc\n  push 0\n  ret"))
        self.assertEqual(out, b"\x41")

    def test_prints_writes_the_range_verbatim(self):
        out, _, _ = run(
            body("  push 1\n  push 3\n  prints\n  push 0\n  ret", '.data m "hello"\n')
        )
        self.assertEqual(out, b"ell")

    def test_prints_of_zero_length_writes_nothing(self):
        out, _, _ = run(
            body("  push 0\n  push 0\n  prints\n  push 0\n  ret", '.data m "hello"\n')
        )
        self.assertEqual(out, b"")


class TestCallingConvention(unittest.TestCase):
    def test_arguments_land_in_source_order(self):
        # The first value pushed becomes locals[0].
        source = (
            ".func f 2 2\n  load 0\n  print\n  load 1\n  print\n  push 0\n  ret\n.end\n"
            ".func main 0 0\n  push 1\n  push 2\n  call f\n  pop\n  push 0\n  ret\n.end\n"
        )
        self.assertEqual(run(source)[0], b"12")

    def test_extra_locals_start_at_zero(self):
        source = (
            ".func f 1 3\n  load 1\n  print\n  load 2\n  print\n  push 0\n  ret\n.end\n"
            ".func main 0 0\n  push 9\n  call f\n  pop\n  push 0\n  ret\n.end\n"
        )
        self.assertEqual(run(source)[0], b"00")

    def test_return_value_reaches_the_caller(self):
        source = (
            ".func f 0 0\n  push 99\n  ret\n.end\n"
            ".func main 0 0\n  call f\n  print\n  push 0\n  ret\n.end\n"
        )
        self.assertEqual(run(source)[0], b"99")

    def test_callee_cannot_see_the_callers_stack(self):
        # Each frame gets a private operand stack, so over-popping is T01, not theft.
        source = (
            ".func f 0 0\n  pop\n  push 0\n  ret\n.end\n"
            ".func main 0 0\n  push 111\n  call f\n  pop\n  push 0\n  ret\n.end\n"
        )
        _, err, status = run(source)
        self.assertIn(b"T01", err)
        self.assertEqual(status, 70)


class TestTraps(unittest.TestCase):
    def assertTrap(self, trap_id: str, source: str):
        out, err, status = run(source)
        self.assertIn(trap_id.encode(), err, err)
        self.assertEqual(status, 70)
        self.assertTrue(err.endswith(b"\n"))
        self.assertFalse(err.endswith(b"\r\n"))
        return err

    def test_t01_stack_underflow(self):
        self.assertTrap("T01", body("  pop"))

    def test_t04_division_by_zero(self):
        self.assertTrap("T04", body("  push 1\n  push 0\n  div"))
        self.assertTrap("T04", body("  push 1\n  push 0\n  mod"))

    def test_t05_invalid_opcode(self):
        module = Module(code=b"\xEE", entry=0)
        out, err = io.BytesIO(), io.BytesIO()
        self.assertEqual(execute(module, out, err), 70)
        self.assertIn(b"T05", err.getvalue())

    def test_t05_truncated_immediate(self):
        module = Module(code=b"\x02\x01\x02", entry=0)  # push, then only 2 of 4 bytes
        out, err = io.BytesIO(), io.BytesIO()
        self.assertEqual(execute(module, out, err), 70)
        self.assertIn(b"T05", err.getvalue())

    def test_t07_global_index_out_of_range(self):
        self.assertTrap(
            "T07", body("  push 1\n  push 5\n  gstorex", ".globals 2\n")
        )
        self.assertTrap("T07", body("  push -1\n  gloadx", ".globals 2\n"))

    def test_t08_data_access_out_of_range(self):
        self.assertTrap("T08", body("  push 99\n  dload", '.data m "hi"\n'))
        self.assertTrap("T08", body("  push 0\n  push 99\n  prints", '.data m "hi"\n'))
        self.assertTrap("T08", body("  push 0\n  push -1\n  prints", '.data m "hi"\n'))

    def test_t09_jump_out_of_range(self):
        module = Module(code=b"\x30\xFF\xFF\x00\x00", entry=0)  # jmp 0xFFFF
        out, err = io.BytesIO(), io.BytesIO()
        self.assertEqual(execute(module, out, err), 70)
        self.assertIn(b"T09", err.getvalue())

    def test_t10_trap_instruction_carries_its_code(self):
        err = self.assertTrap("T10", body("  trap 7"))
        self.assertIn(b"trap instruction (code=7)", err)

    def test_output_before_a_trap_survives(self):
        out, err, status = run(
            body("  pushs m\n  prints\n  push 1\n  push 0\n  div", '.data m "before"\n')
        )
        self.assertEqual(out, b"before")
        self.assertIn(b"T04", err)
        self.assertEqual(status, 70)

    def test_trap_reports_the_address_of_the_trapping_instruction(self):
        # `push 1` is 5 bytes, `push 0` is 5 more, so `div` starts at 0x0A.
        _, err, _ = run(body("  push 1\n  push 0\n  div"))
        self.assertIn(b"pc=0000000A", err)


class TestLimits(unittest.TestCase):
    def test_operand_stack_holds_exactly_the_documented_depth(self):
        module = assemble(body("  halt"), "<test>")
        machine = VM(module, io.BytesIO())
        for i in range(MAX_OPERAND_STACK):
            machine.push(i)
        with self.assertRaises(Trap) as caught:
            machine.push(0)
        self.assertEqual(caught.exception.trap_id, "T02")

    def _recurse(self, depth: int):
        source = (
            ".func down 1 1\n"
            "  load 0\n"
            "  jz bottom\n"
            "  load 0\n"
            "  push 1\n"
            "  sub\n"
            "  call down\n"
            "  ret\n"
            "bottom:\n"
            "  push 0\n"
            "  ret\n"
            ".end\n"
            ".func main 0 0\n"
            f"  push {depth}\n"
            "  call down\n"
            "  pop\n"
            "  push 0\n"
            "  ret\n"
            ".end\n"
        )
        return run(source)

    def test_call_depth_boundary_is_exact(self):
        # entry frame + (depth + 1) activations of `down` must fit in MAX_CALL_DEPTH.
        deepest = MAX_CALL_DEPTH - 2
        self.assertEqual(self._recurse(deepest)[2], 0)
        _, err, status = self._recurse(deepest + 1)
        self.assertIn(b"T03", err)
        self.assertEqual(status, 70)


if __name__ == "__main__":
    unittest.main()
