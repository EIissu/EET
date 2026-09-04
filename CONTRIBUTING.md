# Contributing to EET

The repository has one invariant, and everything here exists to protect it:

> **Every runtime produces byte-identical output for the same input.**

If a change would weaken that, it does not land, however convenient it is.

## The ground rules

1. **The spec is the authority.** `spec/eet-vm.md` defines behaviour. A runtime that
   disagrees with it has a bug. If the spec is ambiguous, that is a *worse* bug, because
   it is a bug in every runtime at once — open an issue and we fix the prose first.
2. **Never edit a golden to make something pass.** `tests/conformance/golden/` is the
   pass/fail bar. Regenerate it with `python tools/eet.py golden` only when the spec has
   changed on purpose, and say so in the commit message.
3. **New behaviour lands spec-first.** Write the specification change, then the reference
   implementation, then the other runtimes. `test_spec_sync.py` parses the spec's own
   markdown tables and will fail if the code and the document drift apart.

## Getting set up

You need Python 3.9 or newer for the toolchain. Everything else is optional — the tools
detect what is installed and skip the rest:

```
python tools/eet.py list
```

| Runtime | Needs |
|---|---|
| Python | nothing beyond the standard library |
| Java | a JDK 17+ (`JAVA_HOME`, or `javac` on `PATH`) |
| C# / .NET | the .NET SDK |
| C++ | CMake and any C++20 compiler |

## The loop

```
python tools/eet.py build            # build every available runtime
python tools/eet.py verify           # the conformance matrix -- this is the gate
python tools/eet.py run programs/life.eet -r cpp
```

Plus the per-language unit tests:

```
cd runtimes/python && python -m unittest discover -s tests -t .
dotnet test runtimes/dotnet/Eet.sln
```

## Adding a program

Drop a `.eet` file in `programs/`, then:

```
python tools/eet.py golden           # record its expected output from the reference
python tools/eet.py verify           # confirm every runtime already agrees
```

A good program earns its place by exercising something the others do not. The existing
set covers recursion (`fib`), computed memory (`sieve`, `life`), fixed-point arithmetic
(`mandelbrot`), the data section (`banner`), the numeric edge cases (`arith`) and each
trap (`trap_*`). Adding a seventh way to print "hello" does not help.

If your program is meant to fail, name it `trap_*` and let the goldens capture the
stderr line and the exit status — that is a conformance test too, and a valuable one.

## Adding a runtime

This is the fun one, and it is deliberately easy:

1. Create `runtimes/<language>/`.
2. Implement `spec/eet-vm.md`. Read `runtimes/python/eetvm/` for a reference, but
   implement the *document*, not the Python.
3. Give it a CLI that takes exactly `<binary> run <file.eetb>`.
4. Add one `Runtime` entry to `tools/runtimes.py` describing how to build and invoke it.
5. `python tools/eet.py verify -r <key>` until it is green.
6. Add its toolchain to `.github/workflows/ci.yml`.

Nothing else in the tooling needs to know your language exists.

### What will break first

Every implementer has hit at least one of these. They are not hypothetical:

* **Integer overflow.** Must wrap, never throw, never saturate. C# needs `unchecked`;
  C++ needs the arithmetic done in `uint32_t` because signed overflow is undefined.
* **`INT32_MIN / -1`.** Must produce `INT32_MIN`, and `INT32_MIN % -1` must produce `0`.
  C# throws `OverflowException` here. C++ raises `SIGFPE` and kills the process. Java is
  already correct. Python needs help for a different reason — see below.
* **Division rounding.** Truncates toward zero; the remainder follows the dividend.
  Python's `//` and `%` floor instead, which is why `eetvm/ops.py` implements both by hand.
* **Shift counts.** Masked to five bits, always. Shifting by 32 or more is undefined in
  C++ and would otherwise diverge.
* **Newline translation.** stdout must be a raw byte stream. On Windows an untreated
  stdout turns every `\n` into `\r\n` and every multi-line golden fails by one byte a line.
* **Output before a trap.** It must survive. Flush before you exit.

## Style

`.editorconfig` covers the mechanical parts. Beyond that: write code that looks like it
belongs in its own language, not like the Python transliterated. Comment the *why* —
especially where your language's native behaviour differs from the spec and you had to
correct for it. Those comments are the most valuable prose in the repository.
