using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eet.Core;

/// <summary>
/// An EET machine bound to one output stream (specification section 5).
/// </summary>
/// <remarks>
/// <para>
/// A machine is single use: construct it, <see cref="Run"/> it once, discard it. That is
/// what lets the storage below be laid out once in the constructor and never grown again.
/// </para>
/// <para>
/// Section 1.2 gives every frame a private operand stack. Rather than allocate one per
/// call - which would put a fresh array on the heap for each step of a recursion - the
/// machine carves one flat array into fixed 1024-slot windows, one per frame, and keeps the
/// current frame's base in a field. Privacy then falls out of arithmetic: a pop that would
/// cross <c>_stackBase</c> is <see cref="TrapId.T01"/>, so a callee cannot reach its
/// caller's values even though the storage is contiguous. Locals work the same way.
/// </para>
/// </remarks>
public sealed class VirtualMachine
{
    private readonly byte[] _code;
    private readonly byte[] _data;
    private readonly int[] _globals;
    private readonly Stream _stdout;

    private readonly int[] _stack = new int[Isa.MaxCallDepth * Isa.MaxOperandStack];
    private readonly int[] _localSlots = new int[Isa.MaxCallDepth * Isa.MaxLocals];
    private readonly Frame[] _frames = new Frame[Isa.MaxCallDepth];

    // print renders at most eleven bytes and is on the hot path of every program that
    // produces numbers, so it formats into this rather than allocating.
    private readonly byte[] _decimalBuffer = new byte[Ops.MaxDecimalLength];

    private int _depth;
    private int _stackBase;
    private int _sp;
    private int _localsBase;
    private int _localCount;
    private int _pc;

    // The address of the instruction being executed. Every trap reports this, never _pc,
    // which by the time an instruction runs has already moved past its immediates (5.2).
    private int _opPc;

    /// <summary>
    /// Prepares a machine to run <paramref name="module"/>, writing its output to
    /// <paramref name="stdout"/>.
    /// </summary>
    /// <param name="module">The program to execute.</param>
    /// <param name="stdout">
    /// A raw byte sink. It must not translate line endings or apply an encoding
    /// (section 4.6).
    /// </param>
    public VirtualMachine(EetModule module, Stream stdout)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(stdout);

        _code = module.CodeBytes;
        _data = module.DataBytes;
        _globals = new int[module.GlobalCount];
        _stdout = stdout;

        // Startup, 5.1: one frame whose return address is the "exit" sentinel.
        _frames[0] = new Frame(returnPc: -1, callerStackPointer: 0, localCount: module.EntryLocals);
        _depth = 1;
        _localCount = module.EntryLocals;
        _pc = module.Entry;
        _opPc = module.Entry;
    }

    /// <summary>
    /// Runs <paramref name="module"/> to completion and applies the termination protocol of
    /// section 5.3.
    /// </summary>
    /// <param name="module">The program to execute.</param>
    /// <param name="stdout">Raw byte sink for program output.</param>
    /// <param name="stderr">Raw byte sink for the trap line, if any.</param>
    /// <returns>The process exit status.</returns>
    public static int Execute(EetModule module, Stream stdout, Stream stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        VirtualMachine machine = new(module, stdout);
        try
        {
            int status = machine.Run();
            stdout.Flush();
            return status;
        }
        catch (EetTrap trap)
        {
            // "A trap does not discard prior output" (5.3): stdout is flushed first so that
            // a consumer reading both streams sees the program's bytes before the diagnosis.
            stdout.Flush();
            stderr.Write(Encoding.UTF8.GetBytes(trap.ToLine()));
            stderr.Flush();
            return Isa.ExitTrap;
        }
    }

    /// <summary>
    /// Executes instructions until the program terminates.
    /// </summary>
    /// <returns>
    /// The process exit status: 0 from <c>halt</c>, or the entry frame's returned value
    /// masked to a byte.
    /// </returns>
    /// <exception cref="EetTrap">
    /// The program faulted; the caller owns the section 6 line.
    /// </exception>
    public int Run()
    {
        while (true)
        {
            if (_pc >= _code.Length)
            {
                // Falling off the end of the code section, 5.4. This is the one trap that
                // reports the program counter itself: there is no instruction to blame.
                throw EetTrap.At(TrapId.T09, _pc);
            }

            _opPc = _pc;
            Opcode op = (Opcode)_code[_pc++];

            switch (op)
            {
                // -- 4.1 stack and control
                case Opcode.Halt:
                    return Isa.ExitOk;

                case Opcode.Nop:
                    break;

                case Opcode.Push:
                    Push(ReadI32());
                    break;

                case Opcode.Pop:
                    Pop();
                    break;

                case Opcode.Dup:
                {
                    int a = Pop();
                    Push(a);
                    Push(a);
                    break;
                }

                case Opcode.Swap:
                {
                    int b = Pop();
                    int a = Pop();
                    Push(b);
                    Push(a);
                    break;
                }

                case Opcode.Over:
                {
                    int b = Pop();
                    int a = Pop();
                    Push(a);
                    Push(b);
                    Push(a);
                    break;
                }

                case Opcode.Rot:
                {
                    int c = Pop();
                    int b = Pop();
                    int a = Pop();
                    Push(b);
                    Push(c);
                    Push(a);
                    break;
                }

                // -- 4.2 arithmetic and bitwise. The deeper operand is a, the top is b, so
                // b comes off first; "push 10, push 3, sub" has to yield 7 (4.2).
                case Opcode.Add:
                {
                    int b = Pop();
                    Push(Ops.Add(Pop(), b));
                    break;
                }

                case Opcode.Sub:
                {
                    int b = Pop();
                    Push(Ops.Sub(Pop(), b));
                    break;
                }

                case Opcode.Mul:
                {
                    int b = Pop();
                    Push(Ops.Mul(Pop(), b));
                    break;
                }

                case Opcode.Div:
                {
                    int b = Pop();
                    int a = Pop();
                    if (b == 0)
                    {
                        throw EetTrap.At(TrapId.T04, _opPc);
                    }

                    Push(Ops.Div(a, b));
                    break;
                }

                case Opcode.Mod:
                {
                    int b = Pop();
                    int a = Pop();
                    if (b == 0)
                    {
                        throw EetTrap.At(TrapId.T04, _opPc);
                    }

                    Push(Ops.Mod(a, b));
                    break;
                }

                case Opcode.Neg:
                    Push(Ops.Neg(Pop()));
                    break;

                case Opcode.And:
                {
                    int b = Pop();
                    Push(Ops.And(Pop(), b));
                    break;
                }

                case Opcode.Or:
                {
                    int b = Pop();
                    Push(Ops.Or(Pop(), b));
                    break;
                }

                case Opcode.Xor:
                {
                    int b = Pop();
                    Push(Ops.Xor(Pop(), b));
                    break;
                }

                case Opcode.Not:
                    Push(Ops.Not(Pop()));
                    break;

                case Opcode.Shl:
                {
                    int b = Pop();
                    Push(Ops.Shl(Pop(), b));
                    break;
                }

                case Opcode.Shr:
                {
                    int b = Pop();
                    Push(Ops.Shr(Pop(), b));
                    break;
                }

                case Opcode.Ushr:
                {
                    int b = Pop();
                    Push(Ops.Ushr(Pop(), b));
                    break;
                }

                // -- 4.3 comparison. All signed, which C# int comparison already is.
                case Opcode.Eq:
                {
                    int b = Pop();
                    Push(Pop() == b ? 1 : 0);
                    break;
                }

                case Opcode.Ne:
                {
                    int b = Pop();
                    Push(Pop() != b ? 1 : 0);
                    break;
                }

                case Opcode.Lt:
                {
                    int b = Pop();
                    Push(Pop() < b ? 1 : 0);
                    break;
                }

                case Opcode.Le:
                {
                    int b = Pop();
                    Push(Pop() <= b ? 1 : 0);
                    break;
                }

                case Opcode.Gt:
                {
                    int b = Pop();
                    Push(Pop() > b ? 1 : 0);
                    break;
                }

                case Opcode.Ge:
                {
                    int b = Pop();
                    Push(Pop() >= b ? 1 : 0);
                    break;
                }

                // -- 4.4 branching and calls
                case Opcode.Jmp:
                    Jump(ReadU32());
                    break;

                case Opcode.Jz:
                {
                    // The target is decoded before the condition is popped, so a truncated
                    // immediate is T05 even when the branch would not have been taken.
                    uint target = ReadU32();
                    if (Pop() == 0)
                    {
                        Jump(target);
                    }

                    break;
                }

                case Opcode.Jnz:
                {
                    uint target = ReadU32();
                    if (Pop() != 0)
                    {
                        Jump(target);
                    }

                    break;
                }

                case Opcode.Call:
                    Call();
                    break;

                case Opcode.Ret:
                    if (Return(out int status))
                    {
                        return status;
                    }

                    break;

                // -- 4.5 memory
                case Opcode.Load:
                {
                    int index = ReadU8();
                    if (index >= _localCount)
                    {
                        throw EetTrap.At(TrapId.T06, _opPc);
                    }

                    Push(_localSlots[_localsBase + index]);
                    break;
                }

                case Opcode.Store:
                {
                    // The index is validated before the value is popped, so an out-of-range
                    // store on an empty stack is T06 and not T01.
                    int index = ReadU8();
                    if (index >= _localCount)
                    {
                        throw EetTrap.At(TrapId.T06, _opPc);
                    }

                    _localSlots[_localsBase + index] = Pop();
                    break;
                }

                case Opcode.GLoad:
                {
                    int index = ReadU16();
                    if (index >= _globals.Length)
                    {
                        throw EetTrap.At(TrapId.T07, _opPc);
                    }

                    Push(_globals[index]);
                    break;
                }

                case Opcode.GStore:
                {
                    int index = ReadU16();
                    if (index >= _globals.Length)
                    {
                        throw EetTrap.At(TrapId.T07, _opPc);
                    }

                    _globals[index] = Pop();
                    break;
                }

                case Opcode.DLoad:
                {
                    int address = Pop();
                    if (OutOfRange(address, _data.Length))
                    {
                        throw EetTrap.At(TrapId.T08, _opPc);
                    }

                    // dload zero-extends, so the pushed value is always 0..255 (4.5).
                    Push(_data[address]);
                    break;
                }

                case Opcode.GLoadX:
                {
                    int index = Pop();
                    if (OutOfRange(index, _globals.Length))
                    {
                        throw EetTrap.At(TrapId.T07, _opPc);
                    }

                    Push(_globals[index]);
                    break;
                }

                case Opcode.GStoreX:
                {
                    // Index on top, value beneath (4.5). Both come off before the range
                    // check, so an underflow beneath a bad index is still reported as T01.
                    int index = Pop();
                    int value = Pop();
                    if (OutOfRange(index, _globals.Length))
                    {
                        throw EetTrap.At(TrapId.T07, _opPc);
                    }

                    _globals[index] = value;
                    break;
                }

                // -- 4.6 output
                case Opcode.Print:
                    _stdout.Write(Ops.FormatDecimal(Pop(), _decimalBuffer));
                    break;

                case Opcode.PrintC:
                    _stdout.WriteByte((byte)(Pop() & 0xFF));
                    break;

                case Opcode.PrintS:
                {
                    int length = Pop();
                    int address = Pop();
                    // The sum is computed in 64 bits: two large positive operands would
                    // otherwise wrap round to an offset that looks like it is in range.
                    if (length < 0 || address < 0 || (long)address + length > _data.Length)
                    {
                        throw EetTrap.At(TrapId.T08, _opPc);
                    }

                    _stdout.Write(_data, address, length);
                    break;
                }

                // -- 4.7 diagnostics
                case Opcode.Trap:
                    throw EetTrap.User(_opPc, ReadU8());

                default:
                    throw EetTrap.At(TrapId.T05, _opPc);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="index"/> falls outside <c>[0, length)</c>, which is the
    /// shape of the bounds condition on <c>dload</c>, <c>gloadx</c> and <c>gstorex</c>.
    /// </summary>
    /// <remarks>
    /// One unsigned comparison covers both ends, because a negative index reinterprets as
    /// a very large <see cref="uint"/>. The conversion is written <c>unchecked</c> on
    /// purpose: under <c>CheckForOverflowUnderflow</c> a negative <see cref="int"/> cast to
    /// <see cref="uint"/> throws, which would turn a trap into an internal error.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OutOfRange(int index, int length)
        => unchecked((uint)index >= (uint)length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(int value)
    {
        if (_sp - _stackBase >= Isa.MaxOperandStack)
        {
            throw EetTrap.At(TrapId.T02, _opPc);
        }

        _stack[_sp++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Pop()
    {
        if (_sp <= _stackBase)
        {
            throw EetTrap.At(TrapId.T01, _opPc);
        }

        return _stack[--_sp];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadU8()
    {
        if (_pc >= _code.Length)
        {
            throw EetTrap.At(TrapId.T05, _opPc);
        }

        return _code[_pc++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadU16()
    {
        if (_pc + sizeof(ushort) > _code.Length)
        {
            throw EetTrap.At(TrapId.T05, _opPc);
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_code.AsSpan(_pc));
        _pc += sizeof(ushort);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadU32()
    {
        if (_pc + sizeof(uint) > _code.Length)
        {
            throw EetTrap.At(TrapId.T05, _opPc);
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_code.AsSpan(_pc));
        _pc += sizeof(uint);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadI32()
    {
        if (_pc + sizeof(int) > _code.Length)
        {
            throw EetTrap.At(TrapId.T05, _opPc);
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(_code.AsSpan(_pc));
        _pc += sizeof(int);
        return value;
    }

    /// <summary>Takes a branch, validating the target as section 4.4 requires.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Jump(uint target)
    {
        if (target >= (uint)_code.Length)
        {
            throw EetTrap.At(TrapId.T09, _opPc);
        }

        _pc = (int)target;
    }

    /// <summary>
    /// Enters a new frame (section 5.4). The order of the checks below is itself normative:
    /// a call that is both too deep and short of arguments must report T03, not T01.
    /// </summary>
    private void Call()
    {
        uint target = ReadU32();
        int argumentCount = ReadU8();
        int localCount = ReadU8();

        if (argumentCount > localCount)
        {
            throw EetTrap.At(TrapId.T06, _opPc);
        }

        if (_depth >= Isa.MaxCallDepth)
        {
            throw EetTrap.At(TrapId.T03, _opPc);
        }

        if (_sp - _stackBase < argumentCount)
        {
            throw EetTrap.At(TrapId.T01, _opPc);
        }

        _sp -= argumentCount;

        if (target >= (uint)_code.Length)
        {
            throw EetTrap.At(TrapId.T09, _opPc);
        }

        int index = _depth;
        int localsBase = index * Isa.MaxLocals;

        // Windows are reused by successive calls at the same depth, so a new frame has to
        // be scrubbed: 5.4 step 4 says slots argumentCount..localCount-1 start at zero.
        Array.Clear(_localSlots, localsBase, localCount);

        // The arguments sit in the caller's stack in push order, and that is exactly the
        // order they occupy in the callee's locals: first pushed becomes locals[0].
        _stack.AsSpan(_sp, argumentCount).CopyTo(_localSlots.AsSpan(localsBase, argumentCount));

        _frames[index] = new Frame(returnPc: _pc, callerStackPointer: _sp, localCount: localCount);
        _depth = index + 1;
        _stackBase = index * Isa.MaxOperandStack;
        _sp = _stackBase;
        _localsBase = localsBase;
        _localCount = localCount;
        _pc = (int)target;
    }

    /// <summary>
    /// Leaves the current frame (section 5.4).
    /// </summary>
    /// <param name="status">The process exit status, when the entry frame returned.</param>
    /// <returns><see langword="true"/> when the program has terminated.</returns>
    private bool Return(out int status)
    {
        int value = Pop();
        int index = _depth - 1;
        _depth = index;

        if (index == 0)
        {
            // Returning from the entry frame is a normal exit whose status is the low byte
            // of the returned value (5.3).
            status = value & 0xFF;
            return true;
        }

        Frame finished = _frames[index];
        int caller = index - 1;

        _pc = finished.ReturnPc;
        _stackBase = caller * Isa.MaxOperandStack;
        _localsBase = caller * Isa.MaxLocals;
        _localCount = _frames[caller].LocalCount;
        _sp = finished.CallerStackPointer;

        // Pushing the result onto the restored caller can still overflow, and section 5.4
        // says that is T02 rather than a silently dropped value.
        Push(value);

        status = 0;
        return false;
    }

    /// <summary>
    /// The bookkeeping a frame needs beyond its two windows: where to resume, where the
    /// caller's operand stack had got to, and how many of its local slots are addressable.
    /// </summary>
    private readonly struct Frame(int returnPc, int callerStackPointer, int localCount)
    {
        public int ReturnPc { get; } = returnPc;

        public int CallerStackPointer { get; } = callerStackPointer;

        public int LocalCount { get; } = localCount;
    }
}
