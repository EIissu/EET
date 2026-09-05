"""Command line front end: ``eet asm``, ``eet run``, ``eet dis``.

Run it as ``python -m eetvm ...`` from ``runtimes/python``, or through the repository
driver, ``python tools/eet.py``.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path
from typing import BinaryIO, List, Optional

from . import isa
from .asm import AsmError, assemble
from .binary import LoadError, Module, load
from .disasm import disassemble
from .vm import execute


def _binary_stdout() -> BinaryIO:
    """Return stdout as a raw byte sink with no newline translation.

    On Windows the C runtime rewrites ``\\n`` to ``\\r\\n`` on a text-mode handle, which
    would make this runtime's output differ from the others by one byte per line. Spec
    section 4.6 forbids that, so the descriptor is forced to binary mode.
    """
    if os.name == "nt":  # pragma: no cover - exercised on Windows CI only
        import msvcrt

        msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)
        msvcrt.setmode(sys.stderr.fileno(), os.O_BINARY)
    return sys.stdout.buffer


def _read_module(path: Path) -> Module:
    """Load ``path``, assembling it first when it is ``.eet`` source."""
    if path.suffix == ".eet":
        return assemble(path.read_text(encoding="utf-8"), str(path))
    return load(path.read_bytes())


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="eet",
        description="Assemble, disassemble and run EET programs.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_asm = sub.add_parser("asm", help="assemble .eet source into a .eetb module")
    p_asm.add_argument("source", type=Path)
    p_asm.add_argument("-o", "--output", type=Path, help="defaults to SOURCE with .eetb")

    p_run = sub.add_parser("run", help="run a .eetb module, or assemble and run .eet source")
    p_run.add_argument("program", type=Path)

    p_dis = sub.add_parser("dis", help="disassemble a module to stdout")
    p_dis.add_argument("program", type=Path)

    args = parser.parse_args(argv)

    try:
        if args.command == "asm":
            module = assemble(args.source.read_text(encoding="utf-8"), str(args.source))
            output = args.output or args.source.with_suffix(".eetb")
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(module.to_bytes())
            return isa.EXIT_OK

        if args.command == "dis":
            sys.stdout.write(disassemble(_read_module(args.program)))
            return isa.EXIT_OK

        module = _read_module(args.program)
    except AsmError as error:
        print(f"eet: {error}", file=sys.stderr)
        return isa.EXIT_LOAD_ERROR
    except LoadError as error:
        print(f"eet: bad binary: {error}", file=sys.stderr)
        return isa.EXIT_LOAD_ERROR
    except OSError as error:
        print(f"eet: {error}", file=sys.stderr)
        return isa.EXIT_LOAD_ERROR

    stdout = _binary_stdout()
    return execute(module, stdout, sys.stderr.buffer)


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
