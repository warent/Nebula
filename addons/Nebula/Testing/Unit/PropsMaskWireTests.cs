using System;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The props section with a WIDE presence mask (three bytes and up), which every other props
/// fixture in the suite never reaches (they declare one to four properties). Covers the
/// two-level mask on the wire, the reserve-and-shift backfill, its interplay with the section
/// memo and the self-limiting budget, and the read side's header validation.
///
/// Value decoding needs the Protocol registry, so the read-side tests stop at the mask and
/// age bytes; the encoder itself is covered byte-for-byte in PresenceMaskTests.
/// </summary>
[NebulaUnitTest]
public class PropsMaskWireTests
{
    private const byte AbsoluteAge = 0;

    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;
        public int PropertyCount;

        public Fixture(int intProps)
        {
            var propTypes = Ints(intProps);
            PropertyCount = intProps;
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes);
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        /// <summary>One export at the given tick with exactly these property indices dirty.</summary>
        public ExportResult Export(int tick, NetBuffer buf, int maxBytes, params int[] dirtyIndices)
        {
            long dirty = 0;
            foreach (var i in dirtyIndices) dirty |= 1L << i;
            World.CurrentTick = tick;
            Node.Network.DirtyMask = dirty;
            Serializer.Begin();
            return Serializer.Export(World, Peer, buf, maxBytes);
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    private static SerialVariantType[] Ints(int n)
    {
        var types = new SerialVariantType[n];
        Array.Fill(types, SerialVariantType.Int);
        return types;
    }

    private static NetBuffer Buffer() => new(1024, usePool: false);

    // 1. Three mask bytes, one dirty prop in the third: header + one mask byte, then the age.
    [NebulaUnitTest]
    public void OneDirtyByte_ShipsHeaderPlusOneMaskByte()
    {
        using var f = new Fixture(24);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 20));

        var span = buf.WrittenSpan;
        Assert.Equal(0b100, span[0]);        // only mask byte 2 is nonzero
        Assert.Equal(0x10, span[1]);         // index 20 = byte 2, bit 4
        Assert.Equal(AbsoluteAge, span[2]);
        Assert.True(span.Length > 3);        // a value follows
    }

    // 2. Dirty props in two different mask bytes: both listed, ascending.
    [NebulaUnitTest]
    public void TwoDirtyBytes_HeaderListsBothAscending()
    {
        using var f = new Fixture(24);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 0, 20));

        var span = buf.WrittenSpan;
        Assert.Equal(0b101, span[0]);
        Assert.Equal(0x01, span[1]);
        Assert.Equal(0x10, span[2]);
        Assert.Equal(AbsoluteAge, span[3]);
    }

    // 3. The player's real shape: 60 props, ship transform in byte 5 and character movement
    //    in byte 6. Indices 46 and 48 dirty -> header names bytes 5 and 6.
    [NebulaUnitTest]
    public void PlayerShape_ShipAndCharacterBytes()
    {
        using var f = new Fixture(60);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 46, 48));

        var span = buf.WrittenSpan;
        Assert.Equal(0b0110_0000, span[0]);
        Assert.Equal(0x40, span[1]);   // byte 5: index 46 = bit 6
        Assert.Equal(0x01, span[2]);   // byte 6: index 48 = bit 0
        Assert.Equal(AbsoluteAge, span[3]);
    }

    // 4. The section is exactly as long as [compact mask][age][values]: compare two exports
    //    of the same single value whose only difference is which mask byte it lives in.
    //    Byte 0 vs byte 2 must cost the same, and one extra dirty byte must cost exactly
    //    one more mask byte plus one value.
    [NebulaUnitTest]
    public void SectionLength_TracksCompactMaskExactly()
    {
        int single0, single20, both;
        using (var f = new Fixture(24))
        {
            var a = Buffer();
            f.Export(1, a, int.MaxValue, 0);
            single0 = a.WrittenSpan.Length;
        }
        using (var f = new Fixture(24))
        {
            var b = Buffer();
            f.Export(1, b, int.MaxValue, 20);
            single20 = b.WrittenSpan.Length;
        }
        using (var f = new Fixture(24))
        {
            var c = Buffer();
            f.Export(1, c, int.MaxValue, 0, 20);
            both = c.WrittenSpan.Length;
        }

        Assert.Equal(single0, single20);
        int valueBytes = single0 - 2 - 1;          // minus [header][one mask byte][age]
        Assert.True(valueBytes >= 2);
        Assert.Equal(single0 + 1 + valueBytes, both); // one more mask byte, one more value
    }

    // 5. Memo interplay: a signature-matched second export (same tick, same dirty set) is a
    //    memo hit and lands byte-identical, compaction included.
    [NebulaUnitTest]
    public void MemoHit_IsByteIdentical_AtWideMask()
    {
        using var f = new Fixture(24);
        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = (1L << 3) | (1L << 20);
        f.Serializer.Begin();

        var first = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, first, int.MaxValue));
        Assert.Equal(0, f.Serializer.MemoHitsForTests);

        var second = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, second, int.MaxValue));
        Assert.Equal(1, f.Serializer.MemoHitsForTests);
        Assert.True(first.WrittenSpan.SequenceEqual(second.WrittenSpan));
        Assert.Equal(0b101, second.WrittenSpan[0]);
    }

    // 6. Budget: one byte under the full section still self-limits (never exceeds
    //    maxBytes), reports Partial, and banks the leftover. The checks measure against
    //    the reserved (worst-case) mask width, so the shipped section is comfortably
    //    inside the budget rather than exactly at it.
    [NebulaUnitTest]
    public void TightBudget_SelfLimits_AtWideMask()
    {
        int fullSize;
        using (var sizing = new Fixture(24))
        {
            var buf = Buffer();
            Assert.Equal(ExportResult.Written, sizing.Export(1, buf, int.MaxValue, 0, 1, 2));
            fullSize = buf.WrittenSpan.Length;
        }

        using var f = new Fixture(24);
        var tight = Buffer();
        int maxBytes = fullSize - 1;
        var result = f.Export(1, tight, maxBytes, 0, 1, 2);

        Assert.Equal(ExportResult.Partial, result);
        Assert.True(tight.WrittenSpan.Length <= maxBytes);
        Assert.Equal(0b001, tight.WrittenSpan[0]);          // still byte 0 only
        Assert.NotEqual(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 7. Nothing dirty writes nothing at all - no header, no age.
    [NebulaUnitTest]
    public void NothingDirty_WritesNothing()
    {
        using var f = new Fixture(24);
        var buf = Buffer();
        Assert.Equal(ExportResult.None, f.Export(1, buf, int.MaxValue));
        Assert.Equal(0, buf.WrittenSpan.Length);
    }

    // 8. Read side: an empty two-level mask is [0x00][age] and consumes exactly two bytes.
    [NebulaUnitTest]
    public void Decode_EmptyHeader_ConsumesHeaderAndAge()
    {
        using var f = new Fixture(24);
        var wire = new NetBuffer(16, usePool: false);
        NetWriter.WriteByte(wire, 0x00);
        NetWriter.WriteByte(wire, AbsoluteAge);
        wire.ResetRead();

        Assert.True(f.Serializer.DeserializeForTests(wire, 1));
        Assert.Equal(2, wire.ReadPosition);
    }

    // 9. Read side: a header naming a byte beyond the mask throws, which ImportState turns
    //    into an aborted, un-acked tick.
    [NebulaUnitTest]
    public void Decode_CorruptHeader_Throws()
    {
        using var f = new Fixture(24);   // 3 mask bytes: header bits 0..2 are valid
        var wire = new NetBuffer(16, usePool: false);
        NetWriter.WriteByte(wire, 0xFF);
        NetWriter.WriteByte(wire, AbsoluteAge);
        wire.ResetRead();

        Assert.Throws<InvalidOperationException>(() => f.Serializer.DeserializeForTests(wire, 1));
    }
}
