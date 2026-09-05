package eet;

import java.io.IOException;
import java.io.OutputStream;
import java.util.Arrays;

/**
 * The EET interpreter (spec section 5).
 *
 * <p>The loop is deliberately literal: fetch, bounds-check the immediates, dispatch. Where
 * Java's native behaviour already is the specified behaviour it is used directly and the
 * reason is named in a comment. It differs in exactly three places, each corrected and
 * cited below: {@code u32} branch targets, which are not {@code int}; the {@code addr + len}
 * of {@code prints}, which can overflow one; and {@code byte}, which Java signs.
 */
final class Vm {

    /** One activation record: private locals, a private operand stack, a return address. */
    private static final class Frame {

        /**
         * Frames are cheap and plentiful and almost every one uses a handful of slots, so
         * the operand stack starts small and grows to the ceiling of section 1 on demand
         * rather than costing 4KB per call.
         */
        private static final int INITIAL_STACK = 16;

        final int[] locals;
        final int returnPc;
        int[] stack = new int[INITIAL_STACK];
        int sp;

        Frame(int nlocals, int returnPc) {
            // Zero-initialised by Java, which is what sections 5.1 and 5.4 ask for.
            this.locals = new int[nlocals];
            this.returnPc = returnPc;
        }
    }

    private final byte[] code;
    private final byte[] data;
    private final int codeLen;
    private final int[] globals;
    private final OutputStream stdout;

    private final Frame[] frames = new Frame[Isa.MAX_CALL_DEPTH];
    private int depth;

    private int pc;

    /** First byte of the instruction being executed; every trap reports it (section 6). */
    private int opPc;

    private Vm(EetModule module, OutputStream stdout) {
        this.code = module.code();
        this.data = module.data();
        this.codeLen = code.length;
        this.globals = new int[module.nglobals()];
        this.stdout = stdout;
        // The entry frame's return address is never read: it is the sentinel "exit" of
        // section 5.1, and ret recognises the entry frame by the call stack emptying.
        this.frames[0] = new Frame(module.entryLocals(), -1);
        this.depth = 1;
        this.pc = module.entry();
    }

    /**
     * Run {@code module} to completion and return the process exit status (section 5.3).
     *
     * <p>The trap protocol lives here because its ordering is observable: whatever the
     * program already wrote must reach stdout before the trap line reaches stderr.
     */
    static int execute(EetModule module, OutputStream stdout, OutputStream stderr)
            throws IOException {
        Vm vm = new Vm(module, stdout);
        try {
            int status = vm.run();
            stdout.flush();
            return status;
        } catch (Trap trap) {
            stdout.flush();
            stderr.write(trap.line());
            stderr.flush();
            return Isa.EXIT_TRAP;
        }
    }

    private int run() throws IOException {
        while (true) {
            if (pc >= codeLen) {
                // Falling off the end of the code section (section 5.4). No instruction
                // started here, so the address reported is the fall-through pc itself.
                throw new Trap(Trap.Id.T09, pc);
            }
            opPc = pc;
            Isa.Opcode op = Isa.decode(code[pc]);
            pc++;
            if (op == null) {
                throw trap(Trap.Id.T05);
            }
            // One check covers the whole instruction: reading any immediate past the end of
            // code[] is T05 (sections 3 and 5.2). Every case below then reads its own
            // immediates knowing they are there.
            if (pc + op.immediateBytes() > codeLen) {
                throw trap(Trap.Id.T05);
            }

            switch (op) {
                // -- 4.1 stack and control
                case HALT -> {
                    return Isa.EXIT_OK;
                }
                case NOP -> {
                }
                case PUSH -> push(i32());
                case POP -> pop();
                case DUP -> {
                    int a = pop();
                    push(a);
                    push(a);
                }
                case SWAP -> {
                    int b = pop();
                    int a = pop();
                    push(b);
                    push(a);
                }
                case OVER -> {
                    int b = pop();
                    int a = pop();
                    push(a);
                    push(b);
                    push(a);
                }
                case ROT -> {
                    int c = pop();
                    int b = pop();
                    int a = pop();
                    push(b);
                    push(c);
                    push(a);
                }

                // -- 4.2 arithmetic and bitwise, 4.3 comparison
                case NEG -> push(-pop());
                case NOT -> push(~pop());
                case ADD, SUB, MUL, DIV, MOD, AND, OR, XOR, SHL, SHR, USHR,
                        EQ, NE, LT, LE, GT, GE -> {
                    // The top of the stack is the right-hand operand: a b -> a op b.
                    int b = pop();
                    int a = pop();
                    push(apply(op, a, b));
                }

                // -- 4.4 branching and calls
                case JMP -> jump(u32());
                case JZ -> {
                    long target = u32();
                    if (pop() == 0) {
                        jump(target);
                    }
                }
                case JNZ -> {
                    long target = u32();
                    if (pop() != 0) {
                        jump(target);
                    }
                }
                case CALL -> call();
                case RET -> {
                    int value = pop();
                    Frame finished = frames[--depth];
                    if (depth == 0) {
                        // Returning from the entry frame ends the program (section 5.3).
                        return value & 0xFF;
                    }
                    pc = finished.returnPc;
                    push(value);
                }

                // -- 4.5 memory
                case LOAD -> {
                    int idx = u8();
                    int[] locals = frame().locals;
                    if (idx >= locals.length) {
                        throw trap(Trap.Id.T06);
                    }
                    push(locals[idx]);
                }
                case STORE -> {
                    int idx = u8();
                    int[] locals = frame().locals;
                    // The index is validated before the value is popped, so a store that is
                    // both out of range and starved reports T06 and not T01.
                    if (idx >= locals.length) {
                        throw trap(Trap.Id.T06);
                    }
                    locals[idx] = pop();
                }
                case GLOAD -> {
                    int idx = u16();
                    if (idx >= globals.length) {
                        throw trap(Trap.Id.T07);
                    }
                    push(globals[idx]);
                }
                case GSTORE -> {
                    int idx = u16();
                    if (idx >= globals.length) {
                        throw trap(Trap.Id.T07);
                    }
                    globals[idx] = pop();
                }
                case DLOAD -> {
                    int addr = pop();
                    if (addr < 0 || addr >= data.length) {
                        throw trap(Trap.Id.T08);
                    }
                    // Java's byte is signed; section 4.5 wants the value zero-extended into
                    // 0..255, so a bare data[addr] would be wrong for every byte over 127.
                    push(data[addr] & 0xFF);
                }
                case GLOADX -> {
                    int idx = pop();
                    if (idx < 0 || idx >= globals.length) {
                        throw trap(Trap.Id.T07);
                    }
                    push(globals[idx]);
                }
                case GSTOREX -> {
                    // The index sits on top of the value: v idx -> (section 4.5).
                    int idx = pop();
                    int value = pop();
                    if (idx < 0 || idx >= globals.length) {
                        throw trap(Trap.Id.T07);
                    }
                    globals[idx] = value;
                }

                // -- 4.6 output
                case PRINT -> stdout.write(Ops.decimal(pop()));
                case PRINTC -> stdout.write(pop() & 0xFF);
                case PRINTS -> {
                    int len = pop();
                    int addr = pop();
                    // addr + len is widened before the comparison: both are i32 and their
                    // sum can overflow into a negative int, which would slip past the range
                    // check and hand a bad slice to write() (section 4.6).
                    if (len < 0 || addr < 0 || (long) addr + len > data.length) {
                        throw trap(Trap.Id.T08);
                    }
                    stdout.write(data, addr, len);
                }

                // -- 4.7 diagnostics
                case TRAP -> throw new Trap(
                        Trap.Id.T10, opPc, "trap instruction (code=" + u8() + ")");

                // Isa.Opcode is the single source of truth for what exists; this fires only
                // if a constant is added there and no case is added here.
                default -> throw new AssertionError("unhandled opcode " + op);
            }
        }
    }

    /**
     * The binary operators of sections 4.2 and 4.3, as a table of pure functions.
     *
     * <p>Every one of them is a bare Java operator. That is not laziness: {@link Ops}
     * records the JLS clause behind each, covering the wrapping, the truncation toward
     * zero, the five-bit shift mask and the signedness of the comparisons.
     */
    private int apply(Isa.Opcode op, int a, int b) {
        return switch (op) {
            case ADD -> a + b;
            case SUB -> a - b;
            case MUL -> a * b;
            case DIV -> {
                if (b == 0) {
                    throw trap(Trap.Id.T04);
                }
                yield Ops.div(a, b);
            }
            case MOD -> {
                if (b == 0) {
                    throw trap(Trap.Id.T04);
                }
                yield Ops.mod(a, b);
            }
            case AND -> a & b;
            case OR -> a | b;
            case XOR -> a ^ b;
            case SHL -> a << b;
            case SHR -> a >> b;
            case USHR -> a >>> b;
            case EQ -> a == b ? 1 : 0;
            case NE -> a != b ? 1 : 0;
            case LT -> a < b ? 1 : 0;
            case LE -> a <= b ? 1 : 0;
            case GT -> a > b ? 1 : 0;
            case GE -> a >= b ? 1 : 0;
            // Guards the case-label group in run() against drifting out of step with this
            // table; no opcode outside that group can reach here.
            default -> throw new AssertionError("not a binary operator: " + op);
        };
    }

    /** {@code call target, nargs, nlocals} (spec section 5.4). */
    private void call() {
        long target = u32();
        int nargs = u8();
        int nlocals = u8();

        if (nargs > nlocals) {
            throw trap(Trap.Id.T06);
        }
        if (depth == Isa.MAX_CALL_DEPTH) {
            throw trap(Trap.Id.T03);
        }

        // The return address is the pc after the whole instruction, immediates included.
        Frame callee = new Frame(nlocals, pc);
        // Popped back to front, so the first value the caller pushed becomes locals[0].
        for (int i = nargs - 1; i >= 0; i--) {
            callee.locals[i] = pop();
        }
        if (target >= codeLen) {
            throw trap(Trap.Id.T09);
        }

        frames[depth++] = callee;
        pc = (int) target;
    }

    /**
     * Take a branch, checked at the moment it is taken (section 4.4).
     *
     * <p>The target arrives as a {@code long} because it is encoded {@code u32}: a target at
     * or above 2^31 would be a negative {@code int}, would pass a signed range check, and
     * would then index outside {@code code[]} instead of raising {@code T09}.
     */
    private void jump(long target) {
        if (target >= codeLen) {
            throw trap(Trap.Id.T09);
        }
        pc = (int) target;
    }

    private Frame frame() {
        return frames[depth - 1];
    }

    private void push(int value) {
        Frame frame = frame();
        if (frame.sp == frame.stack.length) {
            grow(frame);
        }
        frame.stack[frame.sp++] = value;
    }

    private int pop() {
        Frame frame = frame();
        if (frame.sp == 0) {
            throw trap(Trap.Id.T01);
        }
        return frame.stack[--frame.sp];
    }

    private void grow(Frame frame) {
        if (frame.sp == Isa.MAX_OPERAND_STACK) {
            throw trap(Trap.Id.T02);
        }
        frame.stack = Arrays.copyOf(
                frame.stack, Math.min(frame.stack.length * 2, Isa.MAX_OPERAND_STACK));
    }

    // -- immediate decoding. All multi-byte integers are little-endian (section 2).

    private int u8() {
        return code[pc++] & 0xFF;
    }

    private int u16() {
        int value = (code[pc] & 0xFF) | ((code[pc + 1] & 0xFF) << 8);
        pc += 2;
        return value;
    }

    private int i32() {
        int value = (code[pc] & 0xFF)
                | ((code[pc + 1] & 0xFF) << 8)
                | ((code[pc + 2] & 0xFF) << 16)
                | ((code[pc + 3] & 0xFF) << 24);
        pc += 4;
        return value;
    }

    /** The same four bytes as {@link #i32()}, read unsigned; see {@link #jump}. */
    private long u32() {
        return i32() & 0xFFFF_FFFFL;
    }

    private Trap trap(Trap.Id id) {
        return new Trap(id, opPc);
    }
}
