// Bc7Mode02.cs — Modes 0 & 2 (3 subsets, RGB). Port of pack_mode0_or_2_rgb.
//   Mode 0: 4-bit endpoints, 6 unique p-bits, 3-bit indices, 16 partitions.
//   Mode 2: 5-bit endpoints, no p-bits,    2-bit indices, 64 partitions.
// This path uses fixed p-bits (mode 0) and does not apply the least-squares endpoint refinement.

namespace Bcn.Bc7;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static partial class Bc7Block
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Dist3(int r, int g, int b, int r2, int g2, int b2)
    {
        int dr = r - r2, dg = g - g2, db = b - b2;
        return dr * dr + dg * dg + db * db;
    }

    // Build a 3-subset assignment from the block axis: split by sign, then re-split the worse-fit half.
    static bool Determine3Subsets(scoped Span<byte> final3,
        scoped ReadOnlySpan<ColorRgba> px,
        float axR,
        float axG,
        float axB,
        int meanR,
        int meanG,
        int meanB)
    {
        Span<int> sub = stackalloc int[16];
        Span<int> mR = stackalloc int[2];
        Span<int> mG = stackalloc int[2];
        Span<int> mB = stackalloc int[2];
        Span<int> tot = stackalloc int[2];
        ref var _pp0 = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
        {
            ref readonly var _qq0 = ref Unsafe.Add(ref _pp0, i);
            int rd = _qq0.R - meanR, gd = _qq0.G - meanG, bd = _qq0.B - meanB;
            var s = rd * axR + gd * axG + bd * axB > 0f ? 1 : 0;
            sub[i] = s;
            mR[s] += _qq0.R;
            mG[s] += _qq0.G;
            mB[s] += _qq0.B;
            tot[s]++;
        }

        for (var s = 0; s < 2; s++)
        {
            var t = tot[s];
            if (t == 0) return false;

            var h = t >> 1;
            mR[s] = (mR[s] + h) / t;
            mG[s] = (mG[s] + h) / t;
            mB[s] = (mB[s] + h) / t;
        }

        Span<int> sse = stackalloc int[2];
        ref var _pp1 = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
        {
            ref readonly var _qq1 = ref Unsafe.Add(ref _pp1, i);
            var s = sub[i];
            sse[s] += Dist3(_qq1.R, _qq1.G, _qq1.B, mR[s], mG[s], mB[s]);
        }

        var split = sse[1] > sse[0] ? 1 : 0;
        if (tot[split] < 2) return false;

        int loY = int.MaxValue, hiY = 0;
        ref var _pp2 = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
        {
            ref readonly var _qq2 = ref Unsafe.Add(ref _pp2, i);
            if (sub[i] != split) continue;

            var y = (_qq2.R + _qq2.G + _qq2.B << 4) + i;
            if (y < loY) loY = y;
            if (y > hiY) hiY = y;
        }

        int loi = loY & 15, hii = hiY & 15;
        if (loi == hii) return false;

        int lr = px[loi].R, lg = px[loi].G, lb = px[loi].B;
        int hr = px[hii].R, hg = px[hii].G, hb = px[hii].B;

        final3.Fill(2);
        ref var _pp3 = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
            if (sub[i] == split)
            {
                ref readonly var _qq3 = ref Unsafe.Add(ref _pp3, i);
                var d0 = Dist3(lr, lg, lb, _qq3.R, _qq3.G, _qq3.B);
                var d1 = Dist3(hr, hg, hb, _qq3.R, _qq3.G, _qq3.B);
                final3[i] = (byte)(d1 > d0 ? 1 : 0);
            }

        return true;
    }

    // Match the desired 3-subset assignment against the 64 canned patterns over all 6 label permutations.
    // Returns the mode-2 (64) index; outputs the best mode-0 (first-16) index.
    static int Pick3SubsetPatIndex(scoped ReadOnlySpan<byte> desired, out int patFirst16)
    {
        patFirst16 = 0;
        int m0 = 0, m1 = 0, m2 = 0;
        for (var i = 0; i < 16; i++)
        {
            int s = desired[i];
            if (s == 0) m0 |= 1 << i;
            else if (s == 1) m1 |= 1 << i;
            else m2 |= 1 << i;
        }

        int n0 = BitOperations.PopCount((uint)m0), n1 = BitOperations.PopCount((uint)m1), n2 = 16 - n0 - n1;

        int bestScore = -1, bestPat = 0;
        for (var p = 0; p < 64; p++)
        {
            var packed = Bc7Tables.Part3Bitmasks[p];
            int S0 = (int)(packed & 0xFFFF), S1 = (int)(packed >> 16);
            int C00 = BitOperations.PopCount((uint)(m0 & S0)), C01 = BitOperations.PopCount((uint)(m0 & S1)), C02 = n0 - C00 - C01;
            int C10 = BitOperations.PopCount((uint)(m1 & S0)), C11 = BitOperations.PopCount((uint)(m1 & S1)), C12 = n1 - C10 - C11;
            int C20 = BitOperations.PopCount((uint)(m2 & S0)), C21 = BitOperations.PopCount((uint)(m2 & S1)), C22 = n2 - C20 - C21;
            int s0 = C00 + C11 + C22, s1 = C00 + C12 + C21, s2 = C01 + C10 + C22, s3 = C01 + C12 + C20, s4 = C02 + C10 + C21, s5 = C02 + C11 + C20;
            var sc = s0;
            if (s1 > sc) sc = s1;
            if (s2 > sc) sc = s2;
            if (s3 > sc) sc = s3;
            if (s4 > sc) sc = s4;
            if (s5 > sc) sc = s5;
            if (sc > bestScore)
            {
                bestScore = sc;
                bestPat = p;
                if (sc == 16)
                {
                    if (p <= 15) patFirst16 = bestPat;
                    break;
                }
            }

            if (p == 15) patFirst16 = bestPat;
        }

        return bestPat;
    }

    static bool PackMode0Or2Rgb(scoped Span<byte> block,
        scoped ReadOnlySpan<ColorRgba> px,
        float axR,
        float axG,
        float axB,
        int meanR,
        int meanG,
        int meanB,
        float sseEstToBeat,
        Bc7Flags flags,
        bool wantSse,
        out float estSse,
        out uint trueSse)
    {
        estSse = float.MaxValue;
        trueSse = uint.MaxValue;

        Span<byte> desired = stackalloc byte[16];
        if (!Determine3Subsets(desired, px, axR, axG, axB, meanR, meanG, meanB)) return false;

        var patM2 = Pick3SubsetPatIndex(desired, out var patM0); // patM0 <= 15, patM2 <= 63

        // per-mode (0,2) x per-subset (0,1,2) flattened accumulators
        Span<int> totC = stackalloc int[6];
        Span<int> loC = stackalloc int[6];
        Span<int> hiC = stackalloc int[6];
        Span<int> totR = stackalloc int[6];
        Span<int> totG = stackalloc int[6];
        Span<int> totB = stackalloc int[6];
        Span<float> slamMode = stackalloc float[2];
        Span<float> quantMode = stackalloc float[2];

        Span<int> icov = stackalloc int[18];
        Span<float> cov = stackalloc float[6];
        Span<int> mR = stackalloc int[3];
        Span<int> mG = stackalloc int[3];
        Span<int> mB = stackalloc int[3];
        Span<int> ar = stackalloc int[3];
        Span<int> ag = stackalloc int[3];
        Span<int> ab = stackalloc int[3];
        Span<int> lowDot = stackalloc int[3];
        Span<int> highDot = stackalloc int[3];
        Span<int> spans = stackalloc int[4];

        for (var mode = 0; mode < 2; mode++)
        {
            var bse = mode * 3;
            if (mode == 1 && patM0 == patM2)
            {
                for (var s = 0; s < 3; s++)
                {
                    totC[3 + s] = totC[s];
                    loC[3 + s] = loC[s];
                    hiC[3 + s] = hiC[s];
                    totR[3 + s] = totR[s];
                    totG[3 + s] = totG[s];
                    totB[3 + s] = totB[s];
                }

                slamMode[1] = slamMode[0];
            }
            else
            {
                var pat = mode == 0 ? patM0 : patM2;
                for (var s = 0; s < 3; s++)
                {
                    totC[bse + s] = 0;
                    totR[bse + s] = 0;
                    totG[bse + s] = 0;
                    totB[bse + s] = 0;
                }

                ref var _pp4 = ref MemoryMarshal.GetReference(px);
                for (var i = 0; i < 16; i++)
                {
                    ref readonly var _qq4 = ref Unsafe.Add(ref _pp4, i);
                    int s = Bc7Tables.Partition3[pat * 16 + i];
                    totR[bse + s] += _qq4.R;
                    totG[bse + s] += _qq4.G;
                    totB[bse + s] += _qq4.B;
                    totC[bse + s]++;
                }

                for (var s = 0; s < 3; s++)
                {
                    int t = totC[bse + s], h = t >> 1;
                    mR[s] = (totR[bse + s] + h) / t;
                    mG[s] = (totG[bse + s] + h) / t;
                    mB[s] = (totB[bse + s] + h) / t;
                }

                icov.Clear();
                ref var _pp5 = ref MemoryMarshal.GetReference(px);
                for (var i = 0; i < 16; i++)
                {
                    ref readonly var _qq5 = ref Unsafe.Add(ref _pp5, i);
                    int s = Bc7Tables.Partition3[pat * 16 + i], o = s * 6;
                    int r = _qq5.R - mR[s], g = _qq5.G - mG[s], b = _qq5.B - mB[s];
                    icov[o] += r * r;
                    icov[o + 1] += r * g;
                    icov[o + 2] += r * b;
                    icov[o + 3] += g * g;
                    icov[o + 4] += g * b;
                    icov[o + 5] += b * b;
                }

                float slam = 0;
                for (var s = 0; s < 3; s++)
                {
                    var o = s * 6;
                    var bmv = Math.Max(icov[o], Math.Max(icov[o + 3], icov[o + 5]));
                    for (var j = 0; j < 6; j++) cov[j] = icov[o + j];
                    var sc = 1f / (bmv + 0.0000125f);
                    float wx = sc * cov[0], wy = sc * cov[3], wz = sc * cov[5];
                    var sAxR = cov[0] * wx + cov[1] * wy + cov[2] * wz;
                    var sAxG = cov[1] * wx + cov[3] * wy + cov[4] * wz;
                    var sAxB = cov[2] * wx + cov[4] * wy + cov[5] * wz;
                    slam += EstimateSlamToLineSse(cov, sAxR, sAxG, sAxB, out _);
                    int sr = 306, sg = 601, sb = 117;
                    var k = MathF.Max(MathF.Abs(sAxR), MathF.Max(MathF.Abs(sAxG), MathF.Abs(sAxB)));
                    if (k >= SmallFloat)
                    {
                        var m = 2048f / k;
                        sr = (int)(sAxR * m);
                        sg = (int)(sAxG * m);
                        sb = (int)(sAxB * m);
                    }

                    ar[s] = sr << 4;
                    ag[s] = sg << 4;
                    ab[s] = sb << 4;
                }

                slamMode[mode] = slam;
                lowDot[0] = lowDot[1] = lowDot[2] = int.MaxValue;
                highDot[0] = highDot[1] = highDot[2] = int.MinValue;
                ref var _pp6 = ref MemoryMarshal.GetReference(px);
                for (var i = 0; i < 16; i++)
                {
                    ref readonly var _qq6 = ref Unsafe.Add(ref _pp6, i);
                    int s = Bc7Tables.Partition3[pat * 16 + i];
                    var dot = _qq6.R * ar[s] + _qq6.G * ag[s] + _qq6.B * ab[s] + i;
                    if (dot < lowDot[s]) lowDot[s] = dot;
                    if (dot > highDot[s]) highDot[s] = dot;
                }

                for (var s = 0; s < 3; s++)
                {
                    loC[bse + s] = lowDot[s] & 15;
                    hiC[bse + s] = highDot[s] & 15;
                }
            }

            float quant = 0;
            for (var s = 0; s < 3; s++)
            {
                int lc = loC[bse + s], hc = hiC[bse + s];
                spans[0] = px[hc].R - px[lc].R;
                spans[1] = px[hc].G - px[lc].G;
                spans[2] = px[hc].B - px[lc].B;
                if (mode == 0) quant += AnalyticalQuantEstSse(16, 8, 3, spans, default, UniquePbitDiscount, totC[bse + s]);
                else quant += AnalyticalQuantEstSse(32, 4, 3, spans, default, 1f, totC[bse + s]);
            }

            quantMode[mode] = quant;
        }

        float est0 = quantMode[0] + slamMode[0], est2 = quantMode[1] + slamMode[1];
        Span<int> w = stackalloc int[16];
        Span<uint> lr = stackalloc uint[3];
        Span<uint> lg = stackalloc uint[3];
        Span<uint> lb = stackalloc uint[3];
        Span<uint> hr = stackalloc uint[3];
        Span<uint> hg = stackalloc uint[3];
        Span<uint> hb = stackalloc uint[3];

        var pbitOpt = (flags & Bc7Flags.PBitOpt) != 0;
        Span<float> xl = stackalloc float[4];
        Span<float> xh = stackalloc float[4];
        Span<float> sxl = stackalloc float[4];
        Span<float> sxh = stackalloc float[4];
        Span<int> minC = stackalloc int[4];
        Span<int> maxC = stackalloc int[4];
        Span<uint> pbo = stackalloc uint[2];
        Span<int> eLoR = stackalloc int[3];
        Span<int> eLoG = stackalloc int[3];
        Span<int> eLoB = stackalloc int[3];
        Span<int> eHiR = stackalloc int[3];
        Span<int> eHiG = stackalloc int[3];
        Span<int> eHiB = stackalloc int[3];

        if (est0 < est2)
        {
            // mode 0: 4-bit endpoints, 6 unique p-bits, 3-bit indices, 3 subsets.
            estSse = est0;
            if (est0 >= sseEstToBeat) return false;

            Span<uint> pbits = stackalloc uint[6];
            for (var s = 0; s < 3; s++)
            {
                int lc = loC[s], hc = hiC[s];
                if (pbitOpt)
                {
                    sxl[0] = px[lc].R / 255f;
                    sxl[1] = px[lc].G / 255f;
                    sxl[2] = px[lc].B / 255f;
                    sxl[3] = 0f;
                    sxh[0] = px[hc].R / 255f;
                    sxh[1] = px[hc].G / 255f;
                    sxh[2] = px[hc].B / 255f;
                    sxh[3] = 0f;
                    DetermineUniquePbits(3, 4, sxl, sxh, minC, maxC, pbo);
                    pbits[s * 2] = pbo[0];
                    pbits[s * 2 + 1] = pbo[1];
                    lr[s] = (uint)minC[0];
                    lg[s] = (uint)minC[1];
                    lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0];
                    hg[s] = (uint)maxC[1];
                    hb[s] = (uint)maxC[2];
                }
                else
                {
                    var lp = (uint)(px[lc].R + px[lc].G + px[lc].B >= 129 ? 1 : 0);
                    pbits[s * 2] = lp;
                    lr[s] = (uint)To4(px[lc].R, lp);
                    lg[s] = (uint)To4(px[lc].G, lp);
                    lb[s] = (uint)To4(px[lc].B, lp);
                    var hp = (uint)(px[hc].R + px[hc].G + px[hc].B >= 129 ? 1 : 0);
                    pbits[s * 2 + 1] = hp;
                    hr[s] = (uint)To4(px[hc].R, hp);
                    hg[s] = (uint)To4(px[hc].G, hp);
                    hb[s] = (uint)To4(px[hc].B, hp);
                }
            }

            EvalWeightsMode0(px, w, lr, lg, lb, hr, hg, hb, pbits, (uint)patM0);
            for (var s = 0; s < 3; s++)
            {
                if (!ComputeLs3DSubP(w, px, patM0, s, Bc7Tables.LsTab3, totR[s], totG[s], totB[s], xl, xh)) continue;

                if (pbitOpt)
                {
                    sxl[0] = xl[0] / 255f;
                    sxl[1] = xl[1] / 255f;
                    sxl[2] = xl[2] / 255f;
                    sxl[3] = 0f;
                    sxh[0] = xh[0] / 255f;
                    sxh[1] = xh[1] / 255f;
                    sxh[2] = xh[2] / 255f;
                    sxh[3] = 0f;
                    DetermineUniquePbits(3, 4, sxl, sxh, minC, maxC, pbo);
                    pbits[s * 2] = pbo[0];
                    pbits[s * 2 + 1] = pbo[1];
                    lr[s] = (uint)minC[0];
                    lg[s] = (uint)minC[1];
                    lb[s] = (uint)minC[2];
                    hr[s] = (uint)maxC[0];
                    hg[s] = (uint)maxC[1];
                    hb[s] = (uint)maxC[2];
                }
                else
                {
                    int l0 = RoundPos(xl[0]), l1 = RoundPos(xl[1]), l2 = RoundPos(xl[2]);
                    int h0 = RoundPos(xh[0]), h1 = RoundPos(xh[1]), h2 = RoundPos(xh[2]);
                    var lp = (uint)(l0 + l1 + l2 >= 129 ? 1 : 0);
                    pbits[s * 2] = lp;
                    lr[s] = (uint)To4(l0, lp);
                    lg[s] = (uint)To4(l1, lp);
                    lb[s] = (uint)To4(l2, lp);
                    var hp = (uint)(h0 + h1 + h2 >= 129 ? 1 : 0);
                    pbits[s * 2 + 1] = hp;
                    hr[s] = (uint)To4(h0, hp);
                    hg[s] = (uint)To4(h1, hp);
                    hb[s] = (uint)To4(h2, hp);
                }
            }

            EvalWeightsMode0(px, w, lr, lg, lb, hr, hg, hb, pbits, (uint)patM0);
            for (var s = 0; s < 3; s++)
            {
                eLoR[s] = From4((int)lr[s], pbits[s * 2]);
                eLoG[s] = From4((int)lg[s], pbits[s * 2]);
                eLoB[s] = From4((int)lb[s], pbits[s * 2]);
                eHiR[s] = From4((int)hr[s], pbits[s * 2 + 1]);
                eHiG[s] = From4((int)hg[s], pbits[s * 2 + 1]);
                eHiB[s] = From4((int)hb[s], pbits[s * 2 + 1]);
            }

            if (wantSse)
            {
                var sx = SseMultiSubsetRgb(eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, Bc7Tables.Partition3, patM0 * 16, Bc7Tables.Weights3, w, px);
                trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx;
            }

            EncodeMode0(block, (uint)patM0, lr, lg, lb, hr, hg, hb, pbits, w);
        }
        else
        {
            // mode 2: 5-bit endpoints, no p-bits, 2-bit indices, 3 subsets.
            estSse = est2;
            if (est2 >= sseEstToBeat) return false;

            for (var s = 0; s < 3; s++)
            {
                int lc = loC[3 + s], hc = hiC[3 + s];
                lr[s] = (uint)To5(px[lc].R);
                lg[s] = (uint)To5(px[lc].G);
                lb[s] = (uint)To5(px[lc].B);
                hr[s] = (uint)To5(px[hc].R);
                hg[s] = (uint)To5(px[hc].G);
                hb[s] = (uint)To5(px[hc].B);
            }

            EvalWeightsMode2(px, w, lr, lg, lb, hr, hg, hb, (uint)patM2);
            for (var s = 0; s < 3; s++)
            {
                if (!ComputeLs3DSubP(w, px, patM2, s, Bc7Tables.LsTab2, totR[3 + s], totG[3 + s], totB[3 + s], xl, xh)) continue;

                lr[s] = (uint)To5(RoundPos(xl[0]));
                lg[s] = (uint)To5(RoundPos(xl[1]));
                lb[s] = (uint)To5(RoundPos(xl[2]));
                hr[s] = (uint)To5(RoundPos(xh[0]));
                hg[s] = (uint)To5(RoundPos(xh[1]));
                hb[s] = (uint)To5(RoundPos(xh[2]));
            }

            EvalWeightsMode2(px, w, lr, lg, lb, hr, hg, hb, (uint)patM2);
            for (var s = 0; s < 3; s++)
            {
                eLoR[s] = From5((int)lr[s]);
                eLoG[s] = From5((int)lg[s]);
                eLoB[s] = From5((int)lb[s]);
                eHiR[s] = From5((int)hr[s]);
                eHiG[s] = From5((int)hg[s]);
                eHiB[s] = From5((int)hb[s]);
            }

            if (wantSse)
            {
                var sx = SseMultiSubsetRgb(eLoR, eLoG, eLoB, eHiR, eHiG, eHiB, Bc7Tables.Partition3, patM2 * 16, Bc7Tables.Weights2, w, px);
                trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx;
            }

            EncodeMode2(block, (uint)patM2, lr, lg, lb, hr, hg, hb, w);
        }

        return true;
    }

    // 3-subset 3-bit index eval (eval_weights_mode0_rgb).
    static void EvalWeightsMode0(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<int> w,
        scoped ReadOnlySpan<uint> blr,
        scoped ReadOnlySpan<uint> blg,
        scoped ReadOnlySpan<uint> blb,
        scoped ReadOnlySpan<uint> bhr,
        scoped ReadOnlySpan<uint> bhg,
        scoped ReadOnlySpan<uint> bhb,
        scoped ReadOnlySpan<uint> pbits,
        uint pat)
    {
        Span<int> dr = stackalloc int[3];
        Span<int> dg = stackalloc int[3];
        Span<int> db = stackalloc int[3];
        Span<float> f = stackalloc float[3];
        Span<int> sofs = stackalloc int[3];
        Span<int> lr = stackalloc int[3];
        Span<int> lg = stackalloc int[3];
        Span<int> lb = stackalloc int[3];
        for (var s = 0; s < 3; s++)
        {
            lr[s] = From4((int)blr[s], pbits[s * 2]);
            lg[s] = From4((int)blg[s], pbits[s * 2]);
            lb[s] = From4((int)blb[s], pbits[s * 2]);
            int hr = From4((int)bhr[s], pbits[s * 2 + 1]), hg = From4((int)bhg[s], pbits[s * 2 + 1]), hb = From4((int)bhb[s], pbits[s * 2 + 1]);
            dr[s] = hr - lr[s];
            dg[s] = hg - lg[s];
            db[s] = hb - lb[s];
            f[s] = 7.0f / (dr[s] * dr[s] + dg[s] * dg[s] + db[s] * db[s] + 0.00000125f);
            sofs[s] = lr[s] * dr[s] + lg[s] * dg[s] + lb[s] * db[s];
        }

        EvalSelRgb3(px, w, dr, dg, db, sofs, f, Bc7Tables.Partition3.AsSpan((int)pat * 16, 16), 7);
    }

    // 3-subset 2-bit index eval (eval_weights_mode2_rgb). No p-bits.
    static void EvalWeightsMode2(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<int> w,
        scoped ReadOnlySpan<uint> blr,
        scoped ReadOnlySpan<uint> blg,
        scoped ReadOnlySpan<uint> blb,
        scoped ReadOnlySpan<uint> bhr,
        scoped ReadOnlySpan<uint> bhg,
        scoped ReadOnlySpan<uint> bhb,
        uint pat)
    {
        Span<int> dr = stackalloc int[3];
        Span<int> dg = stackalloc int[3];
        Span<int> db = stackalloc int[3];
        Span<float> f = stackalloc float[3];
        Span<int> sofs = stackalloc int[3];
        Span<int> lr = stackalloc int[3];
        Span<int> lg = stackalloc int[3];
        Span<int> lb = stackalloc int[3];
        for (var s = 0; s < 3; s++)
        {
            lr[s] = From5((int)blr[s]);
            lg[s] = From5((int)blg[s]);
            lb[s] = From5((int)blb[s]);
            int hr = From5((int)bhr[s]), hg = From5((int)bhg[s]), hb = From5((int)bhb[s]);
            dr[s] = hr - lr[s];
            dg[s] = hg - lg[s];
            db[s] = hb - lb[s];
            f[s] = 3.0f / (dr[s] * dr[s] + dg[s] * dg[s] + db[s] * db[s] + 0.00000125f);
            sofs[s] = lr[s] * dr[s] + lg[s] * dg[s] + lb[s] * db[s];
        }

        EvalSelRgb3(px, w, dr, dg, db, sofs, f, Bc7Tables.Partition3.AsSpan((int)pat * 16, 16), 3);
    }

    // BC7 mode-0 block writer (encode_mode0_rgb_block). 4-bit part id, 6 unique p-bits, 3-bit indices.
    static void EncodeMode0(scoped Span<byte> block,
        uint partId,
        scoped Span<uint> lr,
        scoped Span<uint> lg,
        scoped Span<uint> lb,
        scoped Span<uint> hr,
        scoped Span<uint> hg,
        scoped Span<uint> hb,
        scoped Span<uint> p,
        scoped ReadOnlySpan<int> weights)
    {
        int a0 = Bc7Tables.AnchorThird1[(int)partId], a1 = Bc7Tables.AnchorThird2[(int)partId];
        Span<uint> winv = stackalloc uint[3];
        if ((weights[0] & 4) != 0)
        {
            (lr[0], hr[0]) = (hr[0], lr[0]);
            (lg[0], hg[0]) = (hg[0], lg[0]);
            (lb[0], hb[0]) = (hb[0], lb[0]);
            (p[0], p[1]) = (p[1], p[0]);
            winv[0] = 7;
        }

        if ((weights[a0] & 4) != 0)
        {
            (lr[1], hr[1]) = (hr[1], lr[1]);
            (lg[1], hg[1]) = (hg[1], lg[1]);
            (lb[1], hb[1]) = (hb[1], lb[1]);
            (p[2], p[3]) = (p[3], p[2]);
            winv[1] = 7;
        }

        if ((weights[a1] & 4) != 0)
        {
            (lr[2], hr[2]) = (hr[2], lr[2]);
            (lg[2], hg[2]) = (hg[2], lg[2]);
            (lb[2], hb[2]) = (hb[2], lb[2]);
            (p[4], p[5]) = (p[5], p[4]);
            winv[2] = 7;
        }

        var low = 1UL | (ulong)partId << 1
            | (ulong)lr[0] << 5 | (ulong)hr[0] << 9
            | (ulong)lr[1] << 13 | (ulong)hr[1] << 17
            | (ulong)lr[2] << 21 | (ulong)hr[2] << 25
            | (ulong)lg[0] << 29 | (ulong)hg[0] << 33
            | (ulong)lg[1] << 37 | (ulong)hg[1] << 41
            | (ulong)lg[2] << 45 | (ulong)hg[2] << 49
            | (ulong)lb[0] << 53 | (ulong)hb[0] << 57
            | (ulong)lb[1] << 61;

        block[0] = (byte)low;
        block[1] = (byte)(low >> 8);
        block[2] = (byte)(low >> 16);
        block[3] = (byte)(low >> 24);
        block[4] = (byte)(low >> 32);
        block[5] = (byte)(low >> 40);
        block[6] = (byte)(low >> 48);
        block[7] = (byte)(low >> 56);

        var high = (ulong)lb[1] >> 3 | (ulong)hb[1] << 1 | (ulong)lb[2] << 5 | (ulong)hb[2] << 9
            | (ulong)p[0] << 13 | (ulong)p[1] << 14 | (ulong)p[2] << 15 | (ulong)p[3] << 16 | (ulong)p[4] << 17 | (ulong)p[5] << 18;

        var ofs = 19;
        for (var i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Partition3[(int)partId * 16 + i];
            ulong wv = (uint)(weights[i] ^ (int)winv[s]);
            high |= wv << ofs;
            ofs += 3 - (i == 0 || i == a0 || i == a1 ? 1 : 0);
        }

        block[8] = (byte)high;
        block[9] = (byte)(high >> 8);
        block[10] = (byte)(high >> 16);
        block[11] = (byte)(high >> 24);
        block[12] = (byte)(high >> 32);
        block[13] = (byte)(high >> 40);
        block[14] = (byte)(high >> 48);
        block[15] = (byte)(high >> 56);
    }

    // BC7 mode-2 block writer (encode_mode2_rgb_block). 6-bit part id, no p-bits, 2-bit indices.
    static void EncodeMode2(scoped Span<byte> block,
        uint partId,
        scoped Span<uint> lr,
        scoped Span<uint> lg,
        scoped Span<uint> lb,
        scoped Span<uint> hr,
        scoped Span<uint> hg,
        scoped Span<uint> hb,
        scoped ReadOnlySpan<int> weights)
    {
        int a0 = Bc7Tables.AnchorThird1[(int)partId], a1 = Bc7Tables.AnchorThird2[(int)partId];
        Span<uint> winv = stackalloc uint[3];
        if ((weights[0] & 2) != 0)
        {
            (lr[0], hr[0]) = (hr[0], lr[0]);
            (lg[0], hg[0]) = (hg[0], lg[0]);
            (lb[0], hb[0]) = (hb[0], lb[0]);
            winv[0] = 3;
        }

        if ((weights[a0] & 2) != 0)
        {
            (lr[1], hr[1]) = (hr[1], lr[1]);
            (lg[1], hg[1]) = (hg[1], lg[1]);
            (lb[1], hb[1]) = (hb[1], lb[1]);
            winv[1] = 3;
        }

        if ((weights[a1] & 2) != 0)
        {
            (lr[2], hr[2]) = (hr[2], lr[2]);
            (lg[2], hg[2]) = (hg[2], lg[2]);
            (lb[2], hb[2]) = (hb[2], lb[2]);
            winv[2] = 3;
        }

        var v = 0b100UL | (ulong)partId << 3
            | (ulong)lr[0] << 9 | (ulong)hr[0] << 14
            | (ulong)lr[1] << 19 | (ulong)hr[1] << 24
            | (ulong)lr[2] << 29 | (ulong)hr[2] << 34
            | (ulong)lg[0] << 39 | (ulong)hg[0] << 44
            | (ulong)lg[1] << 49 | (ulong)hg[1] << 54
            | (ulong)lg[2] << 59;

        block[0] = (byte)v;
        block[1] = (byte)(v >> 8);
        block[2] = (byte)(v >> 16);
        block[3] = (byte)(v >> 24);
        block[4] = (byte)(v >> 32);
        block[5] = (byte)(v >> 40);
        block[6] = (byte)(v >> 48);
        block[7] = (byte)(v >> 56);

        var v1 = hg[2]
            | (ulong)lb[0] << 5 | (ulong)hb[0] << 10
            | (ulong)lb[1] << 15 | (ulong)hb[1] << 20
            | (ulong)lb[2] << 25 | (ulong)hb[2] << 30;

        block[8] = (byte)v1;
        block[9] = (byte)(v1 >> 8);
        block[10] = (byte)(v1 >> 16);
        block[11] = (byte)(v1 >> 24);
        v1 >>= 32;
        var ofs = 3;
        for (var i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Partition3[(int)partId * 16 + i];
            ulong wv = (uint)(weights[i] ^ (int)winv[s]);
            v1 |= wv << ofs;
            ofs += 2 - (i == 0 || i == a0 || i == a1 ? 1 : 0);
        }

        block[12] = (byte)v1;
        block[13] = (byte)(v1 >> 8);
        block[14] = (byte)(v1 >> 16);
        block[15] = (byte)(v1 >> 24);
    }
}