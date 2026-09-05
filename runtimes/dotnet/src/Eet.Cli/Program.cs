using System.Text;
using Eet.Core;

namespace Eet.Cli;

/// <summary>
/// The <c>eet</c> command: <c>eet run &lt;program.eetb&gt;</c>, and nothing else.
/// </summary>
/// <remarks>
/// The whole front end is a funnel into <see cref="VirtualMachine.Execute"/>. Its one job
/// beyond that is to make sure the process only ever emits bytes the specification asks
/// for: no banner, no trailing newline, no exception text.
/// </remarks>
internal static class Program
{
    /// <summary>Invoked wrongly. 64 is <c>EX_USAGE</c>, the neighbour of the spec's 65.</summary>
    private const int ExitUsage = 64;

    /// <summary>Large enough that even printc-per-cell programs make few syscalls.</summary>
    private const int OutputBufferBytes = 1 << 16;

    private static int Main(string[] args)
    {
        // Raw handles, never Console.Out: section 4.6 forbids an encoding layer, and on
        // Windows a text-mode handle rewrites every \n as \r\n, which would put this
        // runtime one byte per line away from all the others.
        using Stream stderr = Console.OpenStandardError();

        if (args.Length != 2 || !string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            WriteLine(stderr, "eet: usage: eet run <program.eetb>");
            return ExitUsage;
        }

        byte[] image;
        try
        {
            image = File.ReadAllBytes(args[1]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                or ArgumentException or NotSupportedException)
        {
            WriteLine(stderr, $"eet: cannot read {args[1]}: {error.Message}");
            return Isa.ExitLoadError;
        }

        EetModule module;
        try
        {
            module = EetModule.Load(image);
        }
        catch (EetLoadException error)
        {
            // The exact wording of section 2: a load error, never a trap.
            WriteLine(stderr, $"eet: bad binary: {error.Message}");
            return Isa.ExitLoadError;
        }

        using BufferedStream stdout = new(Console.OpenStandardOutput(), OutputBufferBytes);
        try
        {
            return VirtualMachine.Execute(module, stdout, stderr);
        }
        catch (Exception error)
        {
            // Nothing below this point should throw, but a stack trace on stdout or stderr
            // would corrupt a conformance run, so the last resort is a single tidy line.
            // Section 5.3 still applies: whatever the program managed to print survives.
            stdout.Flush();
            WriteLine(stderr, $"eet: internal error: {error.Message}");
            return Isa.ExitTrap;
        }
    }

    private static void WriteLine(Stream stream, string text)
    {
        stream.Write(Encoding.UTF8.GetBytes(text + "\n"));
        stream.Flush();
    }
}
