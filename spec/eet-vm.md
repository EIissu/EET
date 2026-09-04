# The EET Virtual Machine — Specification v1

> **Status:** Frozen. Every runtime in `runtimes/` implements *this document*, not each other.
> If a runtime disagrees with the spec, the runtime is wrong. If the spec is ambiguous, the
> spec is wrong — file an issue, because an ambiguity here is a bug in all five runtimes at once.

EET is a small stack machine. It is deliberately boring: no floats, no garbage collector, no
undefined behaviour. Every observable action is pinned down to the byte, so that five
independent implementations — in Python, Java, C#, C++ and .NET — produce *identical* output
for identical input. That property is the entire point of this repository.

---

## 1. Machine model

An EET machine has:

| Component | Description |
|---|---|
| **Code** | Immutable byte array. Addressed by a program counter `pc` (byte offset from 0). |
| **Data** | Immutable byte array, loaded from the binary. Read-only. |
| **Globals** | Mutable array of `nglobals` 32-bit signed integers, all zero at startup. |
| **Call stack** | Stack of *frames*. Max depth **256** (§6, `T03`). |
| **Frame** | Locals, an operand stack, and a return address. |
| **Locals** | Per frame. `nlocals` slots of `i32`, all zero except incoming arguments. Max 256 slots. |
| **Operand stack** | Per frame. Holds `i32`. Max depth **1024** (§6, `T02`). |
| **Output** | Two independent byte streams, `stdout` and `stderr`. |

### 1.1 The value type

The one and only value type is `i32`: a **32-bit two's-complement signed integer**.

All arithmetic **wraps** on overflow. There is no trapping on overflow, no saturation, and no
promotion to a wider type. `2147483647 + 1` is `-2147483648`, in every runtime, always.

> **Implementation note.** This is the single most common source of cross-language divergence.
> C# and C++ need explicit `unchecked` / unsigned-intermediate handling; Python needs explicit
> masking because its integers are arbitrary-precision; Java's `int` already does the right
> thing. See §7 for the exact reference formulas.

### 1.2 Per-frame operand stacks

Each frame owns a **private** operand stack. A function cannot see or corrupt its caller's
stack. Arguments cross the boundary only through `call`, and results only through `ret`
(§5.4). This makes stack underflow deterministic and frame-local.

---

## 2. Binary container format (`.eetb`)

All multi-byte integers are **little-endian**. `u16` and `u32` are unsigned; `i32` is signed
two's-complement.

```
offset  size      field         value / meaning
------  --------  ------------  --------------------------------------------
0       4         magic         the ASCII bytes  45 45 54 42  ("EETB")
4       2  u16    version       1
6       2  u16    flags         0 (reserved; a loader MUST reject non-zero)
8       2  u16    nglobals      number of global slots, 0..65535
10      2  u16    entry_locals  local slots for the entry frame, 0..256
12      4  u32    entry         byte offset into code[] of the entry point
16      4  u32    code_len      length of code[] in bytes
20      code_len  code          the instruction stream
--      4  u32    data_len      length of data[] in bytes
--      data_len  data          the read-only data section
```

A loader **MUST** reject a file that:

* is shorter than 24 bytes, or
* has the wrong magic, or
* has `version` other than 1, or
* has `flags` other than 0, or
* has `entry >= code_len`, or
* has `entry_locals > 256`, or
* declares a `code_len` or `data_len` that runs past the end of the file, or
* has trailing bytes after `data`.

Rejection is a **load error**, not a trap: print `eet: bad binary: <reason>` to stderr and exit
with status **65**.

---

## 3. Instruction encoding

An instruction is a 1-byte opcode followed by zero or more immediate operands, packed with no
alignment and no padding. Operand widths are fixed per opcode.

Reading an opcode or an operand that would run past the end of `code[]` is trap `T05`.

---

## 4. Instruction set

Stack effects are written `before -> after`, with the **top of stack on the right**.

### 4.1 Stack and control

| Op | Mnemonic | Operands | Effect | Notes |
|---|---|---|---|---|
| `0x00` | `halt`  | — | — | Stop. Exit status 0. |
| `0x01` | `nop`   | — | — | Do nothing. |
| `0x02` | `push`  | `i32` | `-> v` | Push the immediate. |
| `0x03` | `pop`   | — | `v ->` | Discard the top. |
| `0x04` | `dup`   | — | `a -> a a` | |
| `0x05` | `swap`  | — | `a b -> b a` | |
| `0x06` | `over`  | — | `a b -> a b a` | Copy the *second* item to the top. |
| `0x07` | `rot`   | — | `a b c -> b c a` | Rotate the top three left. |

### 4.2 Arithmetic and bitwise

All pop their operands and push exactly one result. For binary ops, `a` is the deeper value:
`a b -> result`. So `push 10`, `push 3`, `sub` yields `7`.

| Op | Mnemonic | Effect | Notes |
|---|---|---|---|
| `0x10` | `add`  | `a b -> a + b`  | wrapping |
| `0x11` | `sub`  | `a b -> a - b`  | wrapping |
| `0x12` | `mul`  | `a b -> a * b`  | wrapping |
| `0x13` | `div`  | `a b -> a / b`  | truncates **toward zero**; zero divisor is `T04`; see §7.2 |
| `0x14` | `mod`  | `a b -> a % b`  | sign follows the **dividend**; zero divisor is `T04`; see §7.2 |
| `0x15` | `neg`  | `a -> -a`       | wrapping: `neg(-2147483648)` is `-2147483648` |
| `0x16` | `and`  | `a b -> a AND b` | bitwise |
| `0x17` | `or`   | `a b -> a OR b`  | bitwise |
| `0x18` | `xor`  | `a b -> a XOR b` | bitwise |
| `0x19` | `not`  | `a -> NOT a`     | bitwise complement |
| `0x1A` | `shl`  | `a b -> a << (b AND 31)` | |
| `0x1B` | `shr`  | `a b -> a >> (b AND 31)` | **arithmetic**, sign-propagating |
| `0x1C` | `ushr` | `a b -> a >>> (b AND 31)` | **logical**, zero-filling |

The shift count is always masked to its low 5 bits, so shifts never invoke
implementation-defined behaviour. See §7.3.

### 4.3 Comparison

Each pops two values and pushes `1` for true or `0` for false. All comparisons are **signed**.

| Op | Mnemonic | Effect |
|---|---|---|
| `0x20` | `eq` | `a b -> (a == b)` |
| `0x21` | `ne` | `a b -> (a != b)` |
| `0x22` | `lt` | `a b -> (a < b)` |
| `0x23` | `le` | `a b -> (a <= b)` |
| `0x24` | `gt` | `a b -> (a > b)` |
| `0x25` | `ge` | `a b -> (a >= b)` |

### 4.4 Branching and calls

| Op | Mnemonic | Operands | Effect |
|---|---|---|---|
| `0x30` | `jmp`  | `u32 target` | `pc = target` |
| `0x31` | `jz`   | `u32 target` | pop `v`; if `v == 0` then `pc = target` |
| `0x32` | `jnz`  | `u32 target` | pop `v`; if `v != 0` then `pc = target` |
| `0x33` | `call` | `u32 target`, `u8 nargs`, `u8 nlocals` | see §5.4 |
| `0x34` | `ret`  | — | see §5.4 |

A branch target at or past `code_len` is trap `T09`. The target is checked **when the branch
is taken**, not when it is decoded.

### 4.5 Memory

| Op | Mnemonic | Operands | Effect | Trap |
|---|---|---|---|---|
| `0x40` | `load`   | `u8 idx`  | `-> locals[idx]` | `T06` if `idx >= nlocals` |
| `0x41` | `store`  | `u8 idx`  | `v ->`, `locals[idx] = v` | `T06` if `idx >= nlocals` |
| `0x42` | `gload`  | `u16 idx` | `-> globals[idx]` | `T07` if `idx >= nglobals` |
| `0x43` | `gstore` | `u16 idx` | `v ->`, `globals[idx] = v` | `T07` if `idx >= nglobals` |
| `0x44` | `dload`  | — | `addr -> data[addr]` | `T08` if `addr < 0` or `addr >= data_len` |
| `0x45` | `gloadx`  | — | `idx -> globals[idx]` | `T07` if `idx < 0` or `idx >= nglobals` |
| `0x46` | `gstorex` | — | `v idx ->`, `globals[idx] = v` | `T07` if `idx < 0` or `idx >= nglobals` |

`dload` zero-extends: the pushed value is in `0..255`.

`gloadx` and `gstorex` are the computed-index forms, which is what makes the globals array
usable as a heap. Note the operand order of `gstorex`: the index is on **top**, so the idiom
is `push value`, `push index`, `gstorex`.

### 4.6 Output

Output is a **byte stream**, never a character stream. There is no encoding layer, no locale,
and no line-ending translation. A runtime **MUST** write to `stdout` in binary mode. On
Windows this means explicitly disabling CRLF translation.

| Op | Mnemonic | Effect |
|---|---|---|
| `0x50` | `print`  | pop `v`; write the decimal text of `v` (§7.4) |
| `0x51` | `printc` | pop `v`; write the single byte `v AND 0xFF` |
| `0x52` | `prints` | pop `len`, pop `addr`; write `data[addr .. addr+len)` verbatim |

`prints` traps `T08` if `len < 0`, `addr < 0`, or `addr + len > data_len`. Note the operand
order: the stack is `addr len ->`, so `len` is on top.

> **This bounds check must not overflow.** `addr` and `len` are `i32`, so computing
> `addr + len` in 32 bits can wrap to a negative number and sail straight through a naive
> comparison — turning a bounds check into an out-of-bounds read. Compare without
> overflowing: check `addr < 0`, then `len < 0`, then `len > data_len - addr`. A runtime
> that gets this wrong passes every well-behaved program and fails only on a hostile one,
> which is the worst way for a bug to behave. `programs/trap_span.eet` exists to catch it.

`print` never emits a newline, a sign for non-negative values, or padding. Exactly the bytes
described in §7.4 and nothing else.

### 4.7 Diagnostics

| Op | Mnemonic | Operands | Effect |
|---|---|---|---|
| `0x60` | `trap` | `u8 code` | Raise trap `T10` carrying the user code. Always fails. |

Any opcode byte not listed in this section is trap `T05`.

---

## 5. Execution

### 5.1 Startup

1. Load and validate the binary (§2).
2. Allocate `nglobals` globals, all `0`.
3. Push a single frame with `entry_locals` local slots, all `0`, and an empty operand stack.
   Its return address is the sentinel "exit".
4. Set `pc = entry`.
5. Run §5.2 until termination.

### 5.2 The cycle

```
loop:
    if pc >= code_len:  trap T09
    op = code[pc]; pc += 1
    decode immediates, advancing pc; if that runs past code_len: trap T05
    execute op
```

The `pc` is advanced past the whole instruction *before* the instruction executes. Branches
therefore assign an absolute target and are unaffected by instruction length.

### 5.3 Termination

| Cause | stdout | stderr | Exit status |
|---|---|---|---|
| `halt` | as produced | as produced | **0** |
| `ret` from the entry frame | as produced | as produced | the returned value `AND 0xFF` |
| any trap | as produced, flushed | trap line (§6) | **70** |
| load error | — | `eet: bad binary: ...` | **65** |

Whatever has already been written to stdout **MUST** be flushed before the process exits,
including on a trap. A trap does not discard prior output.

### 5.4 Calling convention

`call target, nargs, nlocals`:

1. If `nargs > nlocals`, the assembler rejects it as a static error. A loader need not check,
   but the behaviour is still pinned: treat it as trap `T06`.
2. If the call stack already holds 256 frames, trap `T03`.
3. Pop `nargs` values from the **caller's** operand stack. They land in the callee's locals in
   source order: the first value pushed becomes `locals[0]`, the last becomes
   `locals[nargs-1]`. Equivalently, pop into `locals[nargs-1]` down to `locals[0]`. Underflow
   is `T01`.
4. Create a frame with `nlocals` slots; slots `nargs` through `nlocals-1` are `0`.
5. Record the return address, which is the `pc` immediately after the `call` instruction.
6. `pc = target`. A target at or past `code_len` is `T09`.

`ret`:

1. Pop one value `v` from the **current** frame's operand stack. Underflow is `T01`.
2. Discard the frame.
3. If that was the entry frame, terminate with exit status `v AND 0xFF` (§5.3).
4. Otherwise restore the caller's `pc` and push `v` onto the **caller's** operand stack.
   Overflow is `T02`.

Every function must end with `ret`; there is no implicit return. Falling off the end of the
code section is `T09`.

---

## 6. Traps

A trap terminates the program immediately with exit status **70** after writing exactly one
line to **stderr**:

```
eet: trap <ID>: <message> at pc=<PC>
```

`<PC>` is the address of the **first byte of the instruction that trapped**, formatted as
**exactly 8 uppercase hexadecimal digits**, zero-padded. The line ends with a single `\n`
(byte `0x0A`), never `\r\n`.

Two cases have no trapping instruction to point at, so they are pinned explicitly:

* **Falling off the end of the code section** (§5.2) reports the out-of-range `pc` itself —
  that is, `code_len` or whatever larger value the previous branch left behind.
* **A taken branch to an out-of-range target** reports the address of the *branch
  instruction*, not the target, because the branch is what went wrong.

Example: `eet: trap T04: division by zero at pc=0000001A`

| ID | Message | Raised when |
|---|---|---|
| `T01` | `stack underflow` | popping from an empty operand stack |
| `T02` | `stack overflow` | pushing onto a 1024-deep operand stack |
| `T03` | `call depth exceeded` | `call` at 256 frames |
| `T04` | `division by zero` | `div` or `mod` with a zero divisor |
| `T05` | `invalid opcode` | unknown opcode, or immediates run past the code end |
| `T06` | `local index out of range` | bad `load`/`store` index, or `nargs > nlocals` |
| `T07` | `global index out of range` | bad `gload`/`gstore` index |
| `T08` | `data access out of range` | bad `dload` or `prints` range |
| `T09` | `jump out of range` | branch or fall-through past the code end |
| `T10` | `trap instruction` | the `trap` instruction executed |

For `T10` the message is `trap instruction (code=<n>)` with `<n>` in decimal, for example
`eet: trap T10: trap instruction (code=7) at pc=00000042`.

---

## 7. Reference semantics

These are the exact formulas. Where a host language differs, the runtime must correct for it.

Let `wrap(x)` map an arbitrary integer to `i32`:

```
wrap(x) = ((x + 2^31) mod 2^32) - 2^31
```

### 7.1 Wrapping arithmetic

```
add(a,b) = wrap(a + b)      sub(a,b) = wrap(a - b)
mul(a,b) = wrap(a * b)      neg(a)   = wrap(-a)
```

`mul` must be computed as if in infinite precision and then wrapped. In C++ that means doing
the multiply in `uint32_t`, where overflow is defined, and reinterpreting the result; in C# it
means `unchecked`; in Python it means masking.

### 7.2 Division and remainder

`div` truncates toward zero and `mod` takes the sign of the dividend — C, C# and Java
semantics, **not** Python's floor semantics:

```
 7 /  2 ==  3        7 %  2 ==  1
-7 /  2 == -3       -7 %  2 == -1
 7 / -2 == -3        7 % -2 ==  1
-7 / -2 ==  3       -7 % -2 == -1
```

The identity `(a/b)*b + (a%b) == a` holds for every nonzero `b`.

**The overflow case.** `-2147483648 / -1` overflows `i32`. It is **not** a trap: the result
wraps to `-2147483648`. Correspondingly `-2147483648 % -1` is `0`. A C++ runtime must
special-case this, because the raw hardware instruction faults.

Python implementations must not use `//` or `%` directly; see `runtimes/python/eetvm/ops.py`.

### 7.3 Shifts

```
shl(a,b)  = wrap(a * 2^(b AND 31))
shr(a,b)  = floor(a / 2^(b AND 31))              -- arithmetic, sign-propagating
ushr(a,b) = wrap( (a as u32) >> (b AND 31) )     -- logical, zero-filling
```

`ushr(-1, 0)` is `-1`, because the mask makes the shift count zero and no zero-fill occurs.

### 7.4 Decimal formatting for `print`

The bytes written are the shortest ASCII decimal representation of the signed value:

* a leading `-` (byte `0x2D`) if and only if the value is negative,
* then the decimal digits of the magnitude with no leading zeros,
* except that zero is written as the single byte `0`.

`-2147483648` is written as `-2147483648`. Implementations that negate before formatting must
handle this without overflowing.

---

## 8. Conformance

A runtime is conformant when, for every program in `programs/`, it produces byte-identical
`stdout`, byte-identical `stderr` and an identical exit status to the golden files in
`tests/conformance/golden/`. Run the whole matrix with:

```
python tools/eet.py verify
```

The goldens are generated from the Python runtime, which is the **reference implementation** —
but the reference is not the authority. This document is. A disagreement between the reference
and the spec is a bug in the reference.

---

## 9. Reserved for v2

Not in v1, listed so nobody accidentally squats the encoding: opcodes `0x70` through `0x7F` are
reserved for floating point, `0x80` through `0x8F` for a heap, `0x90` through `0x9F` for host
calls, and the `flags` header word for feature bits.
