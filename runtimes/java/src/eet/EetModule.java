package eet;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;

/**
 * A loaded {@code .eetb} image and the loader that validates one (spec section 2).
 *
 * <p>Every rejection listed in section 2 is checked here, in the order the fields appear,
 * so the diagnostic always names the first thing that is wrong. Nothing downstream of
 * {@link #load} re-validates the header: the interpreter is entitled to assume that a
 * module which exists is well formed.
 */
final class EetModule {

    /**
     * The bytes are not a valid EET image.
     *
     * <p>A load error is not a trap: it is reported as {@code eet: bad binary: <reason>} and
     * exits 65, before any of the program has run (spec section 5.3).
     */
    static final class LoadException extends Exception {

        private static final long serialVersionUID = 1L;

        LoadException(String message) {
            super(message);
        }
    }

    /** Header bytes from the magic up to and including {@code code_len}. */
    private static final int HEADER_SIZE = 20;

    /** The header plus the {@code u32 data_len} that follows the code section. */
    private static final int MIN_FILE_SIZE = HEADER_SIZE + 4;

    private static final int VERSION = 1;

    private final int nglobals;
    private final int entryLocals;
    private final int entry;
    private final byte[] code;
    private final byte[] data;

    private EetModule(int nglobals, int entryLocals, int entry, byte[] code, byte[] data) {
        this.nglobals = nglobals;
        this.entryLocals = entryLocals;
        this.entry = entry;
        this.code = code;
        this.data = data;
    }

    /**
     * Parse and validate a {@code .eetb} image.
     *
     * <p>Lengths and offsets are read into {@code long} even though they end up as array
     * indices. They are declared {@code u32}, so a hostile {@code code_len} of
     * {@code 0xFFFFFFFF} lands in an {@code int} as {@code -1} and would sail through a
     * signed bounds check; widening first makes every comparison below mean what it reads
     * like.
     */
    static EetModule load(byte[] image) throws LoadException {
        if (image.length < MIN_FILE_SIZE) {
            throw new LoadException("file too short");
        }
        if (image[0] != 'E' || image[1] != 'E' || image[2] != 'T' || image[3] != 'B') {
            throw new LoadException("bad magic");
        }

        ByteBuffer header = ByteBuffer.wrap(image).order(ByteOrder.LITTLE_ENDIAN);
        int version = header.getShort(4) & 0xFFFF;
        int flags = header.getShort(6) & 0xFFFF;
        int nglobals = header.getShort(8) & 0xFFFF;
        int entryLocals = header.getShort(10) & 0xFFFF;
        long entry = header.getInt(12) & 0xFFFF_FFFFL;
        long codeLen = header.getInt(16) & 0xFFFF_FFFFL;

        if (version != VERSION) {
            throw new LoadException("unsupported version " + version);
        }
        if (flags != 0) {
            throw new LoadException(String.format("unsupported flags 0x%04X", flags));
        }
        if (entryLocals > Isa.MAX_LOCALS) {
            throw new LoadException("entry_locals out of range");
        }

        long codeEnd = HEADER_SIZE + codeLen;
        if (codeEnd > image.length) {
            throw new LoadException("code section runs past end of file");
        }
        if (entry >= codeLen) {
            throw new LoadException("entry past end of code");
        }

        if (image.length < codeEnd + 4) {
            throw new LoadException("missing data length");
        }
        long dataLen = header.getInt((int) codeEnd) & 0xFFFF_FFFFL;
        long dataStart = codeEnd + 4;
        long dataEnd = dataStart + dataLen;
        if (dataEnd > image.length) {
            throw new LoadException("data section runs past end of file");
        }
        if (dataEnd != image.length) {
            throw new LoadException("trailing bytes after data section");
        }

        return new EetModule(
                nglobals,
                entryLocals,
                (int) entry,
                Arrays.copyOfRange(image, HEADER_SIZE, (int) codeEnd),
                Arrays.copyOfRange(image, (int) dataStart, (int) dataEnd));
    }

    int nglobals() {
        return nglobals;
    }

    int entryLocals() {
        return entryLocals;
    }

    int entry() {
        return entry;
    }

    /** The immutable code section. Shared, not copied: only {@link Vm} ever sees it. */
    byte[] code() {
        return code;
    }

    /** The immutable data section. Shared, not copied, for the same reason. */
    byte[] data() {
        return data;
    }
}
