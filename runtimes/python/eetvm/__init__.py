"""EET -- a tiny stack machine, specified once and implemented everywhere.

This package is both the reference runtime and the shared toolchain: the assembler, the
disassembler and the interpreter that produces the conformance goldens.
"""

from .asm import AsmError, assemble
from .binary import LoadError, Module, load
from .disasm import disassemble
from .vm import VM, Trap, execute

__version__ = "1.0.0"

__all__ = [
    "AsmError",
    "LoadError",
    "Module",
    "Trap",
    "VM",
    "__version__",
    "assemble",
    "disassemble",
    "execute",
    "load",
]
