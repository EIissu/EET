"""Section 7 semantics, checked against independent oracles rather than themselves.

``eetvm.ops`` exists precisely because Python's built-in operators are wrong for this job,
so testing it with those same operators would prove nothing. Instead:

* ``ctypes.c_int32`` truncates in C, which is an independent implementation of ``wrap``;
* ``fractions.Fraction`` plus ``math.trunc`` gives exact truncating division with no
  floating point in the middle.
"""

from __future__ import annotations

import ctypes
import math
import unittest
from fractions import Fraction

from eetvm import ops

MIN = -0x8000_0000
MAX = 0x7FFF_FFFF

INTERESTING = [
    0, 1, -1, 2, -2, 7, -7, 255, 256, 65535, 65536,
    MAX, MIN, MAX - 1, MIN + 1, 123456789, -123456789, 987654321,
]


def c_wrap(x: int) -> int:
    """Independent oracle: let the C runtime do the truncation."""
    return ctypes.c_int32(x & 0xFFFF_FFFF).value


class TestWrap(unittest.TestCase):
    def test_matches_c_truncation(self):
        for x in INTERESTING + [MAX + 1, MIN - 1, 1 << 40, -(1 << 40), 0xFFFF_FFFF]:
            with self.subTest(x=x):
                self.assertEqual(ops.wrap(x), c_wrap(x))

    def test_is_idempotent_on_range(self):
        for x in INTERESTING:
            self.assertEqual(ops.wrap(ops.wrap(x)), ops.wrap(x))

    def test_documented_examples(self):
        self.assertEqual(ops.add(MAX, 1), MIN)
        self.assertEqual(ops.sub(MIN, 1), MAX)
        self.assertEqual(ops.mul(65536, 65536), 0)
        self.assertEqual(ops.neg(MIN), MIN)


class TestArithmetic(unittest.TestCase):
    def test_add_sub_mul_wrap_like_c(self):
        for a in INTERESTING:
            for b in INTERESTING:
                with self.subTest(a=a, b=b):
                    self.assertEqual(ops.add(a, b), c_wrap(a + b))
                    self.assertEqual(ops.sub(a, b), c_wrap(a - b))
                    self.assertEqual(ops.mul(a, b), c_wrap(a * b))


class TestDivision(unittest.TestCase):
    def test_truncates_toward_zero(self):
        for a in INTERESTING:
            for b in INTERESTING:
                if b == 0:
                    continue
                with self.subTest(a=a, b=b):
                    expected = c_wrap(math.trunc(Fraction(a, b)))
                    self.assertEqual(ops.div(a, b), expected)

    def test_remainder_follows_the_dividend(self):
        for a in INTERESTING:
            for b in INTERESTING:
                if b == 0:
                    continue
                with self.subTest(a=a, b=b):
                    expected = c_wrap(a - math.trunc(Fraction(a, b)) * b)
                    self.assertEqual(ops.mod(a, b), expected)

    def test_spec_table(self):
        self.assertEqual((ops.div(7, 2), ops.mod(7, 2)), (3, 1))
        self.assertEqual((ops.div(-7, 2), ops.mod(-7, 2)), (-3, -1))
        self.assertEqual((ops.div(7, -2), ops.mod(7, -2)), (-3, 1))
        self.assertEqual((ops.div(-7, -2), ops.mod(-7, -2)), (3, -1))

    def test_overflow_case_wraps_instead_of_trapping(self):
        # The one case where the hardware instruction faults on x86.
        self.assertEqual(ops.div(MIN, -1), MIN)
        self.assertEqual(ops.mod(MIN, -1), 0)

    def test_quotient_remainder_identity(self):
        for a in INTERESTING:
            for b in INTERESTING:
                if b == 0 or (a == MIN and b == -1):
                    continue  # the identity cannot hold where the quotient overflows
                with self.subTest(a=a, b=b):
                    self.assertEqual(ops.add(ops.mul(ops.div(a, b), b), ops.mod(a, b)), a)


class TestShifts(unittest.TestCase):
    def test_counts_are_masked_to_five_bits(self):
        for count in (0, 1, 31, 32, 33, 64, -1, -32):
            with self.subTest(count=count):
                self.assertEqual(ops.shl(1, count), c_wrap(1 << (count & 31)))
                self.assertEqual(ops.shr(-1, count), c_wrap(-1 >> (count & 31)))

    def test_arithmetic_versus_logical(self):
        self.assertEqual(ops.shr(-1, 1), -1)
        self.assertEqual(ops.ushr(-1, 1), MAX)
        self.assertEqual(ops.shr(MIN, 31), -1)
        self.assertEqual(ops.ushr(MIN, 31), 1)

    def test_logical_shift_by_zero_does_not_fill(self):
        # The mask makes the count zero, so no zero-fill happens and the sign survives.
        self.assertEqual(ops.ushr(-1, 0), -1)
        self.assertEqual(ops.ushr(-1, 32), -1)

    def test_ushr_matches_unsigned_view(self):
        for a in INTERESTING:
            for count in range(0, 32):
                with self.subTest(a=a, count=count):
                    self.assertEqual(ops.ushr(a, count), c_wrap((a & 0xFFFF_FFFF) >> count))


class TestFormatting(unittest.TestCase):
    def test_extremes(self):
        self.assertEqual(ops.format_decimal(MIN), b"-2147483648")
        self.assertEqual(ops.format_decimal(MAX), b"2147483647")

    def test_no_sign_or_padding_for_non_negative(self):
        self.assertEqual(ops.format_decimal(0), b"0")
        self.assertEqual(ops.format_decimal(7), b"7")
        self.assertEqual(ops.format_decimal(-7), b"-7")

    def test_round_trips(self):
        for x in INTERESTING:
            self.assertEqual(int(ops.format_decimal(x)), x)


if __name__ == "__main__":
    unittest.main()
