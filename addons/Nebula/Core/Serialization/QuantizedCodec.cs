using System;
using Godot;
using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Wire encoding of a quantized property (<c>[NetProperty(Quantize = step)]</c>), shared by
    /// the server writer and the client reader so the two can never disagree.
    ///
    /// <para>A quantized value is N signed integer step counts ("codes"): float N=1, Vector2
    /// N=2, Vector3 N=3, unit-vector Vector3 N=2 (octahedral u, w). Codes travel either
    /// absolute (N zigzag varints), as a SMALL delta packed into one word (3x10 bits in a
    /// uint32, 2x12 bits in three bytes, one int16), or as a FULL delta (N zigzag varints).
    /// Quaternions are the exception: smallest-three packed into a uint32 at
    /// <see cref="ResolveQuatBits"/> bits per component, absolute only.</para>
    ///
    /// <para><b>Exactness contract.</b> Deltas are exact because both sides compute
    /// <c>Quantize(baseline)</c> from bit-identical floats with identical code: the server's
    /// delta ring holds <see cref="Canonicalize"/>d values, the client holds the
    /// <see cref="Decode"/> of the codes it received, and both are the same
    /// <c>Dequantize(codes)</c>. That does NOT require <c>Quantize(Dequantize(q)) == q</c>;
    /// on the octahedral fold seams it is legitimately false (the mirrored code comes back),
    /// which merely turns that tick's delta into the full form. What it does require is that
    /// <see cref="Dequantize"/> and <see cref="OctDecode"/> are deterministic across
    /// machines: they use only IEEE + - * / and sqrt, never a transcendental.</para>
    ///
    /// <para><b>Magnitude limit.</b> <c>v / step</c> is round-robust in float32 only while
    /// <c>|v| &lt; ~0.5 * step * 2^24</c> (about 84,000 units at a 0.01 step). Beyond that the
    /// grid is coarser than the step, still exact on the wire but no longer the declared
    /// resolution. Codes are clamped to int range rather than left to the platform's
    /// out-of-range float-to-int conversion, which differs between x86 and ARM.</para>
    /// </summary>
    internal static class QuantizedCodec
    {
        /// <summary>
        /// Most bits per smallest-three component: 2 index bits + 3 * 10 fits a uint32.
        /// </summary>
        public const int MaxQuatBits = 10;
        public const int MinQuatBits = 2;
        private const int QuatIndexBits = 2;

        /// <summary>Largest component count of any quantized type (Vector3).</summary>
        public const int MaxComponents = 3;

        // Small-delta word shapes. Ranges are the per-component limits; a delta outside falls
        // back to the full varint form.
        private const int Small3Bits = 10;
        private const int Small3Bias = 1 << (Small3Bits - 1);          // 512
        private const int Small3Max = Small3Bias - 1;                    // 511
        private const int Small3Bytes = 4;
        private const int Small2Bits = 12;
        private const int Small2Bias = 1 << (Small2Bits - 1);          // 2048
        private const int Small2Max = Small2Bias - 1;                    // 2047
        private const int Small2Bytes = 3;

        /// <summary>Smallest-three components live in [-1/sqrt2, 1/sqrt2].</summary>
        private const float QuatHalfRange = 0.70710678f;

        /// <summary>
        /// Inputs whose squared length is below this are not directions (a Vector3.Zero
        /// sentinel, an uninitialised value) and encode as +Y rather than as NaN.
        /// </summary>
        private const float UnitMinLengthSquared = 0.5f;
        private static bool _sentinelWarned;

        // ───────────────────────────── metadata ─────────────────────────────

        public static int ComponentCount(SerialVariantType type, bool unitVector)
        {
            return type switch
            {
                SerialVariantType.Float => 1,
                SerialVariantType.Vector2 => 2,
                SerialVariantType.Vector3 => unitVector ? 2 : 3,
                _ => 0,
            };
        }

        /// <summary>True for the types <c>Quantize</c> is allowed on (NEBULA010).</summary>
        public static bool IsQuantizable(SerialVariantType type)
        {
            return type is SerialVariantType.Float or SerialVariantType.Vector2
                or SerialVariantType.Vector3 or SerialVariantType.Quaternion;
        }

        /// <summary>
        /// Bits per smallest-three component for a declared step: the fewest that make the
        /// component quantum no larger than the step, capped at <see cref="MaxQuatBits"/>.
        /// </summary>
        public static byte ResolveQuatBits(float step)
        {
            if (step <= 0f) return 0;
            int bits = MinQuatBits;
            while (bits < MaxQuatBits && QuatQuantum(bits) > step) bits++;
            return (byte)bits;
        }

        /// <summary>
        /// Code range at a bit width: one level fewer than the width allows, so the level
        /// count is odd and ZERO is a code. A resting rotation (identity, or any axis-aligned
        /// quarter turn) then replicates exactly instead of as a half-quantum wobble.
        /// </summary>
        private static uint QuatMaxCode(int bits) => (1u << bits) - 2;

        /// <summary>The component quantum actually used at a bit width.</summary>
        public static float QuatQuantum(int bits) => 2f * QuatHalfRange / QuatMaxCode(bits);

        /// <summary>
        /// Worst-case error the encoding introduces, in the units the prediction tolerance of
        /// that type is authored in: Euclidean distance for float/Vector2/Vector3 and for unit
        /// vectors (chord length, ~radians), radians for a quaternion (AngleTo). Bounds verified
        /// by brute force in QuantizedCodecTests.
        /// </summary>
        public static float MaxError(SerialVariantType type, bool unitVector, float step)
        {
            switch (type)
            {
                case SerialVariantType.Float: return step * 0.5f;
                case SerialVariantType.Vector2: return step * 0.5f * 1.41421356f;
                case SerialVariantType.Vector3:
                    // Octahedral: the square-to-sphere map's largest singular value is 3, at
                    // the octant centres (1,1,1)/sqrt3, along the (u, w) diagonal. The worst
                    // rounding error in the square is half a step on both axes, sqrt2 * step/2
                    // along that diagonal, so 3 * sqrt2 / 2 = 2.12 steps on the sphere; 2.2
                    // leaves room for float rounding. The fold is an isometry of the square,
                    // so the lower hemisphere has the same bound.
                    return unitVector ? step * 2.2f : step * 0.5f * 1.73205081f;
                case SerialVariantType.Quaternion:
                    // Three components each off by up to half a quantum, plus the
                    // reconstructed largest component's second-order error; angle ~ 2|dq|.
                    return 4f * QuatQuantum(ResolveQuatBits(step));
                default: return 0f;
            }
        }

        // ───────────────────────────── grid ─────────────────────────────

        public static int Quantize(float v, float step)
        {
            float r = MathF.Round(v / step);
            // Clamp in double: (float)int.MaxValue rounds up to 2^31, which is itself out of
            // range, and an out-of-range cast is platform-defined.
            if (r >= 2147483647.0) return int.MaxValue;
            if (r <= -2147483648.0) return int.MinValue;
            if (float.IsNaN(r)) return 0;
            return (int)r;
        }

        public static float Dequantize(int q, float step) => q * step;

        // ───────────────────────────── octahedral ─────────────────────────────

        private static float Sgn(float f) => f >= 0f ? 1f : -1f;

        /// <summary>
        /// Octahedral projection with +Y as the fold axis, so the seams (where a direction has
        /// two equivalent codes) lie in the -Y hemisphere, away from the "up" every direction
        /// property rests at. Non-directions (see <see cref="UnitMinLengthSquared"/>) encode
        /// as +Y with a one-time warning.
        /// </summary>
        public static void OctEncode(Vector3 v, out float u, out float w)
        {
            float lengthSq = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            if (!(lengthSq >= UnitMinLengthSquared))
            {
                if (!_sentinelWarned)
                {
                    _sentinelWarned = true;
                    GD.PushWarning($"QuantizedCodec: a UnitVector property was written with a non-direction {v}; sent as +Y. Properties that use Vector3.Zero as a sentinel must not set UnitVector.");
                }
                u = 0f; w = 0f;
                return;
            }
            float l1 = MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z);
            float px = v.X / l1;
            float pz = v.Z / l1;
            if (v.Y < 0f)
            {
                float fx = (1f - MathF.Abs(pz)) * Sgn(px);
                float fz = (1f - MathF.Abs(px)) * Sgn(pz);
                px = fx; pz = fz;
            }
            u = px; w = pz;
        }

        /// <summary>Inverse of <see cref="OctEncode"/>; the result is renormalised.</summary>
        public static Vector3 OctDecode(float u, float w)
        {
            float y = 1f - MathF.Abs(u) - MathF.Abs(w);
            float x = u;
            float z = w;
            if (y < 0f)
            {
                float fx = (1f - MathF.Abs(w)) * Sgn(u);
                float fz = (1f - MathF.Abs(u)) * Sgn(w);
                x = fx; z = fz;
            }
            float len = MathF.Sqrt(x * x + y * y + z * z);
            return new Vector3(x / len, y / len, z / len);
        }

        // ───────────────────────────── value <-> codes ─────────────────────────────

        /// <summary>Fills <paramref name="codes"/> (length &gt;= ComponentCount) from a value.</summary>
        public static void Encode(in PropertyCache value, SerialVariantType type, bool unitVector, float step, Span<int> codes)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    codes[0] = Quantize(value.FloatValue, step);
                    break;
                case SerialVariantType.Vector2:
                    codes[0] = Quantize(value.Vec2Value.X, step);
                    codes[1] = Quantize(value.Vec2Value.Y, step);
                    break;
                case SerialVariantType.Vector3:
                    if (unitVector)
                    {
                        OctEncode(value.Vec3Value, out float u, out float w);
                        codes[0] = Quantize(u, step);
                        codes[1] = Quantize(w, step);
                    }
                    else
                    {
                        codes[0] = Quantize(value.Vec3Value.X, step);
                        codes[1] = Quantize(value.Vec3Value.Y, step);
                        codes[2] = Quantize(value.Vec3Value.Z, step);
                    }
                    break;
                default:
                    throw new NotSupportedException($"QuantizedCodec.Encode: {type} is not a grid type");
            }
        }

        /// <summary>Writes the value the codes denote into the cache (type-tagged).</summary>
        public static void Decode(ReadOnlySpan<int> codes, SerialVariantType type, bool unitVector, float step, ref PropertyCache cache)
        {
            cache.Type = type;
            switch (type)
            {
                case SerialVariantType.Float:
                    cache.FloatValue = Dequantize(codes[0], step);
                    break;
                case SerialVariantType.Vector2:
                    cache.Vec2Value = new Vector2(Dequantize(codes[0], step), Dequantize(codes[1], step));
                    break;
                case SerialVariantType.Vector3:
                    cache.Vec3Value = unitVector
                        ? OctDecode(Dequantize(codes[0], step), Dequantize(codes[1], step))
                        : new Vector3(Dequantize(codes[0], step), Dequantize(codes[1], step), Dequantize(codes[2], step));
                    break;
                default:
                    throw new NotSupportedException($"QuantizedCodec.Decode: {type} is not a grid type");
            }
        }

        /// <summary>
        /// Replaces the value with what a client holds after receiving it: the exact float the
        /// server's delta ring must carry for deltas against it to be exact.
        /// </summary>
        public static void Canonicalize(ref PropertyCache cache, SerialVariantType type, bool unitVector, float step)
        {
            Span<int> codes = stackalloc int[MaxComponents];
            Encode(in cache, type, unitVector, step, codes);
            Decode(codes, type, unitVector, step, ref cache);
        }

        // ───────────────────────────── quaternion ─────────────────────────────

        /// <summary>
        /// Smallest-three at <paramref name="bits"/> per component, largest forced positive,
        /// components rounded (not truncated) to the grid: [index:2][a][b][c] in a uint32.
        /// </summary>
        public static uint PackQuat(Quaternion q, int bits)
        {
            float absX = MathF.Abs(q.X), absY = MathF.Abs(q.Y), absZ = MathF.Abs(q.Z), absW = MathF.Abs(q.W);
            int maxIndex = 0;
            float maxVal = absX;
            if (absY > maxVal) { maxIndex = 1; maxVal = absY; }
            if (absZ > maxVal) { maxIndex = 2; maxVal = absZ; }
            if (absW > maxVal) { maxIndex = 3; }

            float sign = maxIndex switch
            {
                0 => Sgn(q.X),
                1 => Sgn(q.Y),
                2 => Sgn(q.Z),
                _ => Sgn(q.W),
            };
            float a, b, c;
            switch (maxIndex)
            {
                case 0: a = q.Y * sign; b = q.Z * sign; c = q.W * sign; break;
                case 1: a = q.X * sign; b = q.Z * sign; c = q.W * sign; break;
                case 2: a = q.X * sign; b = q.Y * sign; c = q.W * sign; break;
                default: a = q.X * sign; b = q.Y * sign; c = q.Z * sign; break;
            }

            uint maxCode = QuatMaxCode(bits);
            uint ua = QuatComponentCode(a, maxCode);
            uint ub = QuatComponentCode(b, maxCode);
            uint uc = QuatComponentCode(c, maxCode);
            return (uint)maxIndex | (ua << QuatIndexBits) | (ub << (QuatIndexBits + bits)) | (uc << (QuatIndexBits + 2 * bits));
        }

        private static uint QuatComponentCode(float component, uint maxCode)
        {
            float t = (component + QuatHalfRange) / (2f * QuatHalfRange) * maxCode;
            float r = MathF.Round(t);
            if (r <= 0f) return 0;
            if (r >= maxCode) return maxCode;
            return (uint)r;
        }

        public static Quaternion UnpackQuat(uint packed, int bits)
        {
            uint mask = (1u << bits) - 1;
            uint maxCode = QuatMaxCode(bits);
            int maxIndex = (int)(packed & ((1u << QuatIndexBits) - 1));
            uint ua = Math.Min((packed >> QuatIndexBits) & mask, maxCode);
            uint ub = Math.Min((packed >> (QuatIndexBits + bits)) & mask, maxCode);
            uint uc = Math.Min((packed >> (QuatIndexBits + 2 * bits)) & mask, maxCode);
            float scale = 2f * QuatHalfRange / maxCode;
            float a = ua * scale - QuatHalfRange;
            float b = ub * scale - QuatHalfRange;
            float c = uc * scale - QuatHalfRange;
            // Rounding can push the three components' sum of squares past 1 (the largest is
            // then reconstructed as 0 and the result is short); renormalise so the client
            // never holds a non-unit rotation. Deterministic: sqrt and division only.
            float sumSq = a * a + b * b + c * c;
            float largest = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));
            float invLen = 1f / MathF.Sqrt(sumSq + largest * largest);
            a *= invLen; b *= invLen; c *= invLen; largest *= invLen;
            return maxIndex switch
            {
                0 => new Quaternion(largest, a, b, c),
                1 => new Quaternion(a, largest, b, c),
                2 => new Quaternion(a, b, largest, c),
                _ => new Quaternion(a, b, c, largest),
            };
        }

        // ───────────────────────────── wire forms ─────────────────────────────

        /// <summary>N zigzag varints.</summary>
        public static void WriteCodes(NetBuffer buffer, ReadOnlySpan<int> codes, int count)
        {
            for (int i = 0; i < count; i++) NetWriter.WriteZigZagVarInt(buffer, codes[i]);
        }

        public static void ReadCodes(NetBuffer buffer, Span<int> codes, int count)
        {
            for (int i = 0; i < count; i++) codes[i] = NetReader.ReadZigZagVarInt(buffer);
        }

        /// <summary>
        /// Packs the deltas into the small word for this component count if every one fits;
        /// writes nothing and returns false otherwise (the caller then takes the full form).
        /// </summary>
        public static bool TryWriteSmallDelta(NetBuffer buffer, ReadOnlySpan<int> deltas, int count)
        {
            switch (count)
            {
                case 1:
                    if (deltas[0] < short.MinValue || deltas[0] > short.MaxValue) return false;
                    NetWriter.WriteInt16(buffer, (short)deltas[0]);
                    return true;
                case 2:
                {
                    if (!Fits(deltas[0], Small2Max) || !Fits(deltas[1], Small2Max)) return false;
                    uint word = (uint)(deltas[0] + Small2Bias) | ((uint)(deltas[1] + Small2Bias) << Small2Bits);
                    var span = buffer.GetWriteSpan(Small2Bytes);
                    span[0] = (byte)word;
                    span[1] = (byte)(word >> 8);
                    span[2] = (byte)(word >> 16);
                    buffer.AdvanceWrite(Small2Bytes);
                    return true;
                }
                case 3:
                {
                    if (!Fits(deltas[0], Small3Max) || !Fits(deltas[1], Small3Max) || !Fits(deltas[2], Small3Max)) return false;
                    uint word = (uint)(deltas[0] + Small3Bias)
                        | ((uint)(deltas[1] + Small3Bias) << Small3Bits)
                        | ((uint)(deltas[2] + Small3Bias) << (2 * Small3Bits));
                    NetWriter.WriteUInt32(buffer, word);
                    return true;
                }
                default:
                    throw new NotSupportedException($"QuantizedCodec: {count} components");
            }
        }

        private static bool Fits(int delta, int max) => delta >= -max - 1 && delta <= max;

        public static void ReadSmallDelta(NetBuffer buffer, Span<int> deltas, int count)
        {
            switch (count)
            {
                case 1:
                    deltas[0] = NetReader.ReadInt16(buffer);
                    break;
                case 2:
                {
                    var span = buffer.GetReadSpan(Small2Bytes);
                    uint word = span[0] | ((uint)span[1] << 8) | ((uint)span[2] << 16);
                    buffer.AdvanceRead(Small2Bytes);
                    deltas[0] = (int)(word & (Small2Bias * 2 - 1)) - Small2Bias;
                    deltas[1] = (int)((word >> Small2Bits) & (Small2Bias * 2 - 1)) - Small2Bias;
                    break;
                }
                case 3:
                {
                    uint word = NetReader.ReadUInt32(buffer);
                    deltas[0] = (int)(word & (Small3Bias * 2 - 1)) - Small3Bias;
                    deltas[1] = (int)((word >> Small3Bits) & (Small3Bias * 2 - 1)) - Small3Bias;
                    deltas[2] = (int)((word >> (2 * Small3Bits)) & (Small3Bias * 2 - 1)) - Small3Bias;
                    break;
                }
                default:
                    throw new NotSupportedException($"QuantizedCodec: {count} components");
            }
        }

        /// <summary>Byte cost of the small form, for budget and census reasoning.</summary>
        public static int SmallDeltaBytes(int count) => count switch { 1 => 2, 2 => Small2Bytes, 3 => Small3Bytes, _ => 0 };
    }
}
