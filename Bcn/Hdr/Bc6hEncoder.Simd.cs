// BC6H encoder SIMD kernels. The 16-texel block is divisible by every supported width, so each 
// width tiles it exactly. There are no width-specific intrinsics, for portability across x86 and ARM.
//
// The lane-parallel weight-assignment kernels (single- and two-subset) and the candidate-color generation
// are bit-identical to the scalar path: they use the same FMA form and accumulate the error in scalar order.
// Covariance is not bit-identical, because its horizontal reduction reorders the sum; in practice it selects
// the same principal axis and produces the same encoded block.

namespace Bcn.Hdr;

using System;
using System.Numerics;

static partial class Bc6hEncoder
{
    static readonly int[] Weight4I =
    {
        0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64
    };

    static readonly int[] Weight3I =
    {
        0, 9, 18, 27, 37, 46, 55, 64
    };

    static bool UseSimdWeights => Vector.IsHardwareAccelerated && 16 % Vector<float>.Count == 0;
    static bool UseSimdGeometry => Vector.IsHardwareAccelerated && 16 % Vector<float>.Count == 0;

    // Two-subset candidate generation vectorizes only when the width divides 8 (widths 4 and 8); at width 16
    // it stays scalar.
    static bool Candidates3Vectorizable => 8 % Vector<float>.Count == 0;

    // half-bits to positive-finite float: (h << 13) reinterpreted, scaled by 2^112
    static Vector<float> HtoFVec(Vector<int> hbits)
        => Vector.AsVectorSingle(Vector.ShiftLeft(hbits, 13)) * new Vector<float>(HtoFK);

    // 16 candidate colors interpolated between the (min,max) endpoints
    static void ComputeCandidatesSimd(
        int minR,
        int minG,
        int minB,
        int maxR,
        int maxG,
        int maxB,
        scoped Span<float> cr,
        scoped Span<float> cg,
        scoped Span<float> cb)
    {
        var W = Vector<float>.Count;
        var v64 = new Vector<int>(64);
        var v32 = new Vector<int>(32);
        var minRv = new Vector<int>(minR);
        var maxRv = new Vector<int>(maxR);
        var minGv = new Vector<int>(minG);
        var maxGv = new Vector<int>(maxG);
        var minBv = new Vector<int>(minB);
        var maxBv = new Vector<int>(maxB);
        var w4 = Weight4I.AsSpan();
        for (var b = 0; b < 16; b += W)
        {
            var w = new Vector<int>(w4.Slice(b));
            var w64 = v64 - w;
            HtoFVec(Vector.ShiftRightArithmetic(minRv * w64 + maxRv * w + v32, 6)).CopyTo(cr.Slice(b));
            HtoFVec(Vector.ShiftRightArithmetic(minGv * w64 + maxGv * w + v32, 6)).CopyTo(cg.Slice(b));
            HtoFVec(Vector.ShiftRightArithmetic(minBv * w64 + maxBv * w + v32, 6)).CopyTo(cb.Slice(b));
        }
    }

    // 16 candidate colors for two subsets (8 each). cr/cg/cb are indexed [s*8+j].
    // The caller invokes this only when 8 % Vector<float>.Count == 0 (widths 4 and 8); at width 16 the
    // two-subset path stays scalar, because 8 candidates underfill a 512-bit vector.
    static void ComputeCandidates3Simd(scoped ReadOnlySpan<int> sMin, scoped ReadOnlySpan<int> sMax, scoped Span<float> cr, scoped Span<float> cg, scoped Span<float> cb)
    {
        var W = Vector<float>.Count;
        var v64 = new Vector<int>(64);
        var v32 = new Vector<int>(32);
        var w3 = Weight3I.AsSpan();
        for (var s = 0; s < 2; s++)
        {
            var minR = new Vector<int>(sMin[0 * 2 + s]);
            var maxR = new Vector<int>(sMax[0 * 2 + s]);
            var minG = new Vector<int>(sMin[1 * 2 + s]);
            var maxG = new Vector<int>(sMax[1 * 2 + s]);
            var minB = new Vector<int>(sMin[2 * 2 + s]);
            var maxB = new Vector<int>(sMax[2 * 2 + s]);
            for (var j = 0; j < 8; j += W)
            {
                var o = s * 8 + j;
                var w = new Vector<int>(w3.Slice(j));
                var w64 = v64 - w;
                HtoFVec(Vector.ShiftRightArithmetic(minR * w64 + maxR * w + v32, 6)).CopyTo(cr.Slice(o));
                HtoFVec(Vector.ShiftRightArithmetic(minG * w64 + maxG * w + v32, 6)).CopyTo(cg.Slice(o));
                HtoFVec(Vector.ShiftRightArithmetic(minB * w64 + maxB * w + v32, 6)).CopyTo(cb.Slice(o));
            }
        }
    }

    // Brute-force weight assignment, lane-parallel over texels: for each of the 16 candidates, the squared
    // distance to every lane, tracked against a running per-lane (min, argmin). The minimum is the error, so
    // no gather is needed. The tie-break is strict-less with candidates considered 0..15 in order, matching
    // scalar; the error is summed in scalar order so it is bit-exact.
    static double BrutePathSimd(
        scoped ReadOnlySpan<float> cr,
        scoped ReadOnlySpan<float> cg,
        scoped ReadOnlySpan<float> cb,
        scoped ReadOnlySpan<float> qr16,
        scoped ReadOnlySpan<float> qg16,
        scoped ReadOnlySpan<float> qb16,
        scoped ReadOnlySpan<float> scales,
        scoped Span<byte> weights)
    {
        var W = Vector<float>.Count;
        Span<float> errs = stackalloc float[16];
        for (var bse = 0; bse < 16; bse += W)
        {
            var qr = new Vector<float>(qr16.Slice(bse));
            var qg = new Vector<float>(qg16.Slice(bse));
            var qb = new Vector<float>(qb16.Slice(bse));
            var d0r = new Vector<float>(cr[0]) - qr;
            var d0g = new Vector<float>(cg[0]) - qg;
            var d0b = new Vector<float>(cb[0]) - qb;
            var bestE = d0r * d0r + d0g * d0g + d0b * d0b;
            var bestIdx = Vector<int>.Zero;
            for (var j = 1; j < 16; j++)
            {
                var dr = new Vector<float>(cr[j]) - qr;
                var dg = new Vector<float>(cg[j]) - qg;
                var db = new Vector<float>(cb[j]) - qb;
                var e = dr * dr + dg * dg + db * db;
                var m = Vector.LessThan(e, bestE);
                bestE = Vector.ConditionalSelect(m, e, bestE);
                bestIdx = Vector.ConditionalSelect(Vector.AsVectorInt32(m), new(j), bestIdx);
            }

            bestE.CopyTo(errs.Slice(bse));
            for (var k = 0; k < W; k++) weights[bse + k] = (byte)bestIdx[k];
        }

        double total = 0;
        for (var i = 0; i < 16; i++) total += errs[i] * scales[i];
        return total;
    }

    // Two-subset 3-bit weight assignment, lane-parallel over texels. Each candidate color is blended per lane
    // by the texel's subset bit (the mask from patBits), since lanes can straddle both subsets
    static double AssignWeights3Simd(
        scoped Span<byte> weights,
        uint patBits,
        scoped ReadOnlySpan<float> cr,
        scoped ReadOnlySpan<float> cg,
        scoped ReadOnlySpan<float> cb,
        scoped ReadOnlySpan<float> fr,
        scoped ReadOnlySpan<float> fg,
        scoped ReadOnlySpan<float> fb,
        scoped ReadOnlySpan<float> ps,
        bool computeError)
    {
        var W = Vector<float>.Count;
        Span<int> subMask = stackalloc int[16];
        for (var i = 0; i < 16; i++) subMask[i] = -(int)(patBits >> i & 1); // -1 (all-ones) for subset 1, else 0
        Span<float> errs = stackalloc float[16];
        for (var bse = 0; bse < 16; bse += W)
        {
            var qr = new Vector<float>(fr.Slice(bse));
            var qg = new Vector<float>(fg.Slice(bse));
            var qb = new Vector<float>(fb.Slice(bse));
            var mask = Vector.AsVectorSingle(new Vector<int>(subMask.Slice(bse)));
            var c0r = Vector.ConditionalSelect(mask, new(cr[8]), new(cr[0])) - qr;
            var c0g = Vector.ConditionalSelect(mask, new(cg[8]), new(cg[0])) - qg;
            var c0b = Vector.ConditionalSelect(mask, new(cb[8]), new(cb[0])) - qb;
            var bestE = c0r * c0r + c0g * c0g + c0b * c0b;
            var bestIdx = Vector<int>.Zero;
            for (var j = 1; j < 8; j++)
            {
                var dr = Vector.ConditionalSelect(mask, new(cr[8 + j]), new(cr[j])) - qr;
                var dg = Vector.ConditionalSelect(mask, new(cg[8 + j]), new(cg[j])) - qg;
                var db = Vector.ConditionalSelect(mask, new(cb[8 + j]), new(cb[j])) - qb;
                var e = dr * dr + dg * dg + db * db;
                var m = Vector.LessThan(e, bestE);
                bestE = Vector.ConditionalSelect(m, e, bestE);
                bestIdx = Vector.ConditionalSelect(Vector.AsVectorInt32(m), new(j), bestIdx);
            }

            bestE.CopyTo(errs.Slice(bse));
            for (var k = 0; k < W; k++) weights[bse + k] = (byte)bestIdx[k];
        }

        double total = 0;
        if (computeError)
            for (var i = 0; i < 16; i++)
                total += errs[i] * ps[i];

        return total;
    }

    // Covariance about the integer mean via lane-parallel float FMA. Not bit-identical to scalar, because the
    // horizontal reduction (Vector.Sum) reorders the sum.
    static void CovarianceSimd(
        scoped ReadOnlySpan<int> ir,
        scoped ReadOnlySpan<int> ig,
        scoped ReadOnlySpan<int> ib,
        int meanR,
        int meanG,
        int meanB,
        out float cov0,
        out float cov1,
        out float cov2,
        out float cov3,
        out float cov4,
        out float cov5,
        out float blockMaxVar)
    {
        var W = Vector<float>.Count;
        var mR = new Vector<int>(meanR);
        var mG = new Vector<int>(meanG);
        var mB = new Vector<int>(meanB);
        var c0 = Vector<float>.Zero;
        var c1 = c0;
        var c2 = c0;
        var c3 = c0;
        var c4 = c0;
        var c5 = c0;
        for (var b = 0; b < 16; b += W)
        {
            var r = Vector.ConvertToSingle(new Vector<int>(ir.Slice(b)) - mR);
            var g = Vector.ConvertToSingle(new Vector<int>(ig.Slice(b)) - mG);
            var bl = Vector.ConvertToSingle(new Vector<int>(ib.Slice(b)) - mB);
            c0 += r * r;
            c1 += r * g;
            c2 += r * bl;
            c3 += g * g;
            c4 += g * bl;
            c5 += bl * bl;
        }

        cov0 = Vector.Sum(c0);
        cov1 = Vector.Sum(c1);
        cov2 = Vector.Sum(c2);
        cov3 = Vector.Sum(c3);
        cov4 = Vector.Sum(c4);
        cov5 = Vector.Sum(c5);
        blockMaxVar = MathF.Max(cov0, MathF.Max(cov3, cov5));
    }

    // Projection onto the principal axis: the dot products only (lane-parallel FMA), which are bit-identical;
    // the argmin/argmax over the dots stays scalar to keep the extreme-index tie-break exact.
    static void ProjectionSimd(
        scoped ReadOnlySpan<int> ir,
        scoped ReadOnlySpan<int> ig,
        scoped ReadOnlySpan<int> ib,
        float axisR,
        float axisG,
        float axisB,
        scoped Span<float> dots)
    {
        var W = Vector<float>.Count;
        var aR = new Vector<float>(axisR);
        var aG = new Vector<float>(axisG);
        var aB = new Vector<float>(axisB);
        for (var b = 0; b < 16; b += W)
        {
            var r = Vector.ConvertToSingle(new Vector<int>(ir.Slice(b)));
            var g = Vector.ConvertToSingle(new Vector<int>(ig.Slice(b)));
            var bl = Vector.ConvertToSingle(new Vector<int>(ib.Slice(b)));
            (r * aR + g * aG + bl * aB).CopyTo(dots.Slice(b));
        }
    }
}