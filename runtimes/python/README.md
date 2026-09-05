# The Python runtime

Two things live here:

* **the reference implementation** of [`spec/eet-vm.md`](../../spec/eet-vm.md), and
* **the toolchain** everything else in the repository depends on — the assembler, the
  disassembler and the module loader.

That dual role is why this runtime is written for clarity over speed. Where a faster shape
would obscure the correspondence with the specification, the slower and more literal shape
wins, because four other implementations are read against this one.

The reference is not the authority, though. The spec is. If they disagree, the bug is here.

## Layout

| File | Spec section | What it is |
|---|---|---|
| `eetvm/isa.py` | 4 | The instruction set as data. `tests/test_spec_sync.py` checks it against the spec's own markdown tables. |
| `eetvm/ops.py` | 7 | Exact `i32` semantics. Every function here exists to undo a Python convenience. |
| `eetvm/binary.py` | 2 | The `.eetb` container, and every rejection the loader must make. |
| `eetvm/vm.py` | 5, 6 | The interpreter and the trap protocol. |
| `eetvm/asm.py` | — | The assembler. Two passes, so forward references just work. |
| `eetvm/disasm.py` | — | The disassembler, for when a runtime disagrees and you need to see the bytes. |
| `eetvm/cli.py` | 4.6 | `asm` / `run` / `dis`, and the binary-mode stdout that keeps Windows honest. |

## Use it

```
python -m eetvm asm programs/hello.eet -o build/hello.eetb
python -m eetvm run build/hello.eetb
python -m eetvm dis build/hello.eetb
```

Or install it and get an `eet` command:

```
pip install -e runtimes/python
```

## Tests

No dependencies, no test runner to install:

```
python -m unittest discover -s tests -t .
```

The interesting ones are `test_ops.py`, which checks the `i32` semantics against
`ctypes.c_int32` and `fractions.Fraction` rather than against the Python operators it
exists to replace, and `test_spec_sync.py`, which parses the specification and fails if
the prose and the code have drifted apart.
