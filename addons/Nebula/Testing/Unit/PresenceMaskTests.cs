using System;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The presence-mask wire encoding as pure byte logic: flat below three mask bytes, header
/// plus nonzero bytes from three up. The failure that matters on the read side is a byte the
/// header skips being left stale, so the decode tests hand in a scratch full of 0xFF.
/// </summary>
[NebulaUnitTest]
public class PresenceMaskTests
{
    private static NetBuffer Wire(params byte[] bytes)
    {
        var buf = new NetBuffer(32, usePool: false);
        foreach (var b in bytes) NetWriter.WriteByte(buf, b);
        buf.ResetRead();
        return buf;
    }

    [NebulaUnitTest]
    public void Rule_FlatBelowThree_TwoLevelFromThree()
    {
        Assert.False(PresenceMask.UsesTwoLevel(1));
        Assert.False(PresenceMask.UsesTwoLevel(2));
        for (int w = 3; w <= PresenceMask.MaxMaskBytes; w++)
        {
            Assert.True(PresenceMask.UsesTwoLevel(w));
            Assert.Equal(1 + w, PresenceMask.ReservedBytes(w));
        }
        Assert.Equal(1, PresenceMask.ReservedBytes(1));
        Assert.Equal(2, PresenceMask.ReservedBytes(2));
    }

    [NebulaUnitTest]
    public void Encode_Flat_CopiesBytes()
    {
        Span<byte> dst = stackalloc byte[4];
        int len = PresenceMask.Encode(new byte[] { 0x00, 0xA5 }, dst);
        Assert.Equal(2, len);
        Assert.Equal(0x00, dst[0]);
        Assert.Equal(0xA5, dst[1]);
    }

    [NebulaUnitTest]
    public void Encode_AllZero_IsOneZeroHeader()
    {
        Span<byte> dst = stackalloc byte[9];
        int len = PresenceMask.Encode(new byte[8], dst);
        Assert.Equal(1, len);
        Assert.Equal(0, dst[0]);
    }

    [NebulaUnitTest]
    public void Encode_SingleNonzeroByte_IsHeaderPlusThatByte()
    {
        for (int i = 0; i < 8; i++)
        {
            var mask = new byte[8];
            mask[i] = 0x40;
            Span<byte> dst = stackalloc byte[9];
            int len = PresenceMask.Encode(mask, dst);
            Assert.Equal(2, len);
            Assert.Equal((byte)(1 << i), dst[0]);
            Assert.Equal(0x40, dst[1]);
        }
    }

    [NebulaUnitTest]
    public void Encode_NonzeroBytes_ComeOutAscending()
    {
        var mask = new byte[] { 0, 0x11, 0, 0, 0x22, 0, 0x33, 0 };
        Span<byte> dst = stackalloc byte[9];
        int len = PresenceMask.Encode(mask, dst);
        Assert.Equal(4, len);
        Assert.Equal(0b0101_0010, dst[0]);
        Assert.Equal(0x11, dst[1]);
        Assert.Equal(0x22, dst[2]);
        Assert.Equal(0x33, dst[3]);
    }

    [NebulaUnitTest]
    public void Encode_AllNonzero_CostsOneMoreThanFlat()
    {
        var mask = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Span<byte> dst = stackalloc byte[9];
        int len = PresenceMask.Encode(mask, dst);
        Assert.Equal(9, len);
        Assert.Equal(0xFF, dst[0]);
        for (int i = 0; i < 8; i++) Assert.Equal(mask[i], dst[1 + i]);
    }

    [NebulaUnitTest]
    public void Decode_ZeroFillsSkippedBytes()
    {
        // Header names bytes 1 and 4 of a 6-byte mask; the scratch starts dirty.
        var scratch = new byte[6];
        Array.Fill(scratch, (byte)0xFF);
        var wire = Wire(0b0001_0010, 0xAA, 0xBB, 0x99);

        Assert.True(PresenceMask.Decode(wire, scratch));
        Assert.Equal(new byte[] { 0, 0xAA, 0, 0, 0xBB, 0 }, scratch);
        Assert.Equal(3, wire.ReadPosition);   // the trailing 0x99 is not part of the mask
    }

    [NebulaUnitTest]
    public void Decode_ZeroHeader_ConsumesOneByte_ZeroFillsAll()
    {
        var scratch = new byte[3];
        Array.Fill(scratch, (byte)0xFF);
        var wire = Wire(0x00, 0x77);

        Assert.True(PresenceMask.Decode(wire, scratch));
        Assert.Equal(new byte[] { 0, 0, 0 }, scratch);
        Assert.Equal(1, wire.ReadPosition);
    }

    [NebulaUnitTest]
    public void Decode_Flat_ReadsEveryByte()
    {
        var scratch = new byte[2];
        var wire = Wire(0x12, 0x34);
        Assert.True(PresenceMask.Decode(wire, scratch));
        Assert.Equal(new byte[] { 0x12, 0x34 }, scratch);
        Assert.Equal(2, wire.ReadPosition);
    }

    [NebulaUnitTest]
    public void Decode_HeaderBitBeyondWidth_IsRejected_ScratchUntouched()
    {
        var scratch = new byte[] { 0xFF, 0xFF, 0xFF };
        var wire = Wire(0b0000_1000, 0x01);   // bit 3 names a 4th byte of a 3-byte mask

        Assert.False(PresenceMask.Decode(wire, scratch));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, scratch);
        Assert.Equal(1, wire.ReadPosition);   // only the header was consumed
    }

    [NebulaUnitTest]
    public void Decode_FullWidthHeader_IsInRange()
    {
        var scratch = new byte[8];
        var wire = Wire(0xFF, 1, 2, 3, 4, 5, 6, 7, 8);
        Assert.True(PresenceMask.Decode(wire, scratch));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, scratch);
    }

    [NebulaUnitTest]
    public void RoundTrip_EveryWidth()
    {
        var rng = new Random(77);
        for (int width = 1; width <= PresenceMask.MaxMaskBytes; width++)
        {
            for (int round = 0; round < 32; round++)
            {
                var mask = new byte[width];
                for (int i = 0; i < width; i++) mask[i] = rng.Next(3) == 0 ? (byte)rng.Next(1, 256) : (byte)0;

                var dst = new byte[PresenceMask.ReservedBytes(width)];
                int len = PresenceMask.Encode(mask, dst);
                Assert.True(len <= dst.Length);

                var wire = new NetBuffer(16, usePool: false);
                NetWriter.WriteBytes(wire, dst.AsSpan(0, len));
                wire.ResetRead();
                var back = new byte[width];
                Array.Fill(back, (byte)0xFF);
                Assert.True(PresenceMask.Decode(wire, back));
                Assert.Equal(mask, back);
                Assert.Equal(len, wire.ReadPosition);
            }
        }
    }
}
