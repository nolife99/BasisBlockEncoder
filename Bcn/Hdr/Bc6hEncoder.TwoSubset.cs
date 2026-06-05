// BC6H 2-subset encoder path (port of fast_encode_bc6h_2subsets / _pattern).
// Fires only on high-variance "very complex" blocks; tries 1/4/32 partitions by tier, fits two
// independent subsets (each: PCA axis -> extreme endpoints -> 3-bit weights -> LS refine), picks a
// delta-capable endpoint mode (5/1/0/7) or falls back to absolute mode 9, and commits if it beats
// the current 1-subset error.
//
// SoA throughout (ir/ig/ib int half-bits, fr/fg/fb/ps float). The per-subset accumulation loops are
// the same shape as the 1-subset ones, just blended by a per-texel subset bit — for SIMD that bit
// becomes a mask/blend rather than a branch.
using System;
using System.Numerics;

namespace Bcn.Hdr;

internal static partial class Bc6hEncoder
{
    private const int BC6H_NUM_PATS = 32;

    // pats2[p] = bit i set when texel i is in subset 1; anchor2[p] = subset-1 anchor texel.
    // Both derived from the decode pattern table: high bit 0x80/0x81 = anchor, &1 = subset.
    private static readonly uint[] Pats2 = BuildPats2();
    private static readonly int[] Anchor2 = BuildAnchor2();

    private static uint[] BuildPats2()
    {
        var t = new uint[BC6H_NUM_PATS];
        for (int p = 0; p < BC6H_NUM_PATS; p++)
        {
            uint bits = 0;
            for (int i = 0; i < 16; i++) bits |= (uint)(Bc6hTables.Patterns[p * 16 + i] & 1) << i;
            t[p] = bits;
        }
        return t;
    }

    private static int[] BuildAnchor2()
    {
        var t = new int[BC6H_NUM_PATS];
        for (int p = 0; p < BC6H_NUM_PATS; p++)
            for (int i = 0; i < 16; i++)
                if (Bc6hTables.Patterns[p * 16 + i] == 0x81) { t[p] = i; break; }
        return t;
    }

    private static readonly int[] ModeOrder2 = { 5, 1 };
    private static readonly int[] ModeOrder4 = { 0, 5, 7, 1 };
    private static readonly int[] ModeOrder9 = { 2, 3, 4, 0, 5, 6, 7, 8, 1 };

    private static void TwoSubset(
        ReadOnlySpan<int> ir, ReadOnlySpan<int> ig, ReadOnlySpan<int> ib,
        ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> ps,
        ref double curErr, ref int mode, ref int partition, Span<int> ep, Span<byte> w,
        int meanR, int meanG, int meanB, float axisR, float axisG, float axisB, Params prm)
    {
        if (prm.Max2SubsetPats >= BC6H_NUM_PATS)
        {
            for (int p = 0; p < BC6H_NUM_PATS; p++)
                TwoSubsetPattern(p, Pats2[p], ir, ig, ib, fr, fg, fb, ps, ref curErr, ref mode, ref partition, ep, w, meanR, meanG, meanB, prm);
            return;
        }

        // desired partition: which side of the block axis each texel lands (SIMD: planar FMA + sign-bit pack)
        uint desired = 0;
        for (int i = 0; i < 16; i++)
        {
            float f = (ir[i] - meanR) * axisR + (ig[i] - meanG) * axisG + (ib[i] - meanB) * axisB;
            if (f >= 0f) desired |= 1u << i;
        }

        if (prm.Max2SubsetPats == 1)
        {
            uint best = uint.MaxValue;
            for (uint p = 0; p < BC6H_NUM_PATS; p++)
            {
                int diff = BitOperations.PopCount(Pats2[p] ^ desired);
                int d = Math.Min(diff, 16 - diff);
                uint key = ((uint)d << 8) | p;
                if (key < best) best = key;
            }
            int patIndex = (int)(best & 0xFF);
            TwoSubsetPattern(patIndex, Pats2[patIndex], ir, ig, ib, fr, fg, fb, ps, ref curErr, ref mode, ref partition, ep, w, meanR, meanG, meanB, prm);
        }
        else
        {
            Span<uint> diffs = stackalloc uint[BC6H_NUM_PATS];
            for (int p = 0; p < BC6H_NUM_PATS; p++)
            {
                int diff = BitOperations.PopCount(Pats2[p] ^ desired);
                int d = Math.Min(diff, 16 - diff);
                diffs[p] = ((uint)d << 8) | (uint)p;
            }
            diffs.Sort();
            int n = Math.Min(prm.Max2SubsetPats, BC6H_NUM_PATS);
            for (int it = 0; it < n; it++)
            {
                int patIndex = (int)(diffs[it] & 0xFF);
                TwoSubsetPattern(patIndex, Pats2[patIndex], ir, ig, ib, fr, fg, fb, ps, ref curErr, ref mode, ref partition, ep, w, meanR, meanG, meanB, prm);
            }
        }
    }

    private static void TwoSubsetPattern(
        int patIndex, uint patBits,
        ReadOnlySpan<int> ir, ReadOnlySpan<int> ig, ReadOnlySpan<int> ib,
        ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> ps,
        ref double curErr, ref int mode, ref int partition, Span<int> ep, Span<byte> w,
        int meanR, int meanG, int meanB, Params prm)
    {
        // per-subset covariance about the block mean (SIMD: masked planar MAC)
        Span<long> cov = stackalloc long[2 * 6];
        for (int i = 0; i < 16; i++)
        {
            int s = (int)((patBits >> i) & 1);
            long r = ir[i] - meanR, g = ig[i] - meanG, b = ib[i] - meanB;
            int o = s * 6;
            cov[o + 0] += r * r; cov[o + 1] += r * g; cov[o + 2] += r * b;
            cov[o + 3] += g * g; cov[o + 4] += g * b; cov[o + 5] += b * b;
        }

        Span<float> axR = stackalloc float[2], axG = stackalloc float[2], axB = stackalloc float[2];
        for (int s = 0; s < 2; s++)
        {
            int o = s * 6;
            float c0 = cov[o + 0], c1 = cov[o + 1], c2 = cov[o + 2], c3 = cov[o + 3], c4 = cov[o + 4], c5 = cov[o + 5];
            float sc = 1f / (MathF.Max(c0, MathF.Max(c3, c5)) + REALLY_SMALL_FLOAT_VAL);
            float wx = sc * c0, wy = sc * c3, wz = sc * c5;
            float aR = c0 * wx + c1 * wy + c2 * wz, aG = c1 * wx + c3 * wy + c4 * wz, aB = c2 * wx + c4 * wy + c5 * wz;
            float l = aR * aR + aG * aG + aB * aB;
            float rr = 0.57735027f, gg = 0.57735027f, bb = 0.57735027f;
            if (MathF.Abs(l) >= SMALL_FLOAT_VAL) { float invL = InvSqrt(l); rr = aR * invL; gg = aG * invL; bb = aB * invL; }
            axR[s] = rr; axG[s] = gg; axB[s] = bb;
        }

        // project per subset -> extreme endpoints (half-bits). sMin/sMax indexed [c*2 + s].
        Span<float> minDot = stackalloc float[2] { BIG_FLOAT_VAL, BIG_FLOAT_VAL };
        Span<float> maxDot = stackalloc float[2] { -BIG_FLOAT_VAL, -BIG_FLOAT_VAL };
        Span<int> minIdx = stackalloc int[2], maxIdx = stackalloc int[2];
        for (int i = 0; i < 16; i++)
        {
            int s = (int)((patBits >> i) & 1);
            float dot = ir[i] * axR[s] + ig[i] * axG[s] + ib[i] * axB[s];
            if (dot < minDot[s]) { minDot[s] = dot; minIdx[s] = i; }
            if (dot > maxDot[s]) { maxDot[s] = dot; maxIdx[s] = i; }
        }
        Span<int> sMin = stackalloc int[6], sMax = stackalloc int[6];
        for (int s = 0; s < 2; s++)
        {
            int lo = minIdx[s], hi = maxIdx[s];
            sMin[0 * 2 + s] = ir[lo]; sMin[1 * 2 + s] = ig[lo]; sMin[2 * 2 + s] = ib[lo];
            sMax[0 * 2 + s] = ir[hi]; sMax[1 * 2 + s] = ig[hi]; sMax[2 * 2 + s] = ib[hi];
        }

        // LS refine per subset, using 3-bit weights from a pre-pass assignment on the raw endpoints
        Span<byte> tw = stackalloc byte[16];
        AssignWeights3(tw, patBits, sMin, sMax, fr, fg, fb, ps, false);

        Span<float> z00 = stackalloc float[2], z10 = stackalloc float[2], z11 = stackalloc float[2];
        Span<float> q00r = stackalloc float[2], tR = stackalloc float[2], q00g = stackalloc float[2], tG = stackalloc float[2], q00b = stackalloc float[2], tB = stackalloc float[2];
        for (int i = 0; i < 16; i++)
        {
            int s = (int)((patBits >> i) & 1);
            float r = ir[i], g = ig[i], b = ib[i];
            int sel = tw[i];
            z00[s] += LsW3[sel * 4 + 0]; z10[s] += LsW3[sel * 4 + 1]; z11[s] += LsW3[sel * 4 + 2];
            float wt = LsW3[sel * 4 + 3];
            q00r[s] += wt * r; tR[s] += r; q00g[s] += wt * g; tG[s] += g; q00b[s] += wt * b; tB[s] += b;
        }
        for (int s = 0; s < 2; s++)
        {
            float q10r = tR[s] - q00r[s], q10g = tG[s] - q00g[s], q10b = tB[s] - q00b[s], z01 = z10[s];
            float det = z00[s] * z11[s] - z01 * z10[s];
            if (MathF.Abs(det) < SMALL_FLOAT_VAL) continue;
            det = 1f / det;
            float iz00 = z11[s] * det, iz01 = -z01 * det, iz10 = -z10[s] * det, iz11 = z00[s] * det;
            sMax[0 * 2 + s] = Math.Clamp(RoundI(iz00 * q00r[s] + iz01 * q10r), 0, MAX_BC6H_HALF);
            sMin[0 * 2 + s] = Math.Clamp(RoundI(iz10 * q00r[s] + iz11 * q10r), 0, MAX_BC6H_HALF);
            sMax[1 * 2 + s] = Math.Clamp(RoundI(iz00 * q00g[s] + iz01 * q10g), 0, MAX_BC6H_HALF);
            sMin[1 * 2 + s] = Math.Clamp(RoundI(iz10 * q00g[s] + iz11 * q10g), 0, MAX_BC6H_HALF);
            sMax[2 * 2 + s] = Math.Clamp(RoundI(iz00 * q00b[s] + iz01 * q10b), 0, MAX_BC6H_HALF);
            sMin[2 * 2 + s] = Math.Clamp(RoundI(iz10 * q00b[s] + iz11 * q10b), 0, MAX_BC6H_HALF);
        }

        // pick an endpoint mode: prefer a delta-capable mode whose deltas all fit; else absolute mode 9
        const int ABS_MODE = 9;
        int bc6hMode = ABS_MODE, numEndpointBits = 6;
        Span<int> absBlog = stackalloc int[12]; // [c*4 + s*2 + lh]
        if (prm.NumDiffEndpointModes > 0)
        {
            int[] order; int numModes;
            if (prm.NumDiffEndpointModes >= 9) { order = ModeOrder9; numModes = 9; }
            else if (prm.NumDiffEndpointModes >= 4) { order = ModeOrder4; numModes = 4; }
            else { order = ModeOrder2; numModes = 2; }

            for (int mi = 0; mi < numModes; mi++)
            {
                int m = order[mi];
                int baseBits = Bc6hTables.ModeSigBits[m * 4];
                QuantEndpoints(sMin, sMax, absBlog, baseBits);

                int c;
                for (c = 0; c < 3; c++)
                {
                    int db = Bc6hTables.ModeSigBits[m * 4 + c + 1];
                    int maxD = (1 << (db - 1)) - 1, minD = -maxD;
                    int e0 = absBlog[c * 4 + 0], e1 = absBlog[c * 4 + 1], e2 = absBlog[c * 4 + 2], e3 = absBlog[c * 4 + 3];
                    int d0 = e1 - e0; if (d0 < minD || d0 > maxD) break;
                    int d1 = e2 - e0; if (d1 < minD || d1 > maxD) break;
                    int d2 = e3 - e0; if (d2 < minD || d2 > maxD) break;
                    int d3 = e2 - e1; if (d3 < minD || d3 > maxD) break;
                    int d4 = e3 - e1; if (d4 < minD || d4 > maxD) break;
                }
                if (c == 3) { bc6hMode = m; numEndpointBits = baseBits; break; }
            }
        }

        if (bc6hMode == ABS_MODE) QuantEndpoints(sMin, sMax, absBlog, numEndpointBits);

        // dequantize chosen endpoints back to half-bits for the final weight/error pass
        for (int s = 0; s < 2; s++)
            for (int c = 0; c < 3; c++)
            {
                sMin[c * 2 + s] = ConvertToHalf(Dequant(absBlog[c * 4 + s * 2 + 0], numEndpointBits));
                sMax[c * 2 + s] = ConvertToHalf(Dequant(absBlog[c * 4 + s * 2 + 1], numEndpointBits));
            }

        Span<byte> finalW = stackalloc byte[16];
        double trialErr = AssignWeights3(finalW, patBits, sMin, sMax, fr, fg, fb, ps, true);
        if (trialErr >= curErr) return;

        // commit: build endpoints, resolve the two anchor swaps, delta-encode if not mode 9
        Span<int> tep = stackalloc int[12];
        absBlog.CopyTo(tep);

        if ((finalW[0] & 4) != 0)
        {
            for (int c = 0; c < 3; c++) (tep[c * 4 + 0], tep[c * 4 + 1]) = (tep[c * 4 + 1], tep[c * 4 + 0]);
            for (int i = 0; i < 16; i++) if (((patBits >> i) & 1) == 0) finalW[i] = (byte)(7 - finalW[i]);
        }
        int anchor2 = Anchor2[patIndex];
        if ((finalW[anchor2] & 4) != 0)
        {
            for (int c = 0; c < 3; c++) (tep[c * 4 + 2], tep[c * 4 + 3]) = (tep[c * 4 + 3], tep[c * 4 + 2]);
            for (int i = 0; i < 16; i++) if (((patBits >> i) & 1) == 1) finalW[i] = (byte)(7 - finalW[i]);
        }

        if (bc6hMode != ABS_MODE)
        {
            for (int c = 0; c < 3; c++)
            {
                int db = Bc6hTables.ModeSigBits[bc6hMode * 4 + c + 1];
                int mask = (1 << db) - 1;
                int d0 = tep[c * 4 + 1] - tep[c * 4 + 0];
                int d1 = tep[c * 4 + 2] - tep[c * 4 + 0];
                int d2 = tep[c * 4 + 3] - tep[c * 4 + 0];
                tep[c * 4 + 1] = d0 & mask; tep[c * 4 + 2] = d1 & mask; tep[c * 4 + 3] = d2 & mask;
            }
        }

        curErr = trialErr; mode = bc6hMode; partition = patIndex;
        tep.CopyTo(ep); finalW.CopyTo(w);
    }

    // half-bits -> blog per endpoint (forward quantize), into absBlog[c*4 + s*2 + lh].
    private static void QuantEndpoints(ReadOnlySpan<int> sMin, ReadOnlySpan<int> sMax, Span<int> absBlog, int bits)
    {
        for (int s = 0; s < 2; s++)
            for (int c = 0; c < 3; c++)
            {
                absBlog[c * 4 + s * 2 + 0] = HalfToBlog(sMin[c * 2 + s], bits);
                absBlog[c * 4 + s * 2 + 1] = HalfToBlog(sMax[c * 2 + s], bits);
            }
    }

    // 3-bit weight assignment for 2 subsets; returns 1/luma^2-weighted error (used when computeError).
    // SIMD: per-subset 8 candidate colors are planar; argmin over 8 is branchless/vectorizable.
    private static double AssignWeights3(
        Span<byte> weights, uint patBits, ReadOnlySpan<int> sMin, ReadOnlySpan<int> sMax,
        ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> ps, bool computeError)
    {
        Span<float> cr = stackalloc float[16], cg = stackalloc float[16], cb = stackalloc float[16]; // [s*8 + j]
        if (UseSimdWeights && Candidates3Vectorizable)
            ComputeCandidates3Simd(sMin, sMax, cr, cg, cb);
        else
            for (int s = 0; s < 2; s++)
                for (int j = 0; j < 8; j++)
                {
                    int wv = Bc6hTables.Weight3[j];
                    cr[s * 8 + j] = HtoF((sMin[0 * 2 + s] * (64 - wv) + sMax[0 * 2 + s] * wv + 32) >> 6);
                    cg[s * 8 + j] = HtoF((sMin[1 * 2 + s] * (64 - wv) + sMax[1 * 2 + s] * wv + 32) >> 6);
                    cb[s * 8 + j] = HtoF((sMin[2 * 2 + s] * (64 - wv) + sMax[2 * 2 + s] * wv + 32) >> 6);
                }
        if (UseSimdWeights)
            return AssignWeights3Simd(weights, patBits, cr, cg, cb, fr, fg, fb, ps, computeError);

        double totalErr = 0;
        for (int i = 0; i < 16; i++)
        {
            int o = (int)((patBits >> i) & 1) * 8;
            float qr = fr[i], qg = fg[i], qb = fb[i];
            float bestE = Sq(cr[o] - qr) + Sq(cg[o] - qg) + Sq(cb[o] - qb); int bestIdx = 0;
            for (int j = 1; j < 8; j++)
            {
                float e = Sq(cr[o + j] - qr) + Sq(cg[o + j] - qg) + Sq(cb[o + j] - qb);
                if (e < bestE) { bestE = e; bestIdx = j; }
            }
            weights[i] = (byte)bestIdx;
            if (computeError) totalErr += bestE * ps[i];
        }
        return totalErr;
    }
}
