// Bc7Mode13.cs — part of the BC7 encoder implementation. Modes 1 & 3 (2 subsets, RGB): analytical partition + pair selection + writers.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bcn.Bc7;

internal static partial class Bc7Block
{
    private static bool PackMode1Or3Rgb(scoped Span<byte> block, scoped ReadOnlySpan<ColorRgba> px,
        float axR, float axG, float axB, int meanR, int meanG, int meanB,
        float sseEstToBeat, Bc7Flags flags, bool wantSse, out float estSse, out uint trueSse)
    {
        estSse = float.MaxValue;
        trueSse = uint.MaxValue;

        // (a) analytical partition selection: split pixels by the block-axis sign, then pick the canned
        //     2-subset partition closest to that split (Hamming distance, allowing label inversion).
        int bestPat = SelectPartition2(px, meanR, meanG, meanB, axR, axG, axB);
        int patBits = Bc7Tables.Part2Bitmasks[bestPat];

        // (b) per-subset totals / means
        Span<int> totR = stackalloc int[2]; Span<int> totG = stackalloc int[2];
        Span<int> totB = stackalloc int[2]; Span<int> totC = stackalloc int[2];
        ref ColorRgba _pp1 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq1 = ref Unsafe.Add(ref _pp1, i);
            int s = (patBits >> i) & 1;
            totR[s] += _qq1.R; totG[s] += _qq1.G; totB[s] += _qq1.B; totC[s]++;
        }
        if (totC[0] == 0 || totC[1] == 0) return false; // degenerate partition for this block

        Span<int> meanRs = stackalloc int[2]; Span<int> meanGs = stackalloc int[2]; Span<int> meanBs = stackalloc int[2];
        for (int s = 0; s < 2; s++)
        {
            int t = totC[s], h = t >> 1;
            meanRs[s] = (totR[s] + h) / t; meanGs[s] = (totG[s] + h) / t; meanBs[s] = (totB[s] + h) / t;
        }

        // per-subset covariance (icov[2][6] flattened)
        Span<int> icov = stackalloc int[12];
        ref ColorRgba _pp2 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq2 = ref Unsafe.Add(ref _pp2, i);
            int s = (patBits >> i) & 1, o = s * 6;
            int r = _qq2.R - meanRs[s], g = _qq2.G - meanGs[s], b = _qq2.B - meanBs[s];
            icov[o] += r * r; icov[o + 1] += r * g; icov[o + 2] += r * b;
            icov[o + 3] += g * g; icov[o + 4] += g * b; icov[o + 5] += b * b;
        }

        // (c) per-subset axis + slam-to-line residual + integer scaled axis for projection
        Span<int> ar = stackalloc int[2]; Span<int> ag = stackalloc int[2]; Span<int> ab = stackalloc int[2];
        Span<float> cov = stackalloc float[6];
        float slam = 0;
        for (int s = 0; s < 2; s++)
        {
            int o = s * 6;
            int bmv = Math.Max(icov[o], Math.Max(icov[o + 3], icov[o + 5]));
            for (int j = 0; j < 6; j++) cov[j] = icov[o + j];
            float sc = 1f / (bmv + 0.0000125f);
            float wx = sc * cov[0], wy = sc * cov[3], wz = sc * cov[5];
            float sAxR = cov[0] * wx + cov[1] * wy + cov[2] * wz;
            float sAxG = cov[1] * wx + cov[3] * wy + cov[4] * wz;
            float sAxB = cov[2] * wx + cov[4] * wy + cov[5] * wz;
            slam += EstimateSlamToLineSse(cov, sAxR, sAxG, sAxB, out _);

            int sr = 306, sg = 601, sb = 117;
            float k = MathF.Max(MathF.Abs(sAxR), MathF.Max(MathF.Abs(sAxG), MathF.Abs(sAxB)));
            if (k >= SmallFloat) { float m = 2048f / k; sr = (int)(sAxR * m); sg = (int)(sAxG * m); sb = (int)(sAxB * m); }
            ar[s] = sr << 4; ag[s] = sg << 4; ab[s] = sb << 4;
        }

        // (d) per-subset endpoints via projection (index packed in low nibble)
        Span<int> lowDot = stackalloc int[2]; lowDot[0] = lowDot[1] = int.MaxValue;
        Span<int> highDot = stackalloc int[2]; highDot[0] = highDot[1] = int.MinValue;
        ref ColorRgba _pp3 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq3 = ref Unsafe.Add(ref _pp3, i);
            int s = (patBits >> i) & 1;
            int dot = _qq3.R * ar[s] + _qq3.G * ag[s] + _qq3.B * ab[s] + i;
            if (dot < lowDot[s]) lowDot[s] = dot;
            if (dot > highDot[s]) highDot[s] = dot;
        }
        Span<int> loC = stackalloc int[2]; Span<int> hiC = stackalloc int[2];
        for (int s = 0; s < 2; s++) { loC[s] = lowDot[s] & 0xF; hiC[s] = highDot[s] & 0xF; }

        // (e) estimate BOTH arms of the pair, pick the cheaper, and bail if it can't beat the ceiling.
        float disc1 = (flags & Bc7Flags.PBitOpt) != 0 ? UniquePbitDiscount : 1f;   // mode 1
        float disc3 = (flags & Bc7Flags.PBitOpt) != 0 ? SharedPbitDiscount : 1f;   // mode 3
        Span<int> spans = stackalloc int[4];
        float q1 = 0, q3 = 0;
        for (int s = 0; s < 2; s++)
        {
            spans[0] = px[hiC[s]].R - px[loC[s]].R; spans[1] = px[hiC[s]].G - px[loC[s]].G; spans[2] = px[hiC[s]].B - px[loC[s]].B;
            q1 += AnalyticalQuantEstSse(64,  8, 3, spans, default, disc1, totC[s]);   // mode 1: 6-bit endpoints, 3-bit idx
            q3 += AnalyticalQuantEstSse(128, 4, 3, spans, default, disc3, totC[s]);   // mode 3: 7-bit endpoints, 2-bit idx
        }
        float mode1Est = slam + q1, mode3Est = slam + q3;
        estSse = MathF.Min(mode1Est, mode3Est);
        if (estSse >= sseEstToBeat) return false;        // BAIL — no encode

        // (f) encode the cheaper mode, with LS endpoint refinement + p-bit optimization (PBitOpt path),
        //     or the fixed-p-bit fallback when PBitOpt is off.
        bool pbitOpt = (flags & Bc7Flags.PBitOpt) != 0;
        Span<int> w = stackalloc int[16];
        Span<uint> lr = stackalloc uint[2]; Span<uint> lg = stackalloc uint[2]; Span<uint> lb = stackalloc uint[2];
        Span<uint> hr = stackalloc uint[2]; Span<uint> hg = stackalloc uint[2]; Span<uint> hb = stackalloc uint[2];
        Span<float> xl = stackalloc float[4]; Span<float> xh = stackalloc float[4];
        Span<float> sxl = stackalloc float[4]; Span<float> sxh = stackalloc float[4];
        Span<int> minC = stackalloc int[4]; Span<int> maxC = stackalloc int[4]; Span<uint> pbo = stackalloc uint[2];
        Span<int> eLoR = stackalloc int[2]; Span<int> eLoG = stackalloc int[2]; Span<int> eLoB = stackalloc int[2];
        Span<int> eHiR = stackalloc int[2]; Span<int> eHiG = stackalloc int[2]; Span<int> eHiB = stackalloc int[2];

        if (mode1Est < mode3Est)
        {
            // mode 1: 6-bit endpoints, one shared p-bit per subset, 3-bit indices.
            Span<uint> pb = stackalloc uint[2];
            for (int s = 0; s < 2; s++)
            {
                int lc = loC[s], hc = hiC[s];
                if (pbitOpt)
                {
                    sxl[0] = px[lc].R / 255f; sxl[1] = px[lc].G / 255f; sxl[2] = px[lc].B / 255f; sxl[3] = 0f;
                    sxh[0] = px[hc].R / 255f; sxh[1] = px[hc].G / 255f; sxh[2] = px[hc].B / 255f; sxh[3] = 0f;
                    DetermineSharedPbits(3, 6, sxl, sxh, minC, maxC, pbo);
                    pb[s] = pbo[0]; lr[s] = (uint)minC[0]; lg[s] = (uint)minC[1]; lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0]; hg[s] = (uint)maxC[1]; hb[s] = (uint)maxC[2];
                }
                else
                {
                    uint p = (uint)(Math.Max(px[lc].R + px[lc].G + px[lc].B, px[hc].R + px[hc].G + px[hc].B) >= 129 * 3 ? 1 : 0);
                    pb[s] = p;
                    lr[s] = (uint)To6(px[lc].R, p); lg[s] = (uint)To6(px[lc].G, p); lb[s] = (uint)To6(px[lc].B, p);
                    hr[s] = (uint)To6(px[hc].R, p); hg[s] = (uint)To6(px[hc].G, p); hb[s] = (uint)To6(px[hc].B, p);
                }
            }
            EvalWeightsMode1(px, w, lr, lg, lb, hr, hg, hb, pb, patBits);
            for (int s = 0; s < 2; s++)
            {
                if (!ComputeLs3DSub(w, px, patBits, s, Bc7Tables.LsTab3, totR[s], totG[s], totB[s], xl, xh)) continue;
                if (pbitOpt)
                {
                    sxl[0] = xl[0] / 255f; sxl[1] = xl[1] / 255f; sxl[2] = xl[2] / 255f; sxl[3] = 0f;
                    sxh[0] = xh[0] / 255f; sxh[1] = xh[1] / 255f; sxh[2] = xh[2] / 255f; sxh[3] = 0f;
                    DetermineSharedPbits(3, 6, sxl, sxh, minC, maxC, pbo);
                    pb[s] = pbo[0]; lr[s] = (uint)minC[0]; lg[s] = (uint)minC[1]; lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0]; hg[s] = (uint)maxC[1]; hb[s] = (uint)maxC[2];
                }
                else
                {
                    float l = xl[0] + xl[1] + xl[2], h = xh[0] + xh[1] + xh[2];
                    uint p = (uint)(MathF.Max(l, h) >= 129f * 3f ? 1 : 0); pb[s] = p;
                    lr[s] = (uint)To6(RoundPos(xl[0]), p); lg[s] = (uint)To6(RoundPos(xl[1]), p); lb[s] = (uint)To6(RoundPos(xl[2]), p);
                    hr[s] = (uint)To6(RoundPos(xh[0]), p); hg[s] = (uint)To6(RoundPos(xh[1]), p); hb[s] = (uint)To6(RoundPos(xh[2]), p);
                }
            }
            for (int s = 0; s < 2; s++) {
                eLoR[s] = From6((int)lr[s], pb[s]); eLoG[s] = From6((int)lg[s], pb[s]); eLoB[s] = From6((int)lb[s], pb[s]);
                eHiR[s] = From6((int)hr[s], pb[s]); eHiG[s] = From6((int)hg[s], pb[s]); eHiB[s] = From6((int)hb[s], pb[s]); }
            if (wantSse && System.Runtime.Intrinsics.X86.Fma.IsSupported && System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                trueSse = (uint)EvalSelSseRgb2X86(px, w, eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, patBits, Bc7Tables.Weights3, 7);
            else
            {
                EvalWeightsMode1(px, w, lr, lg, lb, hr, hg, hb, pb, patBits);
                if (wantSse) { long sx = SseMultiSubsetRgb(eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, Bc7Tables.Partition2, bestPat * 16, Bc7Tables.Weights3, w, px);
                  trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
            }
            EncodeMode1(block, (uint)bestPat, lr, lg, lb, hr, hg, hb, pb[0], pb[1], w);
        }
        else
        {
            // mode 3: 7-bit endpoints, unique p-bits, 2-bit indices.
            Span<uint> pbits = stackalloc uint[4];
            for (int s = 0; s < 2; s++)
            {
                int lc = loC[s], hc = hiC[s];
                if (pbitOpt)
                {
                    sxl[0] = px[lc].R / 255f; sxl[1] = px[lc].G / 255f; sxl[2] = px[lc].B / 255f; sxl[3] = 0f;
                    sxh[0] = px[hc].R / 255f; sxh[1] = px[hc].G / 255f; sxh[2] = px[hc].B / 255f; sxh[3] = 0f;
                    DetermineUniquePbits(3, 7, sxl, sxh, minC, maxC, pbo);
                    pbits[s * 2] = pbo[0]; pbits[s * 2 + 1] = pbo[1];
                    lr[s] = (uint)minC[0]; lg[s] = (uint)minC[1]; lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0]; hg[s] = (uint)maxC[1]; hb[s] = (uint)maxC[2];
                }
                else
                {
                    uint lp = (uint)((px[lc].R + px[lc].G + px[lc].B) >= 129 ? 1 : 0);
                    pbits[s * 2] = lp; lr[s] = (uint)To7(px[lc].R, lp); lg[s] = (uint)To7(px[lc].G, lp); lb[s] = (uint)To7(px[lc].B, lp);
                    uint hp = (uint)((px[hc].R + px[hc].G + px[hc].B) >= 129 ? 1 : 0);
                    pbits[s * 2 + 1] = hp; hr[s] = (uint)To7(px[hc].R, hp); hg[s] = (uint)To7(px[hc].G, hp); hb[s] = (uint)To7(px[hc].B, hp);
                }
            }
            EvalWeightsMode3(px, w, lr, lg, lb, hr, hg, hb, pbits, patBits);
            for (int s = 0; s < 2; s++)
            {
                if (!ComputeLs3DSub(w, px, patBits, s, Bc7Tables.LsTab2, totR[s], totG[s], totB[s], xl, xh)) continue;
                if (pbitOpt)
                {
                    sxl[0] = xl[0] / 255f; sxl[1] = xl[1] / 255f; sxl[2] = xl[2] / 255f; sxl[3] = 0f;
                    sxh[0] = xh[0] / 255f; sxh[1] = xh[1] / 255f; sxh[2] = xh[2] / 255f; sxh[3] = 0f;
                    DetermineUniquePbits(3, 7, sxl, sxh, minC, maxC, pbo);
                    pbits[s * 2] = pbo[0]; pbits[s * 2 + 1] = pbo[1];
                    lr[s] = (uint)minC[0]; lg[s] = (uint)minC[1]; lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0]; hg[s] = (uint)maxC[1]; hb[s] = (uint)maxC[2];
                }
                else
                {
                    int lr8 = RoundPos(xl[0]), lg8 = RoundPos(xl[1]), lb8 = RoundPos(xl[2]);
                    int hr8 = RoundPos(xh[0]), hg8 = RoundPos(xh[1]), hb8 = RoundPos(xh[2]);
                    uint lp = (uint)((lr8 + lg8 + lb8) >= 129 ? 1 : 0);
                    pbits[s * 2] = lp; lr[s] = (uint)To7(lr8, lp); lg[s] = (uint)To7(lg8, lp); lb[s] = (uint)To7(lb8, lp);
                    uint hp = (uint)((hr8 + hg8 + hb8) >= 129 ? 1 : 0);
                    pbits[s * 2 + 1] = hp; hr[s] = (uint)To7(hr8, hp); hg[s] = (uint)To7(hg8, hp); hb[s] = (uint)To7(hb8, hp);
                }
            }
            for (int s = 0; s < 2; s++) {
                eLoR[s] = From7((int)lr[s], pbits[s * 2]); eLoG[s] = From7((int)lg[s], pbits[s * 2]); eLoB[s] = From7((int)lb[s], pbits[s * 2]);
                eHiR[s] = From7((int)hr[s], pbits[s * 2 + 1]); eHiG[s] = From7((int)hg[s], pbits[s * 2 + 1]); eHiB[s] = From7((int)hb[s], pbits[s * 2 + 1]); }
            if (wantSse && System.Runtime.Intrinsics.X86.Fma.IsSupported && System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                trueSse = (uint)EvalSelSseRgb2X86(px, w, eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, patBits, Bc7Tables.Weights2, 3);
            else
            {
                EvalWeightsMode3(px, w, lr, lg, lb, hr, hg, hb, pbits, patBits);
                if (wantSse) { long sx = SseMultiSubsetRgb(eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, Bc7Tables.Partition2, bestPat * 16, Bc7Tables.Weights2, w, px);
                  trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
            }
            EncodeMode3(block, (uint)bestPat, lr, lg, lb, hr, hg, hb, pbits, w);
        }
        return true;
    }

    // Per-subset 2-bit index evaluation (faithful port of eval_weights_mode3_rgb).
    private static void EvalWeightsMode3(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w,
        scoped ReadOnlySpan<uint> blr, scoped ReadOnlySpan<uint> blg, scoped ReadOnlySpan<uint> blb,
        scoped ReadOnlySpan<uint> bhr, scoped ReadOnlySpan<uint> bhg, scoped ReadOnlySpan<uint> bhb,
        scoped ReadOnlySpan<uint> pbits, int subsetBitmask)
    {
        Span<int> dr = stackalloc int[2]; Span<int> dg = stackalloc int[2]; Span<int> db = stackalloc int[2];
        Span<float> f = stackalloc float[2]; Span<int> sofs = stackalloc int[2];
        Span<int> lr = stackalloc int[2]; Span<int> lg = stackalloc int[2]; Span<int> lb = stackalloc int[2];
        for (int s = 0; s < 2; s++)
        {
            lr[s] = From7((int)blr[s], pbits[s * 2]); lg[s] = From7((int)blg[s], pbits[s * 2]); lb[s] = From7((int)blb[s], pbits[s * 2]);
            int hr = From7((int)bhr[s], pbits[s * 2 + 1]), hg = From7((int)bhg[s], pbits[s * 2 + 1]), hb = From7((int)bhb[s], pbits[s * 2 + 1]);
            dr[s] = hr - lr[s]; dg[s] = hg - lg[s]; db[s] = hb - lb[s];
            f[s] = 3.0f / (dr[s] * dr[s] + dg[s] * dg[s] + db[s] * db[s] + 0.00000125f);
            sofs[s] = lr[s] * dr[s] + lg[s] * dg[s] + lb[s] * db[s];
        }
        EvalSelRgb2(px, w, dr, dg, db, sofs, f, subsetBitmask, 3);
    }

    // BC7 mode-3 block writer (faithful port of encode_mode3_rgb_block). Sequential bit packing.
    private static void EncodeMode3(scoped Span<byte> block, uint partId,
        scoped Span<uint> lr, scoped Span<uint> lg, scoped Span<uint> lb, scoped Span<uint> hr, scoped Span<uint> hg, scoped Span<uint> hb,
        scoped Span<uint> p, scoped ReadOnlySpan<int> weights)
    {
        int anchor = Bc7Tables.AnchorIndexSecondSubset[(int)partId];

        Span<uint> winv = stackalloc uint[2];
        if ((weights[0] & 2) != 0)            // subset-0 anchor (pixel 0): MSB must be 0 -> swap
        {
            (lr[0], hr[0]) = (hr[0], lr[0]); (lg[0], hg[0]) = (hg[0], lg[0]); (lb[0], hb[0]) = (hb[0], lb[0]);
            (p[0], p[1]) = (p[1], p[0]); winv[0] = 3;
        }
        if ((weights[anchor] & 2) != 0)       // subset-1 anchor: swap
        {
            (lr[1], hr[1]) = (hr[1], lr[1]); (lg[1], hg[1]) = (hg[1], lg[1]); (lb[1], hb[1]) = (hb[1], lb[1]);
            (p[2], p[3]) = (p[3], p[2]); winv[1] = 3;
        }

        ulong x = 0b1000UL | ((ulong)partId << 4)
            | ((ulong)lr[0] << 10) | ((ulong)hr[0] << 17)
            | ((ulong)lr[1] << 24) | ((ulong)hr[1] << 31)
            | ((ulong)lg[0] << 38) | ((ulong)hg[0] << 45)
            | ((ulong)lg[1] << 52) | ((ulong)hg[1] << 59);
        block[0] = (byte)x;         block[1] = (byte)(x >> 8);  block[2] = (byte)(x >> 16); block[3] = (byte)(x >> 24);
        block[4] = (byte)(x >> 32); block[5] = (byte)(x >> 40); block[6] = (byte)(x >> 48); block[7] = (byte)(x >> 56);

        // 2 high bits of hg[1] carry into the second word
        ulong y = ((ulong)hg[1] >> 5) | ((ulong)lb[0] << 2) | ((ulong)hb[0] << 9)
            | ((ulong)lb[1] << 16) | ((ulong)hb[1] << 23)
            | ((ulong)p[0] << 30) | ((ulong)p[1] << 31) | ((ulong)p[2] << 32) | ((ulong)p[3] << 33);
        int ofs = 34;
        for (int i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Partition2[(int)partId * 16 + i];
            ulong wv = (uint)(weights[i] ^ (int)winv[s]);
            y |= wv << ofs;
            ofs += 2 - ((i == 0 || i == anchor) ? 1 : 0);  // anchor indices are 1 bit, rest 2
        }
        block[8]  = (byte)y;         block[9]  = (byte)(y >> 8);  block[10] = (byte)(y >> 16); block[11] = (byte)(y >> 24);
        block[12] = (byte)(y >> 32); block[13] = (byte)(y >> 40); block[14] = (byte)(y >> 48); block[15] = (byte)(y >> 56);
    }

    // Per-subset 3-bit index evaluation (faithful port of eval_weights_mode1_rgb). One shared p-bit per subset.
    private static void EvalWeightsMode1(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w,
        scoped ReadOnlySpan<uint> blr, scoped ReadOnlySpan<uint> blg, scoped ReadOnlySpan<uint> blb,
        scoped ReadOnlySpan<uint> bhr, scoped ReadOnlySpan<uint> bhg, scoped ReadOnlySpan<uint> bhb,
        scoped ReadOnlySpan<uint> pbits, int subsetBitmask)
    {
        Span<int> dr = stackalloc int[2]; Span<int> dg = stackalloc int[2]; Span<int> db = stackalloc int[2];
        Span<float> f = stackalloc float[2]; Span<int> sofs = stackalloc int[2];
        Span<int> lr = stackalloc int[2]; Span<int> lg = stackalloc int[2]; Span<int> lb = stackalloc int[2];
        for (int s = 0; s < 2; s++)
        {
            lr[s] = From6((int)blr[s], pbits[s]); lg[s] = From6((int)blg[s], pbits[s]); lb[s] = From6((int)blb[s], pbits[s]);
            int hr = From6((int)bhr[s], pbits[s]), hg = From6((int)bhg[s], pbits[s]), hb = From6((int)bhb[s], pbits[s]);
            dr[s] = hr - lr[s]; dg[s] = hg - lg[s]; db[s] = hb - lb[s];
            f[s] = 7.0f / (dr[s] * dr[s] + dg[s] * dg[s] + db[s] * db[s] + 0.00000125f);
            sofs[s] = lr[s] * dr[s] + lg[s] * dg[s] + lb[s] * db[s];
        }
        EvalSelRgb2(px, w, dr, dg, db, sofs, f, subsetBitmask, 7);
    }

    // BC7 mode-1 block writer (faithful port of encode_mode1_rgb_block).
    private static void EncodeMode1(scoped Span<byte> block, uint partId,
        scoped Span<uint> lr, scoped Span<uint> lg, scoped Span<uint> lb, scoped Span<uint> hr, scoped Span<uint> hg, scoped Span<uint> hb,
        uint p0, uint p1, scoped ReadOnlySpan<int> weights)
    {
        int anchor = Bc7Tables.AnchorIndexSecondSubset[(int)partId];
        Span<uint> winv = stackalloc uint[2];
        if ((weights[0] & 4) != 0)        // subset-0 anchor (pixel 0): MSB must be 0 -> swap endpoints
        {
            (lr[0], hr[0]) = (hr[0], lr[0]); (lg[0], hg[0]) = (hg[0], lg[0]); (lb[0], hb[0]) = (hb[0], lb[0]); winv[0] = 7;
        }
        if ((weights[anchor] & 4) != 0)   // subset-1 anchor: swap (shared p-bit is unaffected by endpoint order)
        {
            (lr[1], hr[1]) = (hr[1], lr[1]); (lg[1], hg[1]) = (hg[1], lg[1]); (lb[1], hb[1]) = (hb[1], lb[1]); winv[1] = 7;
        }

        block[0] = (byte)(0b10 | (partId << 2));

        ulong x = lr[0] | ((ulong)hr[0] << 6)
            | ((ulong)lr[1] << 12) | ((ulong)hr[1] << 18)
            | ((ulong)lg[0] << 24) | ((ulong)hg[0] << 30)
            | ((ulong)lg[1] << 36) | ((ulong)hg[1] << 42)
            | ((ulong)lb[0] << 48) | ((ulong)hb[0] << 54)
            | ((ulong)lb[1] << 60);
        block[1] = (byte)x;         block[2] = (byte)(x >> 8);  block[3] = (byte)(x >> 16); block[4] = (byte)(x >> 24);
        block[5] = (byte)(x >> 32); block[6] = (byte)(x >> 40); block[7] = (byte)(x >> 48); block[8] = (byte)(x >> 56);
        block[9] = (byte)((lb[1] >> 4) | (hb[1] << 2));   // top 2 bits of lb1, then hb1

        ulong y = p0 | ((ulong)p1 << 1);
        int ofs = 2;
        for (int i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Partition2[(int)partId * 16 + i];
            ulong wv = (uint)(weights[i] ^ (int)winv[s]);
            y |= wv << ofs;
            ofs += 3 - ((i == 0 || i == anchor) ? 1 : 0);  // anchor indices are 2 bits, rest 3
        }
        block[10] = (byte)y;         block[11] = (byte)(y >> 8);  block[12] = (byte)(y >> 16);
        block[13] = (byte)(y >> 24); block[14] = (byte)(y >> 32); block[15] = (byte)(y >> 40);
    }
}
