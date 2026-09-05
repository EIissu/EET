package eet;

/**
 * The instruction set, the machine limits and the exit statuses, as data.
 *
 * <p>This is the Java twin of {@code runtimes/python/eetvm/isa.py}: it describes the shape
 * of the machine (spec sections 1, 3 and 4) and executes none of it. The arithmetic lives
 * in {@link Ops} and the interpreter in {@link Vm}.
 */
final class Isa {

    private Isa() {
    }

    // -- machine limits (spec section 1) ------------------------------------------------

    static final int MAX_OPERAND_STACK = 1024;
    static final int MAX_CALL_DEPTH = 256;
    static final int MAX_LOCALS = 256;

    // -- exit statuses (spec section 5.3) -----------------------------------------------

    static final int EXIT_OK = 0;
    static final int EXIT_LOAD_ERROR = 65;
    static final int EXIT_TRAP = 70;

    /** The width in bytes of one immediate operand (spec section 3). */
    enum Operand {
        U8(1),
        U16(2),
        U32(4),
        I32(4);

        private final int size;

        Operand(int size) {
            this.size = size;
        }
    }

    /**
     * Every opcode in v1, with the immediates that follow it (spec section 4).
     *
     * <p>Immediates are packed with no alignment and no padding, so the declared operands
     * are all the interpreter needs to know how long an instruction is.
     */
    enum Opcode {
        // 4.1 stack and control
        HALT(0x00),
        NOP(0x01),
        PUSH(0x02, Operand.I32),
        POP(0x03),
        DUP(0x04),
        SWAP(0x05),
        OVER(0x06),
        ROT(0x07),
        // 4.2 arithmetic and bitwise
        ADD(0x10),
        SUB(0x11),
        MUL(0x12),
        DIV(0x13),
        MOD(0x14),
        NEG(0x15),
        AND(0x16),
        OR(0x17),
        XOR(0x18),
        NOT(0x19),
        SHL(0x1A),
        SHR(0x1B),
        USHR(0x1C),
        // 4.3 comparison
        EQ(0x20),
        NE(0x21),
        LT(0x22),
        LE(0x23),
        GT(0x24),
        GE(0x25),
        // 4.4 branching and calls
        JMP(0x30, Operand.U32),
        JZ(0x31, Operand.U32),
        JNZ(0x32, Operand.U32),
        CALL(0x33, Operand.U32, Operand.U8, Operand.U8),
        RET(0x34),
        // 4.5 memory
        LOAD(0x40, Operand.U8),
        STORE(0x41, Operand.U8),
        GLOAD(0x42, Operand.U16),
        GSTORE(0x43, Operand.U16),
        DLOAD(0x44),
        GLOADX(0x45),
        GSTOREX(0x46),
        // 4.6 output
        PRINT(0x50),
        PRINTC(0x51),
        PRINTS(0x52),
        // 4.7 diagnostics
        TRAP(0x60, Operand.U8);

        private final int encoding;
        private final int immediateBytes;

        Opcode(int encoding, Operand... operands) {
            int bytes = 0;
            for (Operand operand : operands) {
                bytes += operand.size;
            }
            this.encoding = encoding;
            this.immediateBytes = bytes;
        }

        /** Total size of this instruction's immediates; the opcode byte is not counted. */
        int immediateBytes() {
            return immediateBytes;
        }
    }

    private static final Opcode[] BY_ENCODING = new Opcode[256];

    static {
        for (Opcode op : Opcode.values()) {
            BY_ENCODING[op.encoding] = op;
        }
    }

    /**
     * The opcode encoded by {@code b}, or {@code null} when the byte is not assigned in v1.
     *
     * <p>Null rather than an {@code Optional} because this is the innermost step of the
     * interpreter loop and the one caller turns the miss straight into trap {@code T05}
     * (spec section 4.7).
     */
    static Opcode decode(byte b) {
        return BY_ENCODING[b & 0xFF];
    }
}
