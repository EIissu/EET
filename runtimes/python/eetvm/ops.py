"""Exact ``i32`` semantics (spec section 7).

Python's integers are arbitrary-precision and its ``//`` and ``%`` floor rather than
truncate, so *none* of the built-in operators can be used directly. Every function here
exists to undo a Python convenience that would otherwise make this runtime disagree with
the C, C#, C++ and Java ones.

Read this module alongside spec section 7 -- the correspondence is one-to-one.
"""

from __future__ import annotations

INT32_MIN = -0x8000_0000
INT32_MAX = 0x7FFF_FFFF
UINT32_MASK = 0xFFFF_FFFF


def wrap(x: int) -> int:
    """Map an arbitrary integer onto ``i32`` by two's-complement truncation.

    This is the spec's ``wrap(x) = ((x + 2^31) mod 2^32) - 2^31``, written with a mask
    because that is faster and provably identical for every input.
    """
    x &= UINT32_MASK
    return x - 0x1_0000_0000 if x > INT32_MAX else x


def to_u32(x: int) -> int:
    """Reinterpret an ``i32`` as a ``u32`` without changing its bits."""
    return x & UINT32_MASK


def add(a: int, b: int) -> int:
    return wrap(a + b)


def sub(a: int, b: int) -> int:
    return wrap(a - b)


def mul(a: int, b: int) -> int:
    return wrap(a * b)


def neg(a: int) -> int:
    # neg(INT32_MIN) is INT32_MIN, which is exactly what wrap() produces.
    return wrap(-a)


def div(a: int, b: int) -> int:
    """Truncating division. The caller must have rejected ``b == 0`` already.

    Python's ``//`` floors, so ``-7 // 2`` is ``-4`` where the spec demands ``-3``. We
    divide magnitudes and reapply the sign instead.

    ``INT32_MIN / -1`` overflows; the spec says it wraps rather than trapping, and
    ``wrap`` delivers that for free.
    """
    q = abs(a) // abs(b)
    if (a < 0) != (b < 0):
        q = -q
    return wrap(q)


def mod(a: int, b: int) -> int:
    """Remainder whose sign follows the dividend. ``b == 0`` must already be rejected.

    Defined as ``a - div(a, b) * b`` so the identity ``(a/b)*b + (a%b) == a`` holds by
    construction, including at ``INT32_MIN % -1``, which is ``0``.
    """
    return wrap(a - wrap(div(a, b) * b))


def bit_and(a: int, b: int) -> int:
    return wrap(a & b)


def bit_or(a: int, b: int) -> int:
    return wrap(a | b)


def bit_xor(a: int, b: int) -> int:
    return wrap(a ^ b)


def bit_not(a: int) -> int:
    return wrap(~a)


def shl(a: int, b: int) -> int:
    return wrap(a << (b & 31))


def shr(a: int, b: int) -> int:
    """Arithmetic right shift. Python's ``>>`` already sign-propagates."""
    return wrap(a >> (b & 31))


def ushr(a: int, b: int) -> int:
    """Logical right shift: zero-fill from the left, done on the ``u32`` view."""
    return wrap(to_u32(a) >> (b & 31))


def format_decimal(v: int) -> bytes:
    """The exact bytes ``print`` emits for ``v`` (spec section 7.4).

    Python's ``str(int)`` already produces the shortest representation with a leading
    ``-`` and no leading zeros, including for ``INT32_MIN``, so this is a thin wrapper --
    but it is a named one, because the other four runtimes each need real code here.
    """
    return str(v).encode("ascii")
