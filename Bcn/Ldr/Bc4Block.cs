// Bc4Block.cs — BC4 / DXT5A single-channel block encoder (namespace Bcn.Ldr).
//
// Port of basist::encode_bc4 (== rgbcx::encode_bc4), the optimal fast min/max BC4 kernel
// (matches brute-force closest-of-8 for the chosen endpoints). Shared unchanged by BC4, 
// BC5 (two of these), and BC3's alpha.
//
// Tiering follows the BC7 encoder: width-adaptive SIMD with a scalar fallback. All arithmetic here is
// integer (multiply, add, signed compare, count, OR-shift), so every tier is order-independent and
// bit-identical to the scalar path. The only non-vectorized step is the final 48-bit selector
// pack, which is inherently scalar and cheap (as in BC7).
//
// Whether SIMD outperforms scalar for 16 inexpensive elements depends on the workload, so the tiers
// exist and the fastest is selected per width (wide lanes can lose when reductions and packing dominate).

namespace Bcn.Ldr;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

static partial class Bc4Block
{
    // s_tran base table (rgbcx s_tran0): crossing-count (0..7) -> BC4 selector code. Pixel i sits at
    // bit 3*i in the 48-bit selector word (row*12 + col*3 == 3*(4*row+col) == 3*i), so a single
    // table + `<< 3*i` reproduces upstream's four pre-shifted s_tran0..3 tables exactly.
    static readonly byte[] BaseTran =
    {
        1, 7, 6, 5, 4, 3, 2, 0
    };

    // Pre-shifted tables for the scalar path (from the reference encoder).
    static readonly uint[] STran0 =
    {
        1u, 7u, 6u, 5u, 4u, 3u, 2u, 0u
    };

    static readonly uint[] STran1 =
    {
        1u << 3, 7u << 3, 6u << 3, 5u << 3, 4u << 3, 3u << 3, 2u << 3, 0u << 3
    };

    static readonly uint[] STran2 =
    {
        1u << 6, 7u << 6, 6u << 6, 5u << 6, 4u << 6, 3u << 6, 2u << 6, 0u << 6
    };

    static readonly uint[] STran3 =
    {
        1u << 9, 7u << 9, 6u << 9, 5u << 9, 4u << 9, 3u << 9, 2u << 9, 0u << 9
    };

    /// <summary>Encode 16 texels' channel `c` (0=R,1=G,2=B,3=A) to an 8-byte BC4 block.</summary>
    // No V512 tier: a BC4 block is exactly one Vector512<int>, so V512 would halve the 7 threshold
    // compares — but the per-block cost is dominated by the parts that do not widen (the scalar 48-bit
    // selector pack, roughly 65%, plus the single min/max horizontal reduction and endpoint setup). A
    // V512 path would add wider codegen and the DOTNET_PreferredVectorBitWidth gating cost for no
    // meaningful gain, and would not apply on CPUs without AVX-512.
    internal static void Encode(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output, int channel)
    {
        if (Vector256.IsHardwareAccelerated) EncodeV256(pixels, output, channel);
        else if (Vector128.IsHardwareAccelerated) EncodeV128(pixels, output, channel);
        else EncodeScalar(pixels, output, channel);
    }

    // ---------------- scalar (fallback and reference path) ----------------
    internal static void EncodeScalar(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output, int channel)
    {
        Span<int> p = stackalloc int[16];
        switch (channel)
        {
            case 0:
                for (var i = 0; i < 16; i++) p[i] = pixels[i].R;
                break;

            case 1:
                for (var i = 0; i < 16; i++) p[i] = pixels[i].G;
                break;

            case 2:
                for (var i = 0; i < 16; i++) p[i] = pixels[i].B;
                break;

            default:
                for (var i = 0; i < 16; i++) p[i] = pixels[i].A;
                break;
        }

        EncodeChannel(p, output);
    }

    /// <summary>Scalar core over 16 pre-gathered channel values. Byte-identical to the reference encoder.</summary>
    internal static void EncodeChannel(scoped ReadOnlySpan<int> p, scoped Span<byte> output)
    {
        int minV = p[0], maxV = p[0];
        for (var i = 1; i < 16; i++)
        {
            var v = p[i];
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
        }

        output[0] = (byte)maxV;
        output[1] = (byte)minV;
        if (maxV == minV)
        {
            output.Slice(2, 6).Clear();
            return;
        }

        var delta = maxV - minV;
        int t0 = delta * 13, t1 = delta * 11, t2 = delta * 9, t3 = delta * 7, t4 = delta * 5, t5 = delta * 3, t6 = delta * 1;
        var bias = 4 - minV * 14;

        ref var s0 = ref MemoryMarshal.GetArrayDataReference(STran0);
        ref var s1 = ref MemoryMarshal.GetArrayDataReference(STran1);
        ref var s2 = ref MemoryMarshal.GetArrayDataReference(STran2);
        ref var s3 = ref MemoryMarshal.GetArrayDataReference(STran3);
        ref var pr = ref MemoryMarshal.GetReference(p);

        ulong a0 = 0, a1 = 0, a2 = 0, a3 = 0;
        for (var row = 0; row < 4; row++)
        {
            int b = row * 4, shift = row * 12;
            int v0 = Unsafe.Add(ref pr, b) * 14 + bias, v1 = Unsafe.Add(ref pr, b + 1) * 14 + bias;
            int v2 = Unsafe.Add(ref pr, b + 2) * 14 + bias, v3 = Unsafe.Add(ref pr, b + 3) * 14 + bias;
            a0 |= (ulong)Unsafe.Add(ref s0, Cross(v0, t0, t1, t2, t3, t4, t5, t6)) << shift;
            a1 |= (ulong)Unsafe.Add(ref s1, Cross(v1, t0, t1, t2, t3, t4, t5, t6)) << shift;
            a2 |= (ulong)Unsafe.Add(ref s2, Cross(v2, t0, t1, t2, t3, t4, t5, t6)) << shift;
            a3 |= (ulong)Unsafe.Add(ref s3, Cross(v3, t0, t1, t2, t3, t4, t5, t6)) << shift;
        }

        WritePacked(output, a0 | a1 | a2 | a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Cross(int v, int t0, int t1, int t2, int t3, int t4, int t5, int t6)
        => (v >= t0 ? 1 : 0) + (v >= t1 ? 1 : 0) + (v >= t2 ? 1 : 0) + (v >= t3 ? 1 : 0)
            + (v >= t4 ? 1 : 0) + (v >= t5 ? 1 : 0) + (v >= t6 ? 1 : 0);

    // ---------------- V256 (AVX2 / any 256-bit) ----------------
    internal static void EncodeV256(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> output, int channel)
    {
        var pru = MemoryMarshal.Cast<ColorRgba, uint>(px);
        var sh = channel * 8;
        var mask = Vector256.Create(0xFFu);
        var c0 = (Vector256.ShiftRightLogical(Vector256.Create(pru), (byte)sh) & mask).AsInt32();
        var c1 = (Vector256.ShiftRightLogical(Vector256.Create(pru.Slice(8)), (byte)sh) & mask).AsInt32();

        int minV = HMin(Vector256.Min(c0, c1)), maxV = HMax(Vector256.Max(c0, c1));
        output[0] = (byte)maxV;
        output[1] = (byte)minV;
        if (maxV == minV)
        {
            output.Slice(2, 6).Clear();
            return;
        }

        int delta = maxV - minV, bias = 4 - minV * 14;
        Vector256<int> f14 = Vector256.Create(14), bv = Vector256.Create(bias);
        Vector256<int> t0 = Vector256.Create(delta * 13), t1 = Vector256.Create(delta * 11), t2 = Vector256.Create(delta * 9),
            t3 = Vector256.Create(delta * 7), t4 = Vector256.Create(delta * 5), t5 = Vector256.Create(delta * 3), t6 = Vector256.Create(delta);

        Vector256<int> v0 = c0 * f14 + bv, v1 = c1 * f14 + bv;
        Vector256<int> n0 = CountGE(v0, t0, t1, t2, t3, t4, t5, t6), n1 = CountGE(v1, t0, t1, t2, t3, t4, t5, t6);

        Span<int> cnt = stackalloc int[16];
        n0.CopyTo(cnt);
        n1.CopyTo(cnt.Slice(8));
        PackFromCounts(output, cnt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<int> CountGE(Vector256<int> v,
        Vector256<int> t0,
        Vector256<int> t1,
        Vector256<int> t2,
        Vector256<int> t3,
        Vector256<int> t4,
        Vector256<int> t5,
        Vector256<int> t6)
    {
        // GreaterThanOrEqual yields -1 where true, so subtracting accumulates the crossing count (0..7).
        var c = Vector256<int>.Zero;
        c -= Vector256.GreaterThanOrEqual(v, t0);
        c -= Vector256.GreaterThanOrEqual(v, t1);
        c -= Vector256.GreaterThanOrEqual(v, t2);
        c -= Vector256.GreaterThanOrEqual(v, t3);
        c -= Vector256.GreaterThanOrEqual(v, t4);
        c -= Vector256.GreaterThanOrEqual(v, t5);
        c -= Vector256.GreaterThanOrEqual(v, t6);
        return c;
    }

    // ---------------- V128 (NEON / SSE / any 128-bit) ----------------
    internal static void EncodeV128(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> output, int channel)
    {
        var pru = MemoryMarshal.Cast<ColorRgba, uint>(px);
        var sh = channel * 8;
        var mask = Vector128.Create(0xFFu);
        var g0 = (Vector128.ShiftRightLogical(Vector128.Create(pru), (byte)sh) & mask).AsInt32();
        var g1 = (Vector128.ShiftRightLogical(Vector128.Create(pru.Slice(4)), (byte)sh) & mask).AsInt32();
        var g2 = (Vector128.ShiftRightLogical(Vector128.Create(pru.Slice(8)), (byte)sh) & mask).AsInt32();
        var g3 = (Vector128.ShiftRightLogical(Vector128.Create(pru.Slice(12)), (byte)sh) & mask).AsInt32();

        var minV = HMin(Vector128.Min(Vector128.Min(g0, g1), Vector128.Min(g2, g3)));
        var maxV = HMax(Vector128.Max(Vector128.Max(g0, g1), Vector128.Max(g2, g3)));
        output[0] = (byte)maxV;
        output[1] = (byte)minV;
        if (maxV == minV)
        {
            output.Slice(2, 6).Clear();
            return;
        }

        int delta = maxV - minV, bias = 4 - minV * 14;
        Vector128<int> f14 = Vector128.Create(14), bv = Vector128.Create(bias);
        Vector128<int> t0 = Vector128.Create(delta * 13), t1 = Vector128.Create(delta * 11), t2 = Vector128.Create(delta * 9),
            t3 = Vector128.Create(delta * 7), t4 = Vector128.Create(delta * 5), t5 = Vector128.Create(delta * 3), t6 = Vector128.Create(delta);

        Span<int> cnt = stackalloc int[16];
        CountGE(g0 * f14 + bv, t0, t1, t2, t3, t4, t5, t6).CopyTo(cnt);
        CountGE(g1 * f14 + bv, t0, t1, t2, t3, t4, t5, t6).CopyTo(cnt.Slice(4));
        CountGE(g2 * f14 + bv, t0, t1, t2, t3, t4, t5, t6).CopyTo(cnt.Slice(8));
        CountGE(g3 * f14 + bv, t0, t1, t2, t3, t4, t5, t6).CopyTo(cnt.Slice(12));
        PackFromCounts(output, cnt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<int> CountGE(Vector128<int> v,
        Vector128<int> t0,
        Vector128<int> t1,
        Vector128<int> t2,
        Vector128<int> t3,
        Vector128<int> t4,
        Vector128<int> t5,
        Vector128<int> t6)
    {
        var c = Vector128<int>.Zero;
        c -= Vector128.GreaterThanOrEqual(v, t0);
        c -= Vector128.GreaterThanOrEqual(v, t1);
        c -= Vector128.GreaterThanOrEqual(v, t2);
        c -= Vector128.GreaterThanOrEqual(v, t3);
        c -= Vector128.GreaterThanOrEqual(v, t4);
        c -= Vector128.GreaterThanOrEqual(v, t5);
        c -= Vector128.GreaterThanOrEqual(v, t6);
        return c;
    }

    // ---------------- shared tail ----------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackFromCounts(scoped Span<byte> output, scoped ReadOnlySpan<int> cnt)
    {
        ref var bt = ref MemoryMarshal.GetArrayDataReference(BaseTran);
        ref var cr = ref MemoryMarshal.GetReference(cnt);
        ulong f = 0;
        for (var i = 0; i < 16; i++) f |= (ulong)Unsafe.Add(ref bt, Unsafe.Add(ref cr, i)) << 3 * i;
        WritePacked(output, f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void WritePacked(scoped Span<byte> output, ulong f)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output[2..], (uint)f);
        BinaryPrimitives.WriteUInt16LittleEndian(output[6..], (ushort)(f >> 32));
    }

    // horizontal min/max (same shape as the BC7 HMin/HMax helpers)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HMin(Vector128<int> m)
    {
        m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1)));
        m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
        return m.ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HMax(Vector128<int> m)
    {
        m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1)));
        m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
        return m.ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HMin(Vector256<int> v) => HMin(Vector128.Min(v.GetLower(), v.GetUpper()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HMax(Vector256<int> v) => HMax(Vector128.Max(v.GetLower(), v.GetUpper()));
}