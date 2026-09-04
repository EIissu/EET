"""Reading and writing the ``.eetb`` container (spec section 2)."""

from __future__ import annotations

import struct
from dataclasses import dataclass, field

from . import isa


class LoadError(Exception):
    """The bytes are not a valid EET binary. Exits with status 65, never a trap."""


@dataclass
class Module:
    """A loaded EET program."""

    nglobals: int = 0
    entry_locals: int = 0
    entry: int = 0
    code: bytes = b""
    data: bytes = b""
    #: Optional debug side table, ``code offset -> symbol name``. Never serialised.
    symbols: dict = field(default_factory=dict)

    def to_bytes(self) -> bytes:
        if not 0 <= self.nglobals <= 0xFFFF:
            raise LoadError("nglobals out of range")
        if not 0 <= self.entry_locals <= isa.MAX_LOCALS:
            raise LoadError("entry_locals out of range")
        if self.entry >= len(self.code):
            raise LoadError("entry past end of code")
        return b"".join(
            (
                isa.MAGIC,
                struct.pack(
                    "<HHHHII",
                    isa.VERSION,
                    0,  # flags
                    self.nglobals,
                    self.entry_locals,
                    self.entry,
                    len(self.code),
                ),
                self.code,
                struct.pack("<I", len(self.data)),
                self.data,
            )
        )


def load(blob: bytes) -> Module:
    """Parse and validate a ``.eetb`` image.

    Every rejection listed in spec section 2 is checked here, in the order the fields
    appear, so the diagnostic always names the first thing that is wrong.
    """
    if len(blob) < isa.MIN_FILE_SIZE:
        raise LoadError("file too short")
    if blob[:4] != isa.MAGIC:
        raise LoadError("bad magic")

    version, flags, nglobals, entry_locals, entry, code_len = struct.unpack_from(
        "<HHHHII", blob, 4
    )
    if version != isa.VERSION:
        raise LoadError(f"unsupported version {version}")
    if flags != 0:
        raise LoadError(f"unsupported flags 0x{flags:04X}")
    if entry_locals > isa.MAX_LOCALS:
        raise LoadError("entry_locals out of range")

    code_start = isa.HEADER_SIZE
    code_end = code_start + code_len
    # Guard against a code_len that overflows past the buffer before slicing.
    if code_end > len(blob) or code_end < code_start:
        raise LoadError("code section runs past end of file")
    if entry >= code_len:
        raise LoadError("entry past end of code")

    if len(blob) < code_end + 4:
        raise LoadError("missing data length")
    (data_len,) = struct.unpack_from("<I", blob, code_end)
    data_start = code_end + 4
    data_end = data_start + data_len
    if data_end > len(blob) or data_end < data_start:
        raise LoadError("data section runs past end of file")
    if data_end != len(blob):
        raise LoadError("trailing bytes after data section")

    return Module(
        nglobals=nglobals,
        entry_locals=entry_locals,
        entry=entry,
        code=blob[code_start:code_end],
        data=blob[data_start:data_end],
    )
