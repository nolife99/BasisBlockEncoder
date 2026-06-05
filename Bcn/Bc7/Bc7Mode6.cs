namespace Bcn.Bc7;

using System;
using System.Runtime.Intrinsics.X86;

static partial class Bc7Block
{
    // Default mode-6 commit: project onto the PCA axis to choose endpoints (index packed in low nibble).
    static void PackMode6Default(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<byte> block,
        float axR,
        float axG,
        float axB,
        int totR,
        int totG,
        int totB,
        Bc7Flags flags,
        bool wantSse,
        out uint trueSse)
    {
        // integer axis, scaled so dot products are multiples of 16 (the index packs into the low nibble).
        int sr = 306, sg = 601, sb = 117; // luma fallback for a degenerate axis
        var k = MathF.Max(MathF.Abs(axR), MathF.Max(MathF.Abs(axG), MathF.Abs(axB)));
        if (k >= SmallFloat)
        {
            var m = 2048f / k;
            sr = (int)(axR * m);
            sg = (int)(axG * m);
            sb = (int)(axB * m);
        }

        sr <<= 4;
        sg <<= 4;
        sb <<= 4;

        ProjMinMax3(px, sr, sg, sb, out var loIdx, out var hiIdx);
        var pbitOptM6 = (flags & Bc7Flags.PBitOptMode6) != 0;
        Span<float> sxl = stackalloc float[4];
        Span<float> sxh = stackalloc float[4];
        Span<int> minC = stackalloc int[4];
        Span<int> maxC = stackalloc int[4];
        Span<uint> pbo = stackalloc uint[2];

        uint p0, p1;
        int lr, lg, lb, hr, hg, hb;
        if (pbitOptM6)
        {
            sxl[0] = px[loIdx].R / 255f;
            sxl[1] = px[loIdx].G / 255f;
            sxl[2] = px[loIdx].B / 255f;
            sxl[3] = 0f;
            sxh[0] = px[hiIdx].R / 255f;
            sxh[1] = px[hiIdx].G / 255f;
            sxh[2] = px[hiIdx].B / 255f;
            sxh[3] = 0f;
            DetermineUniquePbits(3, 7, sxl, sxh, minC, maxC, pbo);
            p0 = pbo[0];
            p1 = pbo[1];
            lr = minC[0];
            lg = minC[1];
            lb = minC[2];
            hr = maxC[0];
            hg = maxC[1];
            hb = maxC[2];
        }
        else
        {
            p0 = 1;
            p1 = 1; // fixed p-bits (keeps alpha 255; the p-bit search isn't worth its cost here)
            lr = To7(px[loIdx].R, p0);
            lg = To7(px[loIdx].G, p0);
            lb = To7(px[loIdx].B, p0);
            hr = To7(px[hiIdx].R, p1);
            hg = To7(px[hiIdx].G, p1);
            hb = To7(px[hiIdx].B, p1);
        }

        Span<int> w = stackalloc int[16];
        EvalWeightsMode6(px, w, lr, lg, lb, hr, hg, hb, p0, p1);

        // one least-squares endpoint refinement + re-eval (bc7f mode-6 path)
        Span<float> xl = stackalloc float[4];
        Span<float> xh = stackalloc float[4];
        if (ComputeLs3D(w, px, Bc7Tables.LsTab4, totR, totG, totB, xl, xh))
        {
            if (pbitOptM6)
            {
                sxl[0] = xl[0] / 255f;
                sxl[1] = xl[1] / 255f;
                sxl[2] = xl[2] / 255f;
                sxl[3] = 0f;
                sxh[0] = xh[0] / 255f;
                sxh[1] = xh[1] / 255f;
                sxh[2] = xh[2] / 255f;
                sxh[3] = 0f;
                DetermineUniquePbits(3, 7, sxl, sxh, minC, maxC, pbo);
                p0 = pbo[0];
                p1 = pbo[1];
                lr = minC[0];
                lg = minC[1];
                lb = minC[2];
                hr = maxC[0];
                hg = maxC[1];
                hb = maxC[2];
            }
            else
            {
                p0 = 1;
                p1 = 1;
                lr = To7(RoundPos(xl[0]), p0);
                lg = To7(RoundPos(xl[1]), p0);
                lb = To7(RoundPos(xl[2]), p0);
                hr = To7(RoundPos(xh[0]), p1);
                hg = To7(RoundPos(xh[1]), p1);
                hb = To7(RoundPos(xh[2]), p1);
            }
        }

        trueSse = 0;
        if (wantSse && Fma.IsSupported && Avx2.IsSupported)
        {
            // fused: final selectors (w) + reconstruction SSE in one pass over the 16 px (replaces the final
            // EvalWeightsMode6 + SseSingleSubsetRgba). Byte-identical to that composition on x86.
            trueSse = (uint)EvalSelSseRgb6X86(px, w, lr, lg, lb, hr, hg, hb, p0, p1);
        }
        else
        {
            EvalWeightsMode6(px, w, lr, lg, lb, hr, hg, hb, p0, p1); // final selectors (for the packer)
            if (wantSse)
                trueSse = (uint)SseSingleSubsetRgba(
                    From7(lr, p0),
                    From7(lg, p0),
                    From7(lb, p0),
                    From7(127, p0),
                    From7(hr, p1),
                    From7(hg, p1),
                    From7(hb, p1),
                    From7(127, p1),
                    Bc7Tables.Weights4,
                    w,
                    px);
        }

        EncodeMode6(block,
            (uint)lr,
            (uint)lg,
            (uint)lb,
            127,
            p0,
            (uint)hr,
            (uint)hg,
            (uint)hb,
            127,
            p1,
            w);
    }

    /// <summary>Index selection for mode 6: width-adaptive SIMD with a scalar fallback.</summary>
    // Mode-6 RGB weight eval. Ref-walk (no per-element bounds checks): at 16 elements the light per-pixel
    // work never amortizes SIMD coefficient setup, so eliminating the px[i]/w[i] bounds checks is what helps.
    static void EvalWeightsMode6(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<int> w,
        int lr,
        int lg,
        int lb,
        int hr,
        int hg,
        int hb,
        uint p0,
        uint p1)
    {
        lr = From7(lr, p0);
        lg = From7(lg, p0);
        lb = From7(lb, p0);
        hr = From7(hr, p1);
        hg = From7(hg, p1);
        hb = From7(hb, p1);
        int dr = hr - lr, dg = hg - lg, db = hb - lb;
        var f = 15.0f / (dr * dr + dg * dg + db * db + 0.00000125f);
        var sofs = -(lr * dr + lg * dg + lb * db);
        EvalSelRgb(px, w, dr, dg, db, sofs, f, 15);
    }

    /// <summary>BC7 mode-6 block writer. Sequential bit packing — inherently scalar, and cheap.</summary>
    static void EncodeMode6(scoped Span<byte> block,
        uint lr,
        uint lg,
        uint lb,
        uint la,
        uint p0,
        uint hr,
        uint hg,
        uint hb,
        uint ha,
        uint p1,
        scoped ReadOnlySpan<int> weights)
    {
        uint weightInv = 0;
        if ((weights[0] & 8) != 0) // anchor index MSB must be 0 -> swap endpoints + invert indices
        {
            (lr, hr) = (hr, lr);
            (lg, hg) = (hg, lg);
            (lb, hb) = (hb, lb);
            (la, ha) = (ha, la);
            (p0, p1) = (p1, p0);
            weightInv = 15;
        }

        var x = 0b1000000UL // mode 6 marker (six 0s then a 1)
            | (ulong)lr << 7 | (ulong)hr << 14
            | (ulong)lg << 21 | (ulong)hg << 28
            | (ulong)lb << 35 | (ulong)hb << 42
            | (ulong)la << 49 | (ulong)ha << 56;

        block[0] = (byte)x;
        block[1] = (byte)(x >> 8);
        block[2] = (byte)(x >> 16);
        block[3] = (byte)(x >> 24);
        block[4] = (byte)(x >> 32);
        block[5] = (byte)(x >> 40);
        block[6] = (byte)(x >> 48);
        x >>= 56; // 7 bits of ha remain
        x |= (ulong)p0 << 7;
        block[7] = (byte)x;

        ulong y = p1;
        var ofs = 1;
        for (var i = 0; i < 16; i++)
        {
            ulong wv = (uint)(weights[i] ^ (int)weightInv);
            y |= wv << ofs;
            ofs += 3 + (i > 0 ? 1 : 0); // anchor index is 3 bits, the rest 4
        }

        block[8] = (byte)y;
        block[9] = (byte)(y >> 8);
        block[10] = (byte)(y >> 16);
        block[11] = (byte)(y >> 24);
        block[12] = (byte)(y >> 32);
        block[13] = (byte)(y >> 40);
        block[14] = (byte)(y >> 48);
        block[15] = (byte)(y >> 56);
    }
}