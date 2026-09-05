package eet;

import java.io.BufferedOutputStream;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.Path;

/**
 * The command line: {@code eet run <program.eetb>}, and nothing else.
 *
 * <p>Its whole job is to turn the three outcomes of spec section 5.3 into process state --
 * the exit status, the bytes on stdout, the one line on stderr -- and to print not a single
 * byte the spec does not call for.
 */
public final class Main {

    /** EX_USAGE, in the same sysexits family as the 65 and 70 of section 5.3. */
    private static final int EXIT_USAGE = 64;

    /** EX_IOERR. The spec has nothing to say about the host's own streams failing. */
    private static final int EXIT_IO_ERROR = 74;

    private Main() {
    }

    public static void main(String[] args) {
        System.exit(run(args));
    }

    private static int run(String[] args) {
        // stderr is opened through the file descriptor rather than System.err for the same
        // reason as stdout below, and because a diagnostic must not depend on the console
        // encoding to end in exactly one 0x0A.
        OutputStream stderr = new FileOutputStream(FileDescriptor.err);

        if (args.length != 2 || !args[0].equals("run")) {
            return fail(stderr, "eet: usage: eet run <program.eetb>", EXIT_USAGE);
        }

        byte[] image;
        try {
            image = Files.readAllBytes(Path.of(args[1]));
        } catch (IOException | InvalidPathException unreadable) {
            return fail(stderr, "eet: cannot read " + args[1], Isa.EXIT_LOAD_ERROR);
        }

        EetModule module;
        try {
            module = EetModule.load(image);
        } catch (EetModule.LoadException rejected) {
            return fail(stderr, "eet: bad binary: " + rejected.getMessage(),
                    Isa.EXIT_LOAD_ERROR);
        } catch (RuntimeException unexpected) {
            // Defensive. The loader is written to reject every malformed image by hand, so
            // nothing should arrive here -- but an image that found a hole in it must still
            // leave as status 65 rather than as a stack trace on stderr.
            return fail(stderr, "eet: bad binary: " + unexpected, Isa.EXIT_LOAD_ERROR);
        }

        // Section 4.6: stdout is a raw byte stream with no encoding layer and no line-ending
        // translation. System.out is a PrintStream with a charset and, on a Windows console,
        // opinions about newlines, so the descriptor is wrapped directly instead. The stream
        // is flushed but never closed: closing it would close file descriptor 1.
        OutputStream stdout =
                new BufferedOutputStream(new FileOutputStream(FileDescriptor.out), 1 << 16);
        try {
            return Vm.execute(module, stdout, stderr);
        } catch (IOException broken) {
            // A closed pipe or a full disk, not a fault in the program being run. It is
            // reported by the status alone: section 5.3 allows exactly two things on
            // stderr, and neither of them is this.
            return EXIT_IO_ERROR;
        }
    }

    private static int fail(OutputStream stderr, String message, int status) {
        try {
            // A literal newline, never System.lineSeparator(): stderr is a byte stream too.
            stderr.write((message + "\n").getBytes(StandardCharsets.UTF_8));
            stderr.flush();
        } catch (IOException mute) {
            // Nothing useful is left to say if the error stream itself cannot be written.
        }
        return status;
    }
}
