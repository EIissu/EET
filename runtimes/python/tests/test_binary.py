"""Container validation: every rejection listed in spec section 2, plus a round trip."""

from __future__ import annotations

import struct
import unittest

from eetvm import LoadError, Module, load


def image(
    magic: bytes = b"EETB",
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
    """Build an image, with hooks for corrupting each field independently."""
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


class TestRoundTrip(unittest.TestCase):
    def test_fields_survive(self):
        original = Module(
            nglobals=17,
            entry_locals=5,
            entry=3,
            code=b"\x01\x01\x01\x00\x01",
            data=b"payload",
        )
        restored = load(original.to_bytes())
        self.assertEqual(restored.nglobals, 17)
        self.assertEqual(restored.entry_locals, 5)
        self.assertEqual(restored.entry, 3)
        self.assertEqual(restored.code, original.code)
        self.assertEqual(restored.data, original.data)

    def test_encoding_is_little_endian(self):
        blob = Module(nglobals=0x0201, code=b"\x00").to_bytes()
        self.assertEqual(blob[8:10], b"\x01\x02")

    def test_empty_data_section_is_fine(self):
        self.assertEqual(load(image(data=b"")).data, b"")

    def test_smallest_valid_image(self):
        # 24 bytes is the structural floor (header plus the data-length word), but a
        # valid program needs at least one code byte for `entry` to point at, so the
        # smallest thing that actually loads is 25 bytes: a lone `halt`.
        blob = image(code=b"\x00", data=b"")
        self.assertEqual(len(blob), 25)
        self.assertEqual(load(blob).code, b"\x00")
        self.assertEqual(len(image(code=b"", data=b"")), 24)


class TestRejections(unittest.TestCase):
    def assertRejects(self, blob: bytes, fragment: str):
        with self.assertRaises(LoadError) as caught:
            load(blob)
        self.assertIn(fragment, str(caught.exception))

    def test_too_short(self):
        self.assertRejects(b"EETB", "too short")
        self.assertRejects(image()[:8], "too short")

    def test_truncated_before_the_data_length_word(self):
        # Long enough to parse the header, too short to hold the data-length word.
        self.assertRejects(image()[:-1], "missing data length")

    def test_bad_magic(self):
        self.assertRejects(image(magic=b"NOPE"), "bad magic")

    def test_unsupported_version(self):
        self.assertRejects(image(version=2), "unsupported version")

    def test_non_zero_flags_are_rejected(self):
        # Reserved for v2 feature bits; a v1 loader must refuse rather than guess.
        self.assertRejects(image(flags=1), "unsupported flags")

    def test_entry_past_end_of_code(self):
        self.assertRejects(image(code=b"\x00", entry=1), "entry past end of code")

    def test_entry_locals_out_of_range(self):
        self.assertRejects(image(entry_locals=257), "entry_locals out of range")

    def test_code_length_runs_past_the_file(self):
        self.assertRejects(image(code=b"\x00", code_len=999), "code section runs past")

    def test_data_length_runs_past_the_file(self):
        self.assertRejects(image(data=b"ab", data_len=999), "data section runs past")

    def test_missing_data_length_word(self):
        blob = image()[:-4]
        self.assertRejects(blob, "too short")

    def test_trailing_bytes_are_rejected(self):
        self.assertRejects(image(trailer=b"junk"), "trailing bytes")

    def test_writer_rejects_an_entry_past_the_code(self):
        with self.assertRaises(LoadError):
            Module(code=b"\x00", entry=5).to_bytes()

    def test_writer_rejects_out_of_range_globals(self):
        with self.assertRaises(LoadError):
            Module(code=b"\x00", nglobals=70000).to_bytes()


if __name__ == "__main__":
    unittest.main()
