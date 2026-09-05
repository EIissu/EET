using System.Buffers.Binary;
using System.Text;
using Eet.Core;

namespace Eet.Tests;

/// <summary>
/// Assembles a <c>.eetb</c> image byte by byte and runs it.
/// </summary>
/// <remarks>
/// The tests build their own containers rather than reaching for
/// <c>programs/*.eetb</c>: the interesting cases here are the ones the repository
/// assembler refuses to emit - a truncated immediate, an opcode that does not exist, a
/// branch off the end of the code - and a hand-rolled emitter is the only way to reach them.
/// </remarks>
internal sealed class TestProgram
{
    private readonly List<byte> _code = [];
    private byte[] _data = [];
    private ushort _globalCount;
    private ushort _entryLocals;
    private uint _entry;

    /// <summary>The offset the next emitted instruction will occupy.</summary>
    public int Here => _code.Count;

    public TestProgram WithGlobals(int count)
    {
        _globalCount = (ushort)count;
        return this;
    }

    public TestProgram WithEntryLocals(int count)
    {
        _entryLocals = (ushort)count;
        return this;
    }

    public TestProgram WithData(params byte[] data)
    {
        _data = data;
        return this;
    }

    public TestProgram WithEntry(int offset)
    {
        _entry = (uint)offset;
        return this;
    }

    public TestProgram Emit(Opcode op)
    {
        _code.Add((byte)op);
        return this;
    }

    public TestProgram Push(int value)
    {
        _code.Add((byte)Opcode.Push);
        return AppendInt32(value);
    }

    public TestProgram Emit(Opcode op, byte operand)
    {
        _code.Add((byte)op);
        _code.Add(operand);
        return this;
    }

    public TestProgram EmitU16(Opcode op, ushort operand)
    {
        _code.Add((byte)op);
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, operand);
        _code.AddRange(buffer);
        return this;
    }

    public TestProgram EmitU32(Opcode op, uint operand)
    {
        _code.Add((byte)op);
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, operand);
        _code.AddRange(buffer);
        return this;
    }

    public TestProgram Call(int target, byte argumentCount, byte localCount)
    {
        EmitU32(Opcode.Call, (uint)target);
        _code.Add(argumentCount);
        _code.Add(localCount);
        return this;
    }

    /// <summary>Emits bytes verbatim, for encodings no legal instruction produces.</summary>
    public TestProgram Raw(params byte[] bytes)
    {
        _code.AddRange(bytes);
        return this;
    }

    public byte[] Image()
    {
        List<byte> image =
        [
            .. Isa.Magic,
        ];

        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], Isa.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..4], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], _globalCount);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], _entryLocals);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], _entry);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)_code.Count);
        image.AddRange(header);

        image.AddRange(_code);

        Span<byte> dataLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(dataLength, (uint)_data.Length);
        image.AddRange(dataLength);
        image.AddRange(_data);

        return [.. image];
    }

    public RunResult Run()
    {
        EetModule module = EetModule.Load(Image());
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        int status = VirtualMachine.Execute(module, stdout, stderr);
        return new RunResult(stdout.ToArray(), Encoding.UTF8.GetString(stderr.ToArray()), status);
    }

    private TestProgram AppendInt32(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _code.AddRange(buffer);
        return this;
    }
}

/// <summary>What a run of a <see cref="TestProgram"/> observably produced.</summary>
internal readonly record struct RunResult(byte[] Stdout, string Stderr, int Status)
{
    /// <summary>Stdout decoded as ASCII, for the many programs that print text.</summary>
    public string Text => Encoding.ASCII.GetString(Stdout);
}
