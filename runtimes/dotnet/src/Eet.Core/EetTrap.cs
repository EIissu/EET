using System.Globalization;

namespace Eet.Core;

/// <summary>
/// The ten trap identifiers of specification section 6.
/// </summary>
/// <remarks>
/// The member names are deliberately the specification's identifiers verbatim, so that
/// <see cref="Enum.ToString()"/> renders the exact text the trap line requires and the two
/// cannot drift apart.
/// </remarks>
public enum TrapId
{
    /// <summary>Popping from an empty operand stack.</summary>
    T01 = 1,

    /// <summary>Pushing onto a full operand stack.</summary>
    T02,

    /// <summary><c>call</c> at the maximum frame count.</summary>
    T03,

    /// <summary><c>div</c> or <c>mod</c> with a zero divisor.</summary>
    T04,

    /// <summary>Unknown opcode, or immediates running past the end of the code.</summary>
    T05,

    /// <summary>Bad local index, or <c>nargs &gt; nlocals</c>.</summary>
    T06,

    /// <summary>Global index outside the globals array.</summary>
    T07,

    /// <summary><c>dload</c> or <c>prints</c> outside the data section.</summary>
    T08,

    /// <summary>Branch target, or fall-through, past the end of the code.</summary>
    T09,

    /// <summary>The <c>trap</c> instruction executed.</summary>
    T10,
}

/// <summary>
/// A deterministic runtime fault. Carries everything the section 6 stderr line needs and
/// nothing else - in particular it is not used for host-level failures such as an
/// unreadable file, which are load errors (<see cref="Isa.ExitLoadError"/>).
/// </summary>
public sealed class EetTrap : Exception
{
    private EetTrap(TrapId id, int pc, string message)
        : base(message)
    {
        Id = id;
        Pc = pc;
    }

    /// <summary>Which trap fired.</summary>
    public TrapId Id { get; }

    /// <summary>
    /// The address of the <em>first byte of the trapping instruction</em>, which is not
    /// the same as the program counter at the moment of the fault - by then it has already
    /// advanced past the immediates (section 5.2).
    /// </summary>
    public int Pc { get; }

    /// <summary>Raises <paramref name="id"/> against the instruction starting at <paramref name="pc"/>.</summary>
    public static EetTrap At(TrapId id, int pc) => new(id, pc, MessageFor(id));

    /// <summary>
    /// Raises <see cref="TrapId.T10"/>, whose message carries the operand of the
    /// <c>trap</c> instruction in decimal.
    /// </summary>
    public static EetTrap User(int pc, byte code) => new(
        TrapId.T10,
        pc,
        string.Create(CultureInfo.InvariantCulture, $"trap instruction (code={code})"));

    /// <summary>
    /// The single line section 6 puts on stderr, terminated by one <c>\n</c>.
    /// </summary>
    /// <remarks>
    /// The line ends in a bare LF and the pc is eight uppercase hex digits; both are
    /// byte-for-byte conformance requirements, so this is the only place either is spelled.
    /// The address is rendered through its unsigned view, which is the reference
    /// implementation's <c>pc &amp; 0xFFFFFFFF</c>.
    /// </remarks>
    public string ToLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"eet: trap {Id}: {Message} at pc={unchecked((uint)Pc):X8}\n");

    private static string MessageFor(TrapId id) => id switch
    {
        TrapId.T01 => "stack underflow",
        TrapId.T02 => "stack overflow",
        TrapId.T03 => "call depth exceeded",
        TrapId.T04 => "division by zero",
        TrapId.T05 => "invalid opcode",
        TrapId.T06 => "local index out of range",
        TrapId.T07 => "global index out of range",
        TrapId.T08 => "data access out of range",
        TrapId.T09 => "jump out of range",
        TrapId.T10 => "trap instruction",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
