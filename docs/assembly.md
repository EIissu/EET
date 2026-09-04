# Writing EET assembly

This is the tutorial. [`spec/eet-vm.md`](../spec/eet-vm.md) is the law — when the two
disagree, the spec wins.

An EET program is a text file with the extension `.eet`. Assemble it into a `.eetb` module
and run it:

```
python tools/eet.py asm                       # assembles everything in programs/
python tools/eet.py run programs/hello.eet    # assembles one and runs it
```

---

## Hello

```asm
.data greeting "Hello from EET!\n"

.func main 0 0
    pushs greeting
    prints
    push 0
    ret
.end
```

Four things are happening:

* `.data greeting "..."` puts those bytes in the read-only data section and defines two
  symbols: `greeting` (its address) and `greeting.len` (its length).
* `.func main 0 0` opens a function taking 0 arguments with 0 local slots. Execution
  starts at `main` unless a `.entry` directive says otherwise.
* `pushs greeting` is sugar for `push greeting` followed by `push greeting.len` — exactly
  the address/length pair that `prints` consumes.
* `ret` from the entry function ends the program, and the value it returns becomes the
  process exit status (masked to a byte).

## The shape of a line

```
label:  mnemonic  operand, operand    ; comment
```

Everything is optional. Comments start with `;` or `#` and run to end of line. Labels sit
at the left margin by convention and are **local to their function**, so every function
can have its own `loop:`.

## Directives

| Directive | Meaning |
|---|---|
| `.globals N` | Allocate `N` global slots. Must appear at the top level. |
| `.data NAME items...` | Append bytes to the data section and define `NAME` and `NAME.len`. Items are quoted strings or byte values, comma separated. |
| `.func NAME nargs nlocals` | Open a function. `nlocals` counts *all* slots, arguments included, so `nargs` may not exceed it. |
| `.end` | Close the current function. |
| `.entry NAME` | Use `NAME` as the entry point instead of `main`. |

## Literals

Decimal, hex, binary and octal all work, with `_` allowed as a separator, and a leading
sign is part of the literal:

```asm
push 255
push 0xFF
push 0b1111_1111
push 0o377
push -2147483648
push 'A'            ; character literals are one byte
```

String and character escapes: `\n` `\r` `\t` `\0` `\e` `\\` `\"` `\'` and `\xNN`.

## Locals, globals and data

Locals are per-call scratch space, addressed by an immediate index:

```asm
.func main 0 2
    push 10
    store 0         ; local 0 = 10
    load 0
    print
```

Globals come in two flavours. `gload`/`gstore` take an immediate index, which is what you
want for named variables. `gloadx`/`gstorex` take the index **from the stack**, which is
what turns the globals array into a heap:

```asm
.globals 512

    push 1
    push 37
    gstorex         ; globals[37] = 1   (value first, then index)

    push 37
    gloadx          ; -> globals[37]
```

The data section is read only. `dload` pops an address and pushes that byte, zero
extended, which is how [`banner.eet`](../programs/banner.eet) reads its bitmap font.

## Control flow

`jmp`, `jz` and `jnz` take a label. `jz` and `jnz` pop the value they test.

```asm
.func main 0 1
    push 5
    store 0
loop:
    load 0
    jz done         ; pops; jumps when the counter reaches zero
    load 0
    print
    push 10
    printc
    load 0
    push 1
    sub
    store 0
    jmp loop
done:
    push 0
    ret
.end
```

## Functions

Call by name and the assembler fills in the target, argument count and frame size from the
`.func` header:

```asm
.func add2 2 2
    load 0
    load 1
    add
    ret
.end

.func main 0 0
    push 1
    push 2
    call add2       ; pops two arguments, pushes one result
    print
    push 0
    ret
.end
```

Arguments arrive in **source order**: the first value pushed becomes `locals[0]`. Every
function returns exactly one value, so a call you do not care about is followed by `pop`.

Each frame has a **private operand stack**. A callee cannot reach into its caller's
values; trying to pop past the bottom of your own frame is trap `T01`, not a peek at
someone else's data.

## Output

There is no string type and no formatting layer. Three instructions write bytes:

| | |
|---|---|
| `print` | pop a value, write its decimal digits — no newline, no padding, no sign unless negative |
| `printc` | pop a value, write its low byte |
| `prints` | pop a length and an address, write that slice of the data section verbatim |

A newline is `push 10` then `printc`, or part of a `.data` string.

## Errors the assembler catches for you

The assembler refuses several things that would otherwise become a run-time trap in four
languages at once:

* a local index beyond the enclosing function's `nlocals`,
* a global index beyond `.globals`,
* `nargs` greater than `nlocals`,
* an immediate that does not fit its operand width,
* an undefined label, function or data symbol,
* a duplicate label within a function.

Errors are reported as `file:line: message`.

## Reading the output

When something behaves unexpectedly, disassemble it:

```
python -m eetvm dis build/programs/fib.eetb
```

You get one line per instruction with its address, its raw bytes and the decoded form,
plus a hexdump of the data section — which is usually enough to see whether the bug is in
your program or in a runtime.
