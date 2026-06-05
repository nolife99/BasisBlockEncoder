// Bc7Rgba.cs — the RGBA analytical path (faithful port of fast_pack_bc7_rgba_analytical).
// 4D analogue of the RGB brain: solid early-out, 4D covariance/PCA, dual-plane detection (prefer alpha),
// trivial mode 6, then an estimate->bail cascade across modes 4/5 (dual plane), 7 (2-subset), and 6.
// Uses fixed p-bits; the least-squares endpoint refinement is not applied.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bcn.Bc7;

internal static partial class Bc7Block
{
    // RGBA escalation thresholds (active #else config in bc7f).
    private const int   DpBlockVarThreshRgba          = 1 * 16;
    private const float AlphaDecorrThreshold          = 0.995f;
    private const float StrongDecorrThreshRgba        = 0.85f;
    private const int   TrivialBlockThreshRgba        = 2 * 16;
    private const int   MinBlockMaxVar23SubsetsRgba   = 100 * 16;
    private const float HighOrthoEnergyThreshRgba     = 1.0f * 16;
    private const float OrthoRatio23SubsetThreshRgba  = 0.004f;

    private static void PackRgbaAnalytical(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block, Bc7Flags flags)
    {
        uint fc = AsU32(in px[0]);
        bool solid = true;
        for (int k = 1; k < 16; k++) if (AsU32(in px[k]) != fc) { solid = false; break; }
        if (solid) { PackMode5Solid(block, px[0]); return; }

        Span<int> bs = stackalloc int[12];
        BlockStatsRgba(px, bs);
        int tR = bs[0], tG = bs[1], tB = bs[2], tA = bs[3];
        int minR = bs[4], minG = bs[5], minB = bs[6], minA = bs[7], maxR = bs[8], maxG = bs[9], maxB = bs[10], maxA = bs[11];
        int meanR = (tR + 8) >> 4, meanG = (tG + 8) >> 4, meanB = (tB + 8) >> 4, meanA = (tA + 8) >> 4;

        Span<int> icov = stackalloc int[10];
        CovarianceRgba(px, meanR, meanG, meanB, meanA, icov);
        int bmv4 = Math.Max(Math.Max(icov[0], icov[4]), Math.Max(icov[7], icov[9]));

        // dual-plane detection: prefer alpha, else the weakest-correlated RGB channel.
        int dpChan = -1;
        if ((flags & Bc7Flags.UseDualPlaneRgba) != 0 && bmv4 >= DpBlockVarThreshRgba)
        {
            float rv = icov[0], gv = icov[4], bv = icov[7], av = icov[9];
            if (icov[9] > 0)
            {
                float p03 = icov[0] != 0 ? MathF.Abs(icov[3] / MathF.Sqrt(rv * av)) : 1f;
                float p13 = icov[4] != 0 ? MathF.Abs(icov[6] / MathF.Sqrt(gv * av)) : 1f;
                float p23 = icov[7] != 0 ? MathF.Abs(icov[8] / MathF.Sqrt(bv * av)) : 1f;
                if (MathF.Min(p03, MathF.Min(p13, p23)) < AlphaDecorrThreshold) dpChan = 3;
            }
            if (dpChan < 0)
            {
                bool hasR = icov[0] > 16, hasG = icov[4] > 16, hasB = icov[7] > 16;
                int act = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
                if (act >= 2)
                {
                    float rg = (hasR && hasG) ? MathF.Abs(icov[1] / MathF.Sqrt(rv * gv)) : 1f;
                    float rb = (hasR && hasB) ? MathF.Abs(icov[2] / MathF.Sqrt(rv * bv)) : 1f;
                    float gb = (hasG && hasB) ? MathF.Abs(icov[5] / MathF.Sqrt(gv * bv)) : 1f;
                    if (MathF.Min(rg, MathF.Min(rb, gb)) < StrongDecorrThreshRgba)
                    {
                        if (act == 2) dpChan = !hasR ? 1 : 0;
                        else if (rg < gb && rb < gb) dpChan = 0;
                        else if (rg < rb && gb < rb) dpChan = 1;
                        else dpChan = 2;
                    }
                }
            }
        }

        // trivial mode 6 (flat RGBA block)
        if ((flags & Bc7Flags.UseTrivialMode6) != 0 && dpChan == -1 && bmv4 < TrivialBlockThreshRgba)
        {
            PackMode6RgbaTrivialOrDefault(px, block, useAxis: false, 0, 0, 0, 0, tR, tG, tB, tA, flags, false, out _);
            return;
        }

        Span<float> cov = stackalloc float[10];
        for (int i = 0; i < 10; i++) cov[i] = icov[i];
        EstimatePrincipalAxis4D(cov, bmv4, out float x1, out float y1, out float z1, out float w1);

        Span<int> spans = stackalloc int[4];
        spans[0] = maxR - minR; spans[1] = maxG - minG; spans[2] = maxB - minB; spans[3] = maxA - minA;
        float mode6Slam = EstimateSlamToLineSse4D(cov, x1, y1, z1, w1, out float orthoRatio);
        float mode6Est = mode6Slam + AnalyticalQuantEstSse(128, 16, 4, spans, default, 1.0f, 16);

        float mode45Est = 1e9f, mode7Est = 1e9f;
        Span<byte> b45 = stackalloc byte[16]; Span<byte> b7 = stackalloc byte[16];
        bool m45 = (dpChan >= 0) && PackMode4Or5(b45, px, dpChan, mode6Est, flags, false, out mode45Est, out _);

        if ((flags & Bc7Flags.Use2SubsetsRgba) != 0 && bmv4 >= MinBlockMaxVar23SubsetsRgba && orthoRatio > OrthoRatio23SubsetThreshRgba
            && mode6Slam >= HighOrthoEnergyThreshRgba)
        {
            PackMode7Rgba(b7, px, x1, y1, z1, w1, meanR, meanG, meanB, meanA, mode6Est, flags, false, out mode7Est, out _);
        }

        if (mode45Est < mode7Est && mode45Est < mode6Est) { b45.CopyTo(block); return; }
        if (mode7Est < mode45Est && mode7Est < mode6Est) { b7.CopyTo(block); return; }

        PackMode6RgbaTrivialOrDefault(px, block, useAxis: true, x1, y1, z1, w1, tR, tG, tB, tA, flags, false, out _);
    }

    // Mode-6 RGBA commit. useAxis=false -> trivial luma endpoint pick; useAxis=true -> project onto the 4D axis.
    // bc7f::fast_pack_bc7_rgba_partial_analytical — partially-analytical (HighQuality) RGBA path. Mode-6
    // baseline plus the gated candidates (mode 7, mode 4/5), each actually encoded and scored by true
    // decoded SSE; the genuine minimum wins (tie-break 4/5 > 7 > 6, matching upstream).
    private static void PackRgbaPartialAnalytical(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block, Bc7Flags flags)
    {
        uint fc = AsU32(in px[0]);
        bool solid = true;
        for (int k = 1; k < 16; k++) if (AsU32(in px[k]) != fc) { solid = false; break; }
        if (solid) { PackMode5Solid(block, px[0]); return; }

        Span<int> bs = stackalloc int[12];
        BlockStatsRgba(px, bs);
        int tR = bs[0], tG = bs[1], tB = bs[2], tA = bs[3];
        int minR = bs[4], minG = bs[5], minB = bs[6], minA = bs[7], maxR = bs[8], maxG = bs[9], maxB = bs[10], maxA = bs[11];
        int meanR = (tR + 8) >> 4, meanG = (tG + 8) >> 4, meanB = (tB + 8) >> 4, meanA = (tA + 8) >> 4;

        Span<int> icov = stackalloc int[10];
        CovarianceRgba(px, meanR, meanG, meanB, meanA, icov);
        int bmv4 = Math.Max(Math.Max(icov[0], icov[4]), Math.Max(icov[7], icov[9]));

        int dpChan = -1;
        if ((flags & Bc7Flags.UseDualPlaneRgba) != 0 && bmv4 >= DpBlockVarThreshRgba)
        {
            float rv = icov[0], gv = icov[4], bv = icov[7], av = icov[9];
            if (icov[9] > 0)
            {
                float p03 = icov[0] != 0 ? MathF.Abs(icov[3] / MathF.Sqrt(rv * av)) : 1f;
                float p13 = icov[4] != 0 ? MathF.Abs(icov[6] / MathF.Sqrt(gv * av)) : 1f;
                float p23 = icov[7] != 0 ? MathF.Abs(icov[8] / MathF.Sqrt(bv * av)) : 1f;
                if (MathF.Min(p03, MathF.Min(p13, p23)) < AlphaDecorrThreshold) dpChan = 3;
            }
            if (dpChan < 0)
            {
                bool hasR = icov[0] > 16, hasG = icov[4] > 16, hasB = icov[7] > 16;
                int act = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
                if (act >= 2)
                {
                    float rg = (hasR && hasG) ? MathF.Abs(icov[1] / MathF.Sqrt(rv * gv)) : 1f;
                    float rb = (hasR && hasB) ? MathF.Abs(icov[2] / MathF.Sqrt(rv * bv)) : 1f;
                    float gb = (hasG && hasB) ? MathF.Abs(icov[5] / MathF.Sqrt(gv * bv)) : 1f;
                    if (MathF.Min(rg, MathF.Min(rb, gb)) < StrongDecorrThreshRgba)
                    {
                        if (act == 2) dpChan = !hasR ? 1 : 0;
                        else if (rg < gb && rb < gb) dpChan = 0;
                        else if (rg < rb && gb < rb) dpChan = 1;
                        else dpChan = 2;
                    }
                }
            }
        }

        if ((flags & Bc7Flags.UseTrivialMode6) != 0 && dpChan == -1 && bmv4 < TrivialBlockThreshRgba)
        {
            PackMode6RgbaTrivialOrDefault(px, block, useAxis: false, 0, 0, 0, 0, tR, tG, tB, tA, flags, false, out _);
            return;
        }

        Span<float> cov = stackalloc float[10];
        for (int i = 0; i < 10; i++) cov[i] = icov[i];
        EstimatePrincipalAxis4D(cov, bmv4, out float x1, out float y1, out float z1, out float w1);

        // mode 6 baseline, then its true SSE
        PackMode6RgbaTrivialOrDefault(px, block, useAxis: true, x1, y1, z1, w1, tR, tG, tB, tA, flags, true, out uint mode6Sse);

        if (mode6Sse != 0)
        {
            float mode6Slam = EstimateSlamToLineSse4D(cov, x1, y1, z1, w1, out float orthoRatio);

            Span<byte> b7 = stackalloc byte[16]; uint sse7 = uint.MaxValue;
            Span<byte> b45 = stackalloc byte[16]; uint sse45 = uint.MaxValue;

            if ((flags & Bc7Flags.Use2SubsetsRgba) != 0 && bmv4 >= MinBlockMaxVar23SubsetsRgba &&
                orthoRatio > OrthoRatio23SubsetThreshRgba && mode6Slam >= HighOrthoEnergyThreshRgba)
            {
                if (PackMode7Rgba(b7, px, x1, y1, z1, w1, meanR, meanG, meanB, meanA, 1e9f, flags, true, out _, out uint t7)) sse7 = t7;
            }

            if (dpChan >= 0 && PackMode4Or5(b45, px, dpChan, 1e9f, flags, true, out _, out uint t45)) sse45 = t45;

            uint best = Math.Min(mode6Sse, Math.Min(sse45, sse7));
            if (sse45 != uint.MaxValue && best == sse45) b45.CopyTo(block);
            else if (sse7 != uint.MaxValue && best == sse7) b7.CopyTo(block);
            // else mode 6 (already in block) wins
        }
    }

    private static void PackMode6RgbaTrivialOrDefault(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block,
        bool useAxis, float x1, float y1, float z1, float w1,
        int totR, int totG, int totB, int totA, Bc7Flags flags,
        bool wantSse, out uint trueSse)
    {
        int loIdx, hiIdx;
        if (!useAxis)
        {
            ProjMinMax4(px, 32, 64, 16, 64, out loIdx, out hiIdx);
        }
        else
        {
            int sr = 256, sg = 256, sb = 256, sa = 256;
            float k = MathF.Max(MathF.Max(MathF.Abs(x1), MathF.Abs(y1)), MathF.Max(MathF.Abs(z1), MathF.Abs(w1)));
            if (k >= SmallFloat) { float m = 2048f / k; sr = (int)(x1 * m); sg = (int)(y1 * m); sb = (int)(z1 * m); sa = (int)(w1 * m); }
            sr <<= 4; sg <<= 4; sb <<= 4; sa <<= 4;
            ProjMinMax4(px, sr, sg, sb, sa, out loIdx, out hiIdx);
        }
        bool pbitOptM6 = (flags & Bc7Flags.PBitOptMode6) != 0;
        Span<float> sxl = stackalloc float[4]; Span<float> sxh = stackalloc float[4];
        Span<int> minC = stackalloc int[4]; Span<int> maxC = stackalloc int[4]; Span<uint> pbo = stackalloc uint[2];
        uint p0, p1; int lr, lg, lb, la, hr, hg, hb, ha;
        if (pbitOptM6)
        {
            sxl[0]=px[loIdx].R/255f; sxl[1]=px[loIdx].G/255f; sxl[2]=px[loIdx].B/255f; sxl[3]=px[loIdx].A/255f;
            sxh[0]=px[hiIdx].R/255f; sxh[1]=px[hiIdx].G/255f; sxh[2]=px[hiIdx].B/255f; sxh[3]=px[hiIdx].A/255f;
            DetermineUniquePbits(4, 7, sxl, sxh, minC, maxC, pbo);
            p0=pbo[0]; p1=pbo[1]; lr=minC[0]; lg=minC[1]; lb=minC[2]; la=minC[3]; hr=maxC[0]; hg=maxC[1]; hb=maxC[2]; ha=maxC[3];
        }
        else
        {
            p0 = (uint)(px[loIdx].A > 128 ? 1 : 0); p1 = (uint)(px[hiIdx].A > 128 ? 1 : 0);
            lr = To7(px[loIdx].R, p0); lg = To7(px[loIdx].G, p0); lb = To7(px[loIdx].B, p0); la = To7(px[loIdx].A, p0);
            hr = To7(px[hiIdx].R, p1); hg = To7(px[hiIdx].G, p1); hb = To7(px[hiIdx].B, p1); ha = To7(px[hiIdx].A, p1);
        }
        Span<int> w = stackalloc int[16];
        EvalWeightsMode6Rgba(px, w, lr, lg, lb, la, p0, hr, hg, hb, ha, p1);

        // least-squares endpoint refinement (default fallback only; trivial/solid paths skip it, as upstream does)
        if (useAxis)
        {
            Span<float> xl = stackalloc float[4]; Span<float> xh = stackalloc float[4];
            if (ComputeLs4D(w, px, Bc7Tables.LsTab4, totR, totG, totB, totA, xl, xh))
            {
                if (pbitOptM6)
                {
                    sxl[0]=xl[0]/255f; sxl[1]=xl[1]/255f; sxl[2]=xl[2]/255f; sxl[3]=xl[3]/255f;
                    sxh[0]=xh[0]/255f; sxh[1]=xh[1]/255f; sxh[2]=xh[2]/255f; sxh[3]=xh[3]/255f;
                    DetermineUniquePbits(4, 7, sxl, sxh, minC, maxC, pbo);
                    p0=pbo[0]; p1=pbo[1]; lr=minC[0]; lg=minC[1]; lb=minC[2]; la=minC[3]; hr=maxC[0]; hg=maxC[1]; hb=maxC[2]; ha=maxC[3];
                }
                else
                {
                    p0 = (uint)(xl[3] >= 129f ? 1 : 0);
                    lr = To7(RoundPos(xl[0]), p0); lg = To7(RoundPos(xl[1]), p0); lb = To7(RoundPos(xl[2]), p0); la = To7(RoundPos(xl[3]), p0);
                    p1 = (uint)(xh[3] >= 129f ? 1 : 0);
                    hr = To7(RoundPos(xh[0]), p1); hg = To7(RoundPos(xh[1]), p1); hb = To7(RoundPos(xh[2]), p1); ha = To7(RoundPos(xh[3]), p1);
                }
                EvalWeightsMode6Rgba(px, w, lr, lg, lb, la, p0, hr, hg, hb, ha, p1);
            }
        }
        trueSse = 0;
        if (wantSse)
            trueSse = (uint)SseSingleSubsetRgba(
                From7(lr, p0), From7(lg, p0), From7(lb, p0), From7(la, p0),
                From7(hr, p1), From7(hg, p1), From7(hb, p1), From7(ha, p1),
                Bc7Tables.Weights4, w, px);

        EncodeMode6(block, (uint)lr, (uint)lg, (uint)lb, (uint)la, p0, (uint)hr, (uint)hg, (uint)hb, (uint)ha, p1, w);
    }

    // Mode-6 RGBA index eval (eval_weights_mode6_rgba): 4 channels, 4-bit indices, 7-bit+pbit endpoints.
    private static void EvalWeightsMode6Rgba(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w,
        int lr, int lg, int lb, int la, uint p0, int hr, int hg, int hb, int ha, uint p1)
    {
        lr = From7(lr, p0); lg = From7(lg, p0); lb = From7(lb, p0); la = From7(la, p0);
        hr = From7(hr, p1); hg = From7(hg, p1); hb = From7(hb, p1); ha = From7(ha, p1);
        int dr = hr - lr, dg = hg - lg, db = hb - lb, da = ha - la;
        float f = 15.0f / (dr * dr + dg * dg + db * db + da * da + 0.00000125f);
        int sofs = -(lr * dr + lg * dg + lb * db + la * da);
        EvalSelRgba(px, w, dr, dg, db, da, sofs, f, 15);
    }

    private static void EstimatePrincipalAxis4D(scoped ReadOnlySpan<float> cov, int bmv,
        out float x1, out float y1, out float z1, out float w1)
    {
        float sc = bmv != 0 ? 1f / bmv : 0f;
        float wx = sc * cov[0], wy = sc * cov[4], wz = sc * cov[7], wa = sc * cov[9];
        x1 = y1 = z1 = w1 = 0.25f;
        for (int it = 0; it < 4; it++)
        {
            x1 = cov[0]*wx + cov[1]*wy + cov[2]*wz + cov[3]*wa;
            y1 = cov[1]*wx + cov[4]*wy + cov[5]*wz + cov[6]*wa;
            z1 = cov[2]*wx + cov[5]*wy + cov[7]*wz + cov[8]*wa;
            w1 = cov[3]*wx + cov[6]*wy + cov[8]*wz + cov[9]*wa;
            float t = MathF.Sqrt(x1*x1 + y1*y1 + z1*z1 + w1*w1);
            if (t > SmallFloat) { t = 1f / t; x1 *= t; y1 *= t; z1 *= t; w1 *= t; }
            else { x1 = y1 = z1 = w1 = 0.25f; }
            wx = x1; wy = y1; wz = z1; wa = w1;
        }
    }

    private static float EstimateSlamToLineSse4D(scoped ReadOnlySpan<float> cov, float xr, float yr, float zr, float wr, out float orthoRatio)
    {
        float totalVar = cov[0] + cov[4] + cov[7] + cov[9];
        float l = MathF.Sqrt(xr*xr + yr*yr + zr*zr + wr*wr);
        if (l < SmallFloat) { xr = yr = zr = wr = 0.5f; }
        else { l = 1f / l; xr *= l; yr *= l; zr *= l; wr *= l; }
        float xr2 = cov[0]*xr + cov[1]*yr + cov[2]*zr + cov[3]*wr;
        float xg2 = cov[1]*xr + cov[4]*yr + cov[5]*zr + cov[6]*wr;
        float xb2 = cov[2]*xr + cov[5]*yr + cov[7]*zr + cov[8]*wr;
        float xa2 = cov[3]*xr + cov[6]*yr + cov[8]*zr + cov[9]*wr;
        float pav = xr2*xr + xg2*yr + xb2*zr + xa2*wr;
        float ov = MathF.Max(0f, totalVar - pav);
        orthoRatio = (totalVar > SmallFloat) ? (ov / totalVar) : 0f;
        return ov;
    }
}
