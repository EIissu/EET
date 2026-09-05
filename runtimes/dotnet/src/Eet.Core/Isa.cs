namespace Eet.Core;

/// <summary>
/// The EET opcode numbers, in the order specification section 4 lists them.
/// </summary>
/// <remarks>
/// The numeric values are the contract; the names are not. Any byte with no member here
/// is <see cref="TrapId.T05"/>, which is why <see cref="VirtualMachine"/> switches on this
/// type rather than on <see cref="byte"/> - the compiler then gives the "unknown opcode"
/// path for free in the <c>default</c> arm.
/// </remarks>
public enum Opcode : byte
{
    // 4.1 stack and control
    Halt = 0x00,
    Nop = 0x01,
    Push = 0x02,
    Pop = 0x03,
    Dup = 0x04,
    Swap = 0x05,
    Over = 0x06,
    Rot = 0x07,

    // 4.2 arithmetic and bitwise
    Add = 0x10,
    Sub = 0x11,
    Mul = 0x12,
    Div = 0x13,
    Mod = 0x14,
    Neg = 0x15,
    And = 0x16,
    Or = 0x17,
    Xor = 0x18,
    Not = 0x19,
    Shl = 0x1A,
    Shr = 0x1B,
    Ushr = 0x1C,

    // 4.3 comparison
    Eq = 0x20,
    Ne = 0x21,
    Lt = 0x22,
    Le = 0x23,
    Gt = 0x24,
    Ge = 0x25,

    // 4.4 branching and calls
    Jmp = 0x30,
    Jz = 0x31,
    Jnz = 0x32,
    Call = 0x33,
    Ret = 0x34,

    // 4.5 memory
    Load = 0x40,
    Store = 0x41,
    GLoad = 0x42,
    GStore = 0x43,
    DLoad = 0x44,
    GLoadX = 0x45,
    GStoreX = 0x46,

    // 4.6 output
    Print = 0x50,
    PrintC = 0x51,
    PrintS = 0x52,

    // 4.7 diagnostics
    Trap = 0x60,
}

/// <summary>
/// The constants the specification fixes: container layout (section 2), machine limits
/// (section 1) and process exit statuses (section 5.3).
/// </summary>
public static class Isa
{
    /// <summary>The four magic bytes every <c>.eetb</c> image starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "EETB"u8;

    /// <summary>The only container version this runtime accepts.</summary>
    public const int Version = 1;

    /// <summary>Bytes from the magic through <c>code_len</c>; <c>code</c> starts here.</summary>
    public const int HeaderSize = 20;

    /// <summary>The smallest legal image: a header, no code and an empty data length.</summary>
    public const int MinFileSize = 24;

    /// <summary>Operand stack depth at which a push becomes <see cref="TrapId.T02"/>.</summary>
    public const int MaxOperandStack = 1024;

    /// <summary>Frame count at which a call becomes <see cref="TrapId.T03"/>.</summary>
    public const int MaxCallDepth = 256;

    /// <summary>Upper bound on a frame's local slot count.</summary>
    public const int MaxLocals = 256;

    /// <summary>Normal termination, via <c>halt</c>.</summary>
    public const int ExitOk = 0;

    /// <summary>The image was not a valid EET binary. Never reported as a trap.</summary>
    public const int ExitLoadError = 65;

    /// <summary>Any trap, whichever one it was.</summary>
    public const int ExitTrap = 70;
}
