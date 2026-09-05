```
#####  #####  #####
#      #        #
####   ####     #
#      #        #
#      #        #
#      #        #
#####  #####    #

  Everything Everywhere, Together.
  One spec. Four languages. Zero disagreements.
```

# EET

**One tiny virtual machine. One specification. Four languages. Byte-for-byte identical output.**

[![conformance](https://github.com/EIissu/EET/actions/workflows/ci.yml/badge.svg)](https://github.com/EIissu/EET/actions/workflows/ci.yml)
[![license](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)
[![languages](https://img.shields.io/badge/languages-Python%20%7C%20Java%20%7C%20C%23%20%7C%20C%2B%2B-informational)](spec/eet-vm.md)

That banner up there is not a picture. It is the output of
[`programs/banner.eet`](programs/banner.eet), a program written for a stack machine that
does not exist outside this repository — and it prints exactly those bytes whether you run
it on the Python interpreter, the Java one, the .NET one or the C++ one.

Making that sentence true is the entire project.

---

## The idea

Write a specification precise enough that four independent implementations, in four
languages with four different ideas about what an integer is, cannot disagree. Then prove
it, on every push, by running every program on every runtime and diffing the bytes.

```
             spec/eet-vm.md                  the authority
                    │
        ┌───────────┴───────────┐
        │                       │
   assembler  ───►  .eetb  ───► every runtime
   (Python)         bytes       │
                                ├── Python   reference
                                ├── Java
                                ├── C# / .NET
                                └── C++
                                    │
                                    ▼
                         byte-for-byte identical
                         stdout · stderr · exit status
```

It sounds easy. It is not, and the reasons why are the interesting part — see
[where the languages disagree](#where-the-languages-disagree).

## Try it

Only Python is required to drive everything; the tools detect the rest and skip what you
do not have.

```bash
python tools/eet.py list      # what can this machine build?
python tools/eet.py build     # build every available runtime
python tools/eet.py verify    # the conformance matrix -- this is the whole point
```

Run something:

```bash
python tools/eet.py run programs/mandelbrot.eet
python tools/eet.py run programs/life.eet -r cpp
python tools/eet.py run programs/fib.eet -r java
```

There are no floating point numbers in EET, so `mandelbrot.eet` builds a fixed-point FPU
out of `mul` and `shr` and produces this — identically, on all four:

```
                                     ......:......
                                 ..........:=:::.....
                              ............::=+::.......
                            .............:#@@@@=:........
                         ..........:::::::@@@@@-:::.......
                      ............-@@@@@@@@@@@@@@@@-:*-*...
                   .............:#:=@@@@@@@@@@@@@@@@@@+:....
             .......-:.........::+@@@@@@@@@@@@@@@@@@@@@::....
         ...........:-+@:=@@::::+@@@@@@@@@@@@@@@@@@@@@@+:....
      .............::-#@@@@@@@-=@@@@@@@@@@@@@@@@@@@@@@@@%.....
     ...........:---=@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#......
     ::--@--=--=*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-:.......
     ...........:---=@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@......
      .............:--@@@@@@@@-=@@@@@@@@@@@@@@@@@@@@@@@@+.....
        ............:-#%-=#+::::@@@@@@@@@@@@@@@@@@@@@@@+:....
             .......-:...:.....::=@@@@@@@@@@@@@@@@@@@@@::....
                   .............:=-=@@@@@@@@@@@@@@@@@@=:....
                      ............:#@@@@@@@@@@@@@@*--+=*...
                         .........::::::::+@@@@-:::.......
                           .............::@@@@@+:........
                              ............::=#::.......
                                 ..........:=:=:.....
                                    .......-.......
```

## Write something

EET assembly is small enough to learn in a sitting — the whole language is in
[`docs/assembly.md`](docs/assembly.md).

```asm
.data greeting "Hello from EET!\n"

.func main 0 0
    pushs greeting
    prints
    push 0
    ret
.end
```

```bash
python tools/eet.py run programs/hello.eet
```

## Where the languages disagree

This is the part worth reading. Every row below is a place where writing the obvious code
in one language silently produces a different answer than the obvious code in another —
which is why [spec section 7](spec/eet-vm.md) pins all of it down, and why every runtime
has a comment explaining what it had to do about it.

| The rule | Python | Java | C# | C++ |
|---|---|---|---|---|
| **Overflow wraps** — `2147483647 + 1` is `-2147483648` | ints are arbitrary precision; must mask | correct as-is | needs explicit `unchecked` | signed overflow is **undefined**; do it in `uint32_t` |
| **Division truncates toward zero** — `-7 / 2` is `-3` | `//` **floors** to `-4`; hand-rolled | correct as-is | correct as-is | correct as-is |
| **Remainder follows the dividend** — `-7 % 2` is `-1` | `%` returns `1`; hand-rolled | correct as-is | correct as-is | correct as-is |
| **`INT32_MIN / -1` wraps to `INT32_MIN`** | fine once corrected | correct as-is | **throws `OverflowException`** (so does `%`, and `Math.Abs`) | **traps in hardware and kills the process** — `SIGFPE` on POSIX, `EXCEPTION_INT_OVERFLOW` (`0xC0000095`) on Windows |
| **Shift counts mask to 5 bits** — `1 << 32` is `1` | no mask; must apply one | masks already | masks already | shifting by ≥ 32 is **undefined** |
| **`ushr` is a zero-filling shift** | no such operator | `>>>` | `(int)((uint)a >> n)` | shift the unsigned view |
| **stdout is a raw byte stream** | set `O_BINARY` on Windows | write bytes past `PrintStream` | `Console.OpenStandardOutput()` | `_setmode(_O_BINARY)` on Windows |

Get any one of these wrong and `mandelbrot.eet` still renders — just slightly differently.
That is exactly the class of bug this repository is built to catch.

## The conformance matrix

`verify` assembles every program, runs it on every available runtime, and compares stdout,
stderr and the exit status against the goldens in `tests/conformance/golden/`:

```
conformance matrix
                Python          Java            C# / .NET       C++
arith           ok              ok              ok              ok
banner          ok              ok              ok              ok
edges           ok              ok              ok              ok
fib             ok              ok              ok              ok
hello           ok              ok              ok              ok
life            ok              ok              ok              ok
limits          ok              ok              ok              ok
mandelbrot      ok              ok              ok              ok
sieve           ok              ok              ok              ok
trap_data       ok              ok              ok              ok
trap_depth      ok              ok              ok              ok
trap_div0       ok              ok              ok              ok
trap_falloff    ok              ok              ok              ok
trap_global     ok              ok              ok              ok
trap_span       ok              ok              ok              ok
trap_underflow  ok              ok              ok              ok
trap_user       ok              ok              ok              ok

malformed input
                    Python          Java            C# / .NET       C++
empty               ok              ok              ok              ok
bad-magic           ok              ok              ok              ok
flags-set           ok              ok              ok              ok
code-len-overflows  ok              ok              ok              ok
...

all 136 checks passed across 4 runtime(s)  (68 program, 68 malformed)
```

Programs named `trap_*` are supposed to fail. Their goldens capture the exact stderr line
and the exit status, because *how* a machine breaks is part of its behaviour too.

The **malformed corpus** is the other half. The assembler can only ever emit valid
modules, so nothing in `programs/` can tell you what a runtime does when handed a hostile
file. `tools/malformed.py` builds one image per rejection the spec requires — bad magic, a
reserved flag bit set, an entry point outside the code, and two whose declared section
lengths are large enough to wrap a 32-bit offset calculation — and every runtime must
refuse all of them with exit 65.

## What's here

```
spec/eet-vm.md          the authority: machine model, binary format, every instruction,
                        every trap, and the exact numeric semantics
programs/               EET programs, and the conformance corpus
runtimes/python/        reference implementation + the assembler and disassembler
runtimes/java/          Java 17, javac only, no build tool
runtimes/dotnet/        a real .NET solution: class library, CLI, 117 xUnit tests
runtimes/cpp/           C++20 and CMake
tools/eet.py            build, run, benchmark, verify
tools/runtimes.py       the runtime registry -- one entry per language
tools/malformed.py      hostile images the assembler could never produce
tests/conformance/      the goldens -- byte-exact fixtures
docs/assembly.md        how to write EET assembly
```

Nothing outside `runtimes/python` has a dependency. Java builds with `javac` and no build
tool, C++ links only the standard library, and the .NET solution takes nothing beyond the
test packages. The only thing you install to work on EET is a compiler.

## The machine, briefly

A stack machine with 32-bit signed integers and nothing else. Per-frame operand stacks, a
globals array that doubles as a heap, a read-only data section, and about forty
instructions:

| | |
|---|---|
| stack | `push` `pop` `dup` `swap` `over` `rot` |
| arithmetic | `add` `sub` `mul` `div` `mod` `neg` |
| bitwise | `and` `or` `xor` `not` `shl` `shr` `ushr` |
| compare | `eq` `ne` `lt` `le` `gt` `ge` |
| control | `jmp` `jz` `jnz` `call` `ret` `halt` `nop` |
| memory | `load` `store` `gload` `gstore` `gloadx` `gstorex` `dload` |
| output | `print` `printc` `prints` |
| faults | `trap` |

Ten traps, `T01` to `T10`, each with a specified message and an exit status of 70. The
full definition is in [the spec](spec/eet-vm.md), which is short and which the test suite
literally parses — `test_spec_sync.py` reads the specification's own markdown tables and
fails if the code has drifted from the prose.

## Benchmarks

Same bytecode, same output, four runtimes:

```bash
python tools/eet.py bench
```

Best of five, wall time in milliseconds, process startup included because that is what you
actually wait for:

| | mandelbrot | life | sieve | fib | |
|---|---:|---:|---:|---:|---|
| **C++** | 9.8 | 12.3 | 4.8 | 6.4 | 1.0× |
| **C# / .NET** | 44.7 | 49.7 | 42.5 | 47.0 | 4–9× |
| **Java** | 75.8 | 123.5 | 52.6 | 78.7 | 8–12× |
| **Python** | 424.3 | 1127.4 | 70.1 | 307.7 | 15–92× |

The managed runtimes are dominated by startup on the short programs and close the gap on
the long ones — `sieve` is 9× on .NET where `life` is 4×, and that inversion is entirely
process launch. Measured on Windows 11, .NET 10, JDK 21, MSVC 14.51, CPython 3.14.

## Add your own language

Genuinely the fun part, and deliberately easy: implement the spec, give it a CLI that
takes `run <file.eetb>`, add one entry to `tools/runtimes.py`, and run `verify` until it
is green. Full instructions — including the list of things that will break first — are in
[CONTRIBUTING.md](CONTRIBUTING.md).

Rust, Go, Zig, TypeScript, OCaml and Kotlin are all conspicuously missing.

---

## The wall

Where it started, kept exactly as it was:

> *cute!*
>
> *every great journey begins with a single step!* — 05/09/2026

<details>
<summary>the original README</summary>

<img src="spingebob.png" width="360" alt="spingebob">

</details>

Built by [@EIissu](https://github.com/EIissu), [@Equilius](https://github.com/Equilius)
and vlad. Licensed under [GPL-3.0](LICENSE); see the
[code of conduct](CODE_OF_CONDUCT.md) before opening a pull request.
