using System.Buffers.Binary;
using System.Globalization;

namespace Eet.Core;

/// <summary>
/// The bytes handed to <see cref="EetModule.Load"/> were not a valid <c>.eetb</c> image.
/// </summary>
/// <remarks>
/// Distinct from <see cref="EetTrap"/> on purpose: section 5.3 gives a load error exit
/// status 65 and a <c>eet: bad binary: ...</c> line, where a trap gets 70 and the section 6
/// line. Confusing the two is an observable conformance failure.
/// </remarks>
public sealed class EetLoadException : Exception
{
    /// <summary>Creates the exception with the reason that goes after <c>bad binary:</c>.</summary>
    /// <param name="message">The reason, lowercase and without punctuation.</param>
    public EetLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A loaded EET program: the immutable code and data sections plus the header fields the
/// machine needs at startup (specification section 2).
/// </summary>
public sealed class EetModule
{
    private readonly byte[] _code;
    private readonly byte[] _data;

    private EetModule(int globalCount, int entryLocals, int entry, byte[] code, byte[] data)
    {
        GlobalCount = globalCount;
        EntryLocals = entryLocals;
        Entry = entry;
        _code = code;
        _data = data;
    }

    /// <summary>Number of global slots, all zero at startup.</summary>
    public int GlobalCount { get; }

    /// <summary>Local slots given to the entry frame.</summary>
    public int EntryLocals { get; }

    /// <summary>Byte offset into <see cref="Code"/> where execution begins.</summary>
    public int Entry { get; }

    /// <summary>The instruction stream.</summary>
    public ReadOnlySpan<byte> Code => _code;

    /// <summary>The read-only data section.</summary>
    public ReadOnlySpan<byte> Data => _data;

    // The VM wants the arrays themselves: it indexes them millions of times, and a property
    // returning a fresh span on every access would be re-checking the same bounds forever.
    internal byte[] CodeBytes => _code;

    internal byte[] DataBytes => _data;

    /// <summary>
    /// Parses and validates a <c>.eetb</c> image.
    /// </summary>
    /// <param name="image">The complete file contents.</param>
    /// <returns>The loaded module.</returns>
    /// <exception cref="EetLoadException">
    /// The image violates one of the rules in section 2.
    /// </exception>
    /// <remarks>
    /// The checks run in the order the fields appear, so the reported reason always names
    /// the first thing that is wrong rather than whichever check happened to be cheapest.
    /// </remarks>
    public static EetModule Load(ReadOnlySpan<byte> image)
    {
        if (image.Length < Isa.MinFileSize)
        {
            throw new EetLoadException("file too short");
        }

        if (!image[..Isa.Magic.Length].SequenceEqual(Isa.Magic))
        {
            throw new EetLoadException("bad magic");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(image[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(image[6..]);
        ushort globalCount = BinaryPrimitives.ReadUInt16LittleEndian(image[8..]);
        ushort entryLocals = BinaryPrimitives.ReadUInt16LittleEndian(image[10..]);
        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(image[12..]);
        uint codeLength = BinaryPrimitives.ReadUInt32LittleEndian(image[16..]);

        if (version != Isa.Version)
        {
            throw new EetLoadException(
                string.Create(CultureInfo.InvariantCulture, $"unsupported version {version}"));
        }

        if (flags != 0)
        {
            throw new EetLoadException(
                string.Create(CultureInfo.InvariantCulture, $"unsupported flags 0x{flags:X4}"));
        }

        if (entryLocals > Isa.MaxLocals)
        {
            throw new EetLoadException("entry_locals out of range");
        }

        // The two lengths are u32 and the offsets derived from them are compared in 64-bit
        // arithmetic. A hostile `code_len` of 0xFFFFFFFF would otherwise wrap an int back
        // into range and turn a rejection into an out-of-bounds slice.
        long codeEnd = Isa.HeaderSize + (long)codeLength;
        if (codeEnd > image.Length)
        {
            throw new EetLoadException("code section runs past end of file");
        }

        if (entry >= codeLength)
        {
            throw new EetLoadException("entry past end of code");
        }

        if (image.Length < codeEnd + sizeof(uint))
        {
            throw new EetLoadException("missing data length");
        }

        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(image[(int)codeEnd..]);
        long dataStart = codeEnd + sizeof(uint);
        long dataEnd = dataStart + dataLength;
        if (dataEnd > image.Length)
        {
            throw new EetLoadException("data section runs past end of file");
        }

        if (dataEnd != image.Length)
        {
            throw new EetLoadException("trailing bytes after data section");
        }

        return new EetModule(
            globalCount,
            entryLocals,
            (int)entry,
            image.Slice(Isa.HeaderSize, (int)codeLength).ToArray(),
            image.Slice((int)dataStart, (int)dataLength).ToArray());
    }
}
