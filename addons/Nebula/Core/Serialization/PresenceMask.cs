using System;
using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Wire encoding of a props section's presence mask (which of a scene's properties the
    /// section carries), shared by the server writer and the client reader so the two can
    /// never disagree.
    ///
    /// <para>A scene with N properties has a mask of <c>ceil(N/8)</c> bytes, and a typical
    /// section sets two or three bits of it: a 60-property player scene paid an 8-byte mask
    /// every tick to say that position and rotation changed. So for wide masks the wire
    /// carries a one-byte HEADER whose bit i means "mask byte i is nonzero", followed by only
    /// those bytes, in ascending order. The property index layout clusters a node's hot
    /// properties into one or two mask bytes, which is what makes this pay: 8 bytes become 2.</para>
    ///
    /// <para>Narrow masks stay flat. With one or two mask bytes, <c>1 + nonzero</c> can never
    /// come in under the flat width, so the header would only ever cost. Both sides derive the
    /// rule from the mask width alone - no per-section flag byte.</para>
    ///
    /// <para>The same scheme already exists for NetArray's chunked bool sync (one presence byte
    /// per eight words, client zero-fills); this is the property-mask version of it.</para>
    /// </summary>
    internal static class PresenceMask
    {
        /// <summary>
        /// Smallest mask width (in bytes) that uses the two-level encoding. Below this the
        /// header byte cannot win: at width 2 a single nonzero byte already costs the full flat
        /// width (1 + 1), and two nonzero bytes cost more (1 + 2).
        /// </summary>
        public const int TwoLevelMinBytes = 3;

        /// <summary>The header is one byte, so it can name at most eight mask bytes.</summary>
        public const int HeaderBytes = 1;

        /// <summary>
        /// Widest mask this encoding supports: one header bit per mask byte. Equal to the
        /// scene property ceiling (<see cref="BitConstants.MaxSceneProperties"/> = 64 bits), so
        /// nothing the generator admits can exceed it.
        /// </summary>
        public const int MaxMaskBytes = BitConstants.MaxSceneProperties / BitConstants.BitsInByte;

        public static bool UsesTwoLevel(int byteCount) => byteCount >= TwoLevelMinBytes;

        /// <summary>
        /// Bytes the writer must reserve before the mask's contents are known: the flat width,
        /// or header plus every mask byte when all of them turn out nonzero.
        /// </summary>
        public static int ReservedBytes(int byteCount)
            => UsesTwoLevel(byteCount) ? HeaderBytes + byteCount : byteCount;

        /// <summary>
        /// Encodes <paramref name="mask"/> into <paramref name="dst"/> and returns the bytes
        /// written. <paramref name="dst"/> must hold at least <see cref="ReservedBytes"/>.
        /// </summary>
        public static int Encode(ReadOnlySpan<byte> mask, Span<byte> dst)
        {
            if (mask.Length > MaxMaskBytes)
            {
                throw new InvalidOperationException(
                    $"Presence mask of {mask.Length} bytes exceeds the {MaxMaskBytes}-byte ceiling.");
            }

            if (!UsesTwoLevel(mask.Length))
            {
                mask.CopyTo(dst);
                return mask.Length;
            }

            byte header = 0;
            int written = HeaderBytes;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 0) continue;
                header |= (byte)(1 << i);
                dst[written++] = mask[i];
            }
            dst[0] = header;
            return written;
        }

        /// <summary>
        /// Reads a mask of exactly <c>mask.Length</c> bytes from <paramref name="buffer"/>.
        /// Every byte of <paramref name="mask"/> is written - the ones the header does not
        /// name are zeroed - so a caller may hand in reused scratch without clearing it.
        ///
        /// <para>Runs on untrusted bytes. Returns false, having consumed only the header, when
        /// the header names a byte beyond the mask's width: the stream cannot be realigned
        /// from there, so the caller must abort the whole tick import rather than continue.</para>
        /// </summary>
        public static bool Decode(NetBuffer buffer, Span<byte> mask)
        {
            if (mask.Length > MaxMaskBytes)
            {
                throw new InvalidOperationException(
                    $"Presence mask of {mask.Length} bytes exceeds the {MaxMaskBytes}-byte ceiling.");
            }

            if (!UsesTwoLevel(mask.Length))
            {
                for (int i = 0; i < mask.Length; i++)
                {
                    mask[i] = NetReader.ReadByte(buffer);
                }
                return true;
            }

            byte header = NetReader.ReadByte(buffer);
            // A header bit at or above the width names a byte that does not exist. (For an
            // 8-byte mask the shift yields 0 for any byte value, which is correct: every bit
            // is in range.)
            if ((header >> mask.Length) != 0)
            {
                return false;
            }

            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = (header & (1 << i)) != 0 ? NetReader.ReadByte(buffer) : (byte)0;
            }
            return true;
        }
    }
}
