package eet;

import java.nio.charset.StandardCharsets;

/**
 * A deterministic runtime fault (spec section 6).
 *
 * <p>Unchecked on purpose. A trap can be raised from anywhere inside the interpreter --
 * three helper calls deep, in the middle of an instruction -- and is handled in exactly one
 * place, {@link Vm#execute}. Declaring {@code throws} on every helper would add ceremony
 * without adding a decision anyone has to make.
 */
final class Trap extends RuntimeException {

    private static final long serialVersionUID = 1L;

    /** The trap table of spec section 6: the identifier and the exact message text. */
    enum Id {
        T01("stack underflow"),
        T02("stack overflow"),
        T03("call depth exceeded"),
        T04("division by zero"),
        T05("invalid opcode"),
        T06("local index out of range"),
        T07("global index out of range"),
        T08("data access out of range"),
        T09("jump out of range"),
        T10("trap instruction");

        private final String message;

        Id(String message) {
            this.message = message;
        }
    }

    private final Id id;
    private final int pc;

    Trap(Id id, int pc) {
        this(id, pc, id.message);
    }

    /** For {@code T10}, whose message carries the user code (spec section 6). */
    Trap(Id id, int pc, String message) {
        // A trap is control flow, not a diagnostic: nothing ever prints its stack trace, so
        // suppressing the capture makes raising one as cheap as returning.
        super(message, null, false, false);
        this.id = id;
        this.pc = pc;
    }

    /**
     * The single line this trap writes to stderr, spelled out byte for byte in section 6:
     * the program counter as eight uppercase hex digits, and one {@code 0x0A} to finish.
     *
     * <p>The newline is a literal {@code \n} rather than {@code %n}, which would expand to
     * {@code \r\n} on Windows and put this runtime one byte out from the other four.
     */
    byte[] line() {
        String line = String.format("eet: trap %s: %s at pc=%08X\n", id, getMessage(), pc);
        return line.getBytes(StandardCharsets.US_ASCII);
    }
}
