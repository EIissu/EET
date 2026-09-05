"""The malformed-image corpus (spec section 2).

Every rejection the specification requires of a loader, as a handcrafted `.eetb` file. The
assembler cannot produce these -- that is the point. A runtime that only ever sees valid
input from the assembler will happily read past the end of a buffer when handed something
hostile, and nothing in `programs/` would ever notice.

Each case must make a runtime print a line beginning `eet: bad binary:` on stderr and exit
**65**. The text after the prefix is deliberately *not* specified, so only the prefix and
the status are checked; runtimes are free to word the reason however they like.
"""

from __future__ import annotations

import struct
from typing import Dict, List, NamedTuple

MAGIC = b"EETB"


def image(
    magic: bytes = MAGIC,
    version: int = 1,
    flags: int = 0,
    nglobals: int = 0,
    entry_locals: int = 0,
    entry: int = 0,
    code: bytes = b"\x00",
    data: bytes = b"",
    code_len: int = None,
    data_len: int = None,
    trailer: bytes = b"",
) -> bytes:
    """A well-formed image by default, with a hook for corrupting each field."""
    return b"".join(
        (
            magic,
            struct.pack(
                "<HHHHII",
                version,
                flags,
                nglobals,
                entry_locals,
                entry,
                len(code) if code_len is None else code_len,
            ),
            code,
            struct.pack("<I", len(data) if data_len is None else data_len),
            data,
            trailer,
        )
    )


class Case(NamedTuple):
    name: str
    why: str
    blob: bytes


#: One entry per bullet in spec section 2, plus a couple of hostile shapes.
CASES: List[Case] = [
    Case("empty", "a zero-length file", b""),
    Case("short-magic", "shorter than the magic itself", b"EE"),
    Case(
        "short-header",
        "the magic is right but the header is truncated",
        MAGIC + b"\x01\x00\x00\x00",
    ),
    Case("bad-magic", "the right size, the wrong magic", image(magic=b"NOPE")),
    Case("version-0", "version 0 is not version 1", image(version=0)),
    Case("version-2", "a future version this loader cannot honour", image(version=2)),
    Case(
        "flags-set",
        "a reserved flag bit is set, so the file may mean something else entirely",
        image(flags=1),
    ),
    Case(
        "flags-high",
        "the high reserved flag bit is set",
        image(flags=0x8000),
    ),
    Case("entry-past-code", "the entry point is outside the code section", image(entry=1)),
    Case(
        "entry-locals-257",
        "more entry locals than a frame may hold",
        image(entry_locals=257),
    ),
    Case(
        "code-len-huge",
        "code_len points far past the end of the file",
        image(code_len=0xFFFF_FF00),
    ),
    Case(
        "code-len-overflows",
        "code_len is large enough to wrap a 32-bit offset calculation",
        image(code_len=0xFFFF_FFFF),
    ),
    Case(
        "data-len-huge",
        "data_len points far past the end of the file",
        image(data=b"ab", data_len=0xFFFF_FF00),
    ),
    Case(
        "data-len-overflows",
        "data_len is large enough to wrap a 32-bit offset calculation",
        image(data=b"ab", data_len=0xFFFF_FFFF),
    ),
    Case("missing-data-len", "the data-length word is cut off", image()[:-1]),
    Case("trailing-bytes", "extra bytes after the data section", image(trailer=b"junk")),
    Case(
        "empty-code",
        "a zero-length code section leaves the entry point nowhere to be",
        image(code=b"", data=b""),
    ),
]

BY_NAME: Dict[str, Case] = {c.name: c for c in CASES}
assert len(BY_NAME) == len(CASES), "duplicate case name"
