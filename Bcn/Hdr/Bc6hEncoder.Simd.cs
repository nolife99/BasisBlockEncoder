// BC6H encoder SIMD kernels, written against the width-agnostic System.Numerics.Vector<T> so a single
// implementation lowers to the host vector width (128/256/512-bit) under the JIT. The 16-texel block is
// divisible by every supported width, so each width tiles it exactly. There are no unsafe blocks and no
// width-specific intrinsics, for portability across x86 and ARM.
//
// The lane-parallel weight-assignment kernels (single- and two-subset) and the candidate-color generation
// are bit-identical to the scalar path: they use the same FMA form and accumulate the error in scalar order.
// Covariance is not bit-identical, because its horizontal reduction reorders the sum; in practice it selects
// the same principal axis and produces the same encoded block.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bcn.Hdr;

internal static partial class Bc6hEncoder
{
    private static readonly int[] Weight4I = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
    private static readonly int[] Weight3I = { 0, 9, 18, 27, 37, 46, 55, 64 };

    // SIMD is used when the hardware is vector-accelerated and the width divides the 16-texel block. The
    // width comparison is a compile-time constant the JIT folds away.
    private static bool UseSimdWeights => Vector.IsHardwareAccelerated && (16 % Vector<float>.Count) == 0;
    private static bool UseSimdGeometry => Vector.IsHardwareAccelerated && (16 % Vector<float>.Count) == 0;

    // Portable variable-width load/store from a ref + element offset. Equivalent to Vector<T>.LoadUnsafe /
    // StoreUnsafe (added in .NET 8) but using the span constructor and CopyTo, which are available on every
    // targeted framework. The span is created with exactly Vector<T>.Count elements.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<T> LoadVec<T>(ref T baseRef, nuint offset) where T : struct
        => new Vector<T>(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref baseRef, (nint)offset), Vector<T>.Count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreVec<T>(Vector<T> value, ref T baseRef, nuint offset) where T : struct
        => value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref baseRef, (nint)offset), Vector<T>.Count));

    // half-bits to positive-finite float: (h << 13) reinterpreted, scaled by 2^112. Bit-identical to HtoF.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> HtoFVec(Vector<int> hbits)
        => Vector.AsVectorSingle(Vector.ShiftLeft(hbits, 13)) * new Vector<float>(HtoFK);

    // 16 candidate colors interpolated between the (min,max) endpoints. Bit-identical to scalar.
    private static void ComputeCandidatesSimd(
        int minR, int minG, int minB, int maxR, int maxG, int maxB, Span<float> cr, Span<float> cg, Span<float> cb)
    {
        int W = Vector<float>.Count;
        var v64 = new Vector<int>(64); var v32 = new Vector<int>(32);
        var minRv = new Vector<int>(minR); var maxRv = new Vector<int>(maxR);
        var minGv = new Vector<int>(minG); var maxGv = new Vector<int>(maxG);
        var minBv = new Vector<int>(minB); var maxBv = new Vector<int>(maxB);
        ref int w4 = ref Weight4I[0];
        ref float crr = ref MemoryMarshal.GetReference(cr);
        ref float cgr = ref MemoryMarshal.GetReference(cg);
        ref float cbr = ref MemoryMarshal.GetReference(cb);
        for (nuint b = 0; b < 16; b += (nuint)W)
        {
            var w = LoadVec(ref w4, b);
            var w64 = v64 - w;
            StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minRv * w64 + maxRv * w + v32, 6)), ref crr, b);
            StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minGv * w64 + maxGv * w + v32, 6)), ref cgr, b);
            StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minBv * w64 + maxBv * w + v32, 6)), ref cbr, b);
        }
    }

    // 16 candidate colors for two subsets (8 each). cr/cg/cb are indexed [s*8+j]. Bit-identical to scalar.
    // The caller invokes this only when 8 % Vector<float>.Count == 0 (widths 4 and 8); at width 16 the
    // two-subset path stays scalar, because 8 candidates underfill a 512-bit vector.
    private static void ComputeCandidates3Simd(ReadOnlySpan<int> sMin, ReadOnlySpan<int> sMax, Span<float> cr, Span<float> cg, Span<float> cb)
    {
        int W = Vector<float>.Count;
        var v64 = new Vector<int>(64); var v32 = new Vector<int>(32);
        ref int w3 = ref Weight3I[0];
        ref float crr = ref MemoryMarshal.GetReference(cr);
        ref float cgr = ref MemoryMarshal.GetReference(cg);
        ref float cbr = ref MemoryMarshal.GetReference(cb);
        for (int s = 0; s < 2; s++)
        {
            var minR = new Vector<int>(sMin[0 * 2 + s]); var maxR = new Vector<int>(sMax[0 * 2 + s]);
            var minG = new Vector<int>(sMin[1 * 2 + s]); var maxG = new Vector<int>(sMax[1 * 2 + s]);
            var minB = new Vector<int>(sMin[2 * 2 + s]); var maxB = new Vector<int>(sMax[2 * 2 + s]);
            for (nuint j = 0; j < 8; j += (nuint)W)
            {
                nuint o = (nuint)(s * 8) + j;
                var w = LoadVec(ref w3, j);
                var w64 = v64 - w;
                StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minR * w64 + maxR * w + v32, 6)), ref crr, o);
                StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minG * w64 + maxG * w + v32, 6)), ref cgr, o);
                StoreVec(HtoFVec(Vector.ShiftRightArithmetic(minB * w64 + maxB * w + v32, 6)), ref cbr, o);
            }
        }
    }

    // Brute-force weight assignment, lane-parallel over texels: for each of the 16 candidates, the squared
    // distance to every lane, tracked against a running per-lane (min, argmin). The minimum is the error, so
    // no gather is needed. The tie-break is strict-less with candidates considered 0..15 in order, matching
    // scalar; the error is summed in scalar order so it is bit-exact.
    private static double BrutePathSimd(
        ReadOnlySpan<float> cr, ReadOnlySpan<float> cg, ReadOnlySpan<float> cb,
        ReadOnlySpan<float> qr16, ReadOnlySpan<float> qg16, ReadOnlySpan<float> qb16, ReadOnlySpan<float> scales,
        Span<byte> weights)
    {
        int W = Vector<float>.Count;
        Span<float> errs = stackalloc float[16];
        ref float qrr = ref MemoryMarshal.GetReference(qr16);
        ref float qgr = ref MemoryMarshal.GetReference(qg16);
        ref float qbr = ref MemoryMarshal.GetReference(qb16);
        ref float errr = ref MemoryMarshal.GetReference(errs);
        for (nuint bse = 0; bse < 16; bse += (nuint)W)
        {
            var qr = LoadVec(ref qrr, bse);
            var qg = LoadVec(ref qgr, bse);
            var qb = LoadVec(ref qbr, bse);
            var d0r = new Vector<float>(cr[0]) - qr; var d0g = new Vector<float>(cg[0]) - qg; var d0b = new Vector<float>(cb[0]) - qb;
            var bestE = d0r * d0r + d0g * d0g + d0b * d0b;
            var bestIdx = Vector<int>.Zero;
            for (int j = 1; j < 16; j++)
            {
                var dr = new Vector<float>(cr[j]) - qr; var dg = new Vector<float>(cg[j]) - qg; var db = new Vector<float>(cb[j]) - qb;
                var e = dr * dr + dg * dg + db * db;
                var m = Vector.LessThan(e, bestE);
                bestE = Vector.ConditionalSelect(m, e, bestE);
                bestIdx = Vector.ConditionalSelect(Vector.AsVectorInt32(m), new Vector<int>(j), bestIdx);
            }
            StoreVec(bestE, ref errr, bse);
            for (int k = 0; k < W; k++) weights[(int)bse + k] = (byte)bestIdx[k];
        }
        double total = 0;
        for (int i = 0; i < 16; i++) total += errs[i] * scales[i];
        return total;
    }

    // Two-subset 3-bit weight assignment, lane-parallel over texels. Each candidate color is blended per lane
    // by the texel's subset bit (the mask from patBits), since lanes can straddle both subsets. Bit-identical.
    private static double AssignWeights3Simd(
        Span<byte> weights, uint patBits, ReadOnlySpan<float> cr, ReadOnlySpan<float> cg, ReadOnlySpan<float> cb,
        ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> ps, bool computeError)
    {
        int W = Vector<float>.Count;
        Span<int> subMask = stackalloc int[16];
        for (int i = 0; i < 16; i++) subMask[i] = -(int)((patBits >> i) & 1);   // -1 (all-ones) for subset 1, else 0
        Span<float> errs = stackalloc float[16];
        ref float frr = ref MemoryMarshal.GetReference(fr);
        ref float fgr = ref MemoryMarshal.GetReference(fg);
        ref float fbr = ref MemoryMarshal.GetReference(fb);
        ref int smr = ref subMask[0];
        ref float errr = ref MemoryMarshal.GetReference(errs);
        for (nuint bse = 0; bse < 16; bse += (nuint)W)
        {
            var qr = LoadVec(ref frr, bse);
            var qg = LoadVec(ref fgr, bse);
            var qb = LoadVec(ref fbr, bse);
            var mask = Vector.AsVectorSingle(LoadVec(ref smr, bse));
            var c0r = Vector.ConditionalSelect(mask, new Vector<float>(cr[8]), new Vector<float>(cr[0])) - qr;
            var c0g = Vector.ConditionalSelect(mask, new Vector<float>(cg[8]), new Vector<float>(cg[0])) - qg;
            var c0b = Vector.ConditionalSelect(mask, new Vector<float>(cb[8]), new Vector<float>(cb[0])) - qb;
            var bestE = c0r * c0r + c0g * c0g + c0b * c0b;
            var bestIdx = Vector<int>.Zero;
            for (int j = 1; j < 8; j++)
            {
                var dr = Vector.ConditionalSelect(mask, new Vector<float>(cr[8 + j]), new Vector<float>(cr[j])) - qr;
                var dg = Vector.ConditionalSelect(mask, new Vector<float>(cg[8 + j]), new Vector<float>(cg[j])) - qg;
                var db = Vector.ConditionalSelect(mask, new Vector<float>(cb[8 + j]), new Vector<float>(cb[j])) - qb;
                var e = dr * dr + dg * dg + db * db;
                var m = Vector.LessThan(e, bestE);
                bestE = Vector.ConditionalSelect(m, e, bestE);
                bestIdx = Vector.ConditionalSelect(Vector.AsVectorInt32(m), new Vector<int>(j), bestIdx);
            }
            StoreVec(bestE, ref errr, bse);
            for (int k = 0; k < W; k++) weights[(int)bse + k] = (byte)bestIdx[k];
        }
        double total = 0;
        if (computeError) for (int i = 0; i < 16; i++) total += errs[i] * ps[i];
        return total;
    }

    // Covariance about the integer mean via lane-parallel float FMA. Not bit-identical to scalar, because the
    // horizontal reduction (Vector.Sum) reorders the sum.
    private static void CovarianceSimd(
        ReadOnlySpan<int> ir, ReadOnlySpan<int> ig, ReadOnlySpan<int> ib, int meanR, int meanG, int meanB,
        out float cov0, out float cov1, out float cov2, out float cov3, out float cov4, out float cov5, out float blockMaxVar)
    {
        int W = Vector<float>.Count;
        ref int irr = ref MemoryMarshal.GetReference(ir);
        ref int igr = ref MemoryMarshal.GetReference(ig);
        ref int ibr = ref MemoryMarshal.GetReference(ib);
        var mR = new Vector<int>(meanR); var mG = new Vector<int>(meanG); var mB = new Vector<int>(meanB);
        var c0 = Vector<float>.Zero; var c1 = c0; var c2 = c0; var c3 = c0; var c4 = c0; var c5 = c0;
        for (nuint b = 0; b < 16; b += (nuint)W)
        {
            var r = Vector.ConvertToSingle(LoadVec(ref irr, b) - mR);
            var g = Vector.ConvertToSingle(LoadVec(ref igr, b) - mG);
            var bl = Vector.ConvertToSingle(LoadVec(ref ibr, b) - mB);
            c0 += r * r; c1 += r * g; c2 += r * bl; c3 += g * g; c4 += g * bl; c5 += bl * bl;
        }
        cov0 = Vector.Sum(c0); cov1 = Vector.Sum(c1); cov2 = Vector.Sum(c2);
        cov3 = Vector.Sum(c3); cov4 = Vector.Sum(c4); cov5 = Vector.Sum(c5);
        blockMaxVar = MathF.Max(cov0, MathF.Max(cov3, cov5));
    }

    // Projection onto the principal axis: the dot products only (lane-parallel FMA), which are bit-identical;
    // the argmin/argmax over the dots stays scalar to keep the extreme-index tie-break exact.
    private static void ProjectionSimd(
        ReadOnlySpan<int> ir, ReadOnlySpan<int> ig, ReadOnlySpan<int> ib, float axisR, float axisG, float axisB, Span<float> dots)
    {
        int W = Vector<float>.Count;
        ref int irr = ref MemoryMarshal.GetReference(ir);
        ref int igr = ref MemoryMarshal.GetReference(ig);
        ref int ibr = ref MemoryMarshal.GetReference(ib);
        ref float dr = ref MemoryMarshal.GetReference(dots);
        var aR = new Vector<float>(axisR); var aG = new Vector<float>(axisG); var aB = new Vector<float>(axisB);
        for (nuint b = 0; b < 16; b += (nuint)W)
        {
            var r = Vector.ConvertToSingle(LoadVec(ref irr, b));
            var g = Vector.ConvertToSingle(LoadVec(ref igr, b));
            var bl = Vector.ConvertToSingle(LoadVec(ref ibr, b));
            StoreVec(r * aR + g * aG + bl * aB, ref dr, b);
        }
    }

    // Two-subset candidate generation vectorizes only when the width divides 8 (widths 4 and 8); at width 16
    // it stays scalar.
    private static bool Candidates3Vectorizable => (8 % Vector<float>.Count) == 0;
}
