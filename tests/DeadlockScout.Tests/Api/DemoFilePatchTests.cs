using DemoFile;
using DemoFile.Game.Deadlock;

namespace DeadlockScout.Tests.Api;

/// <summary>
/// Pins the decoder changes in third_party/demofile-net (see VENDORED.md) that a live Deadlock broadcast from build 6821
/// needed. Byte patterns are hand-built; the values match what the haste parser reads.
/// </summary>
public class DemoFilePatchTests
{
    private static FieldEncodingInfo Enc(string? encoder) => new(encoder, 0, 0, null, null);

    [Fact]
    public void Fixed8_unsigned_fields_read_exactly_eight_bits()
    {
        // 200 as a raw byte, then a marker byte. A varint read would swallow both bytes.
        var bytes = new byte[] { 200, 0x2A };
        var b = new BitBuffer(bytes);
        Assert.Equal((byte)200, FieldDecode.CreateDecoder_byte(Enc("fixed8"))(ref b));
        Assert.Equal(8, b.TellBits);
        Assert.Equal(0x2Au, b.ReadUBits(8));
    }

    [Fact]
    public void Fixed8_signed_fields_are_sign_extended()
    {
        var bytes = new byte[] { 0xFF, 0x04 };
        var b = new BitBuffer(bytes);
        var read = FieldDecode.CreateDecoder_sbyte(Enc("fixed8"));
        Assert.Equal((sbyte)-1, read(ref b));
        Assert.Equal((sbyte)4, read(ref b));
    }

    [Fact]
    public void Fixed64_and_varint_unsigned_fields()
    {
        var fixed64 = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0x80 };
        var b = new BitBuffer(fixed64);
        Assert.Equal(0x8000000000000001UL, FieldDecode.CreateDecoder_UInt64(Enc("fixed64"))(ref b));

        var varint = new byte[] { 0xAC, 0x02 }; // 300
        var v = new BitBuffer(varint);
        Assert.Equal(300u, FieldDecode.CreateDecoder_UInt32(Enc(null))(ref v));
        Assert.Equal(16, v.TellBits);
    }

    [Fact]
    public void Hero_ids_are_unsigned_varints()
    {
        // haste reads 18 for this byte; the upstream zigzag decode turned it into 9.
        var b = new BitBuffer(new byte[] { 18 });
        Assert.Equal(18, HeroID.Decode(ref b).Value);
        var odd = new BitBuffer(new byte[] { 7 });
        Assert.Equal(7, HeroID.Decode(ref odd).Value); // zigzag gave -4
    }

    [Fact]
    public void Binary_blocks_skip_their_length_prefixed_bytes()
    {
        var bytes = new byte[] { 3, 9, 9, 9, 0x2A };
        var b = new BitBuffer(bytes);
        FieldDecode.CreateDecoder_CUtlBinaryBlock(Enc(null))(ref b);
        Assert.Equal(32, b.TellBits);
        Assert.Equal(0x2Au, b.ReadUBits(8));
    }
}
