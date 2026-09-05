using System.Buffers.Binary;
using Eet.Core;
using Xunit;

namespace Eet.Tests;

/// <summary>
/// The container loader, specification section 2.
/// </summary>
/// <remarks>
/// The assembler never emits any of these shapes, so nothing in <c>programs/</c> can catch
/// a loader that skips a check. A rejection here has to be an <see cref="EetLoadException"/>
/// and never an <see cref="EetTrap"/>: the two lead to different exit statuses and
/// different stderr text.
/// </remarks>
public class EetModuleTests
{
    [Fact]
    public void AMinimalImageLoads()
    {
        EetModule module = EetModule.Load(Valid());

        Assert.Equal(0, module.Entry);
        Assert.Equal(1, module.Code.Length);
        Assert.Equal(0, module.Data.Length);
    }

    [Fact]
    public void HeaderFieldsSurviveTheRoundTrip()
    {
        byte[] image = Valid(globalCount: 7, entryLocals: 3, code: [0x01, 0x00], entry: 1, data: [9, 8]);

        EetModule module = EetModule.Load(image);

        Assert.Equal(7, module.GlobalCount);
        Assert.Equal(3, module.EntryLocals);
        Assert.Equal(1, module.Entry);
        Assert.Equal(new byte[] { 9, 8 }, module.Data.ToArray());
    }

    [Fact]
    public void AnEmptyFileIsRejected()
        => AssertRejected([]);

    [Fact]
    public void AFileShorterThanTheHeaderIsRejected()
        => AssertRejected(Valid().AsSpan(0, 20).ToArray());

    [Fact]
    public void TheWrongMagicIsRejected()
    {
        byte[] image = Valid();
        image[0] = (byte)'N';

        AssertRejected(image);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(ushort.MaxValue)]
    public void AnyVersionOtherThanOneIsRejected(int version)
        => AssertRejected(Patch(Valid(), 4, (ushort)version));

    [Theory]
    [InlineData(1)]
    [InlineData(0x8000)]
    public void AnyNonZeroFlagIsRejected(int flags)
        => AssertRejected(Patch(Valid(), 6, (ushort)flags));

    [Fact]
    public void MoreEntryLocalsThanAFrameHoldsIsRejected()
        => AssertRejected(Patch(Valid(), 10, 257));

    [Fact]
    public void TheMaximumEntryLocalsIsAccepted()
        => Assert.Equal(Isa.MaxLocals, EetModule.Load(Patch(Valid(), 10, 256)).EntryLocals);

    [Fact]
    public void AnEntryPointPastTheCodeIsRejected()
        => AssertRejected(Patch(Valid(), 12, 1u));

    [Fact]
    public void AZeroLengthCodeSectionLeavesTheEntryPointNowhereToBe()
        => AssertRejected(Valid(code: []));

    [Theory]
    [InlineData(0xFFFF_FF00u)]
    [InlineData(0xFFFF_FFFFu)]
    public void ACodeLengthPastTheEndOfTheFileIsRejected(uint codeLength)
    {
        // The larger value is chosen to wrap a 32-bit offset calculation back into range;
        // the loader has to do that arithmetic in 64 bits to notice.
        AssertRejected(Patch(Valid(), 16, codeLength));
    }

    [Theory]
    [InlineData(0xFFFF_FF00u)]
    [InlineData(0xFFFF_FFFFu)]
    public void ADataLengthPastTheEndOfTheFileIsRejected(uint dataLength)
    {
        byte[] image = Valid(data: [1, 2]);
        AssertRejected(Patch(image, 21, dataLength));
    }

    [Fact]
    public void AMissingDataLengthWordIsRejected()
    {
        byte[] image = Valid();
        AssertRejected(image.AsSpan(0, image.Length - 1).ToArray());
    }

    [Fact]
    public void TrailingBytesAfterTheDataSectionAreRejected()
        => AssertRejected([.. Valid(), .. "junk"u8]);

    private static void AssertRejected(byte[] image)
        => Assert.Throws<EetLoadException>(() => EetModule.Load(image));

    private static byte[] Valid(
        int globalCount = 0,
        int entryLocals = 0,
        int entry = 0,
        byte[]? code = null,
        byte[]? data = null)
    {
        code ??= [0x00];
        data ??= [];

        byte[] image = new byte[Isa.HeaderSize + code.Length + sizeof(uint) + data.Length];
        Span<byte> span = image;

        Isa.Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Isa.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)globalCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)entryLocals);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)entry);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)code.Length);
        code.CopyTo(span[Isa.HeaderSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(Isa.HeaderSize + code.Length)..], (uint)data.Length);
        data.CopyTo(span[(Isa.HeaderSize + code.Length + sizeof(uint))..]);

        return image;
    }

    private static byte[] Patch(byte[] image, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);
        return image;
    }

    private static byte[] Patch(byte[] image, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        return image;
    }
}
