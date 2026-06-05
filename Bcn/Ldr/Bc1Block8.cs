// Bc1Block8.cs — BC1 multi-block ("lane = block") encoder path (namespace Bcn.Ldr).
//
// 8 independent 4x4 blocks are encoded in parallel, one per Vector256 lane. Each per-block scalar step
// (power iteration, 2x2 solve, 565 rounding) becomes a single SIMD op across the 8 blocks; each 16-pixel
// loop becomes 16 vector iterations that accumulate along the pixel dimension, one lane per block.
//
// Because SIMD float/int ops are per-lane IEEE-identical to the scalar path (no cross-lane reduction
// reorder; FMA is avoided to keep the two-rounding form) and every control-flow divergence is turned into
// a masked blend, the result is bit-identical to the per-block encoder, stage by stage. V256-only (AVX2);
// the driver falls back to the per-block path on net7/ARM,
// so this whole unit is gated to keep the net7 build on the cross-platform-safe subset.
#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bcn.Ldr;

internal static partial class Bc1Block
{
    // ---- vectorized scalar-helper equivalents (bit-identical to the per-block scalar forms) ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> To5x8(Vector256<int> v)
    { Vector256<int> t = v * Vector256.Create(31) + Vector256.Create(128); return Vector256.ShiftRightArithmetic(t + Vector256.ShiftRightArithmetic(t, 8), 8); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> To6x8(Vector256<int> v)
    { Vector256<int> t = v * Vector256.Create(63) + Vector256.Create(128); return Vector256.ShiftRightArithmetic(t + Vector256.ShiftRightArithmetic(t, 8), 8); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Scale5To8x8(Vector256<int> v) => (v * Vector256.Create(8)) | Vector256.ShiftRightArithmetic(v, 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Scale6To8x8(Vector256<int> v) => (v * Vector256.Create(4)) | Vector256.ShiftRightArithmetic(v, 4);
    // floor(x/3) for 0 <= x <= 765 via the standard round-up reciprocal (exact in that range).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Div3x8(Vector256<int> x) => Vector256.ShiftRightArithmetic(x * Vector256.Create(43691), 17);
    // exact IEEE negate (sign-bit flip), matching scalar -x including on +/-0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Neg8(Vector256<float> x) => (x.AsInt32() ^ Vector256.Create(unchecked((int)0x80000000))).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> MulAdd(Vector256<float> a, Vector256<float> b, Vector256<float> c)
#if NET9_0_OR_GREATER
        => Vector256.MultiplyAddEstimate(a, b, c);
#else
        => a * b + c;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Split8(Vector256<uint> u, Vector256<uint> mask8, out Vector256<int> r, out Vector256<int> g, out Vector256<int> b)
    { r = (u & mask8).AsInt32(); g = (Vector256.ShiftRightLogical(u, 8) & mask8).AsInt32(); b = (Vector256.ShiftRightLogical(u, 16) & mask8).AsInt32(); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8R(ref uint t0, int i, Vector256<uint> mask8, out Vector256<int> g, out Vector256<int> b)
    { Split8(Vector256.LoadUnsafe(ref Unsafe.Add(ref t0, (nuint)(i * 8))), mask8, out var r, out g, out b); return r; }

    // per-lane gather from a small float / byte table (scalar gather; indices are in range by construction).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> GatherF(float[] tab, Vector256<int> idx)
        => Vector256.Create(tab[idx.GetElement(0)], tab[idx.GetElement(1)], tab[idx.GetElement(2)], tab[idx.GetElement(3)],
                            tab[idx.GetElement(4)], tab[idx.GetElement(5)], tab[idx.GetElement(6)], tab[idx.GetElement(7)]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> GatherB(byte[] tab, Vector256<int> idx)
        => Vector256.Create((int)tab[idx.GetElement(0)], tab[idx.GetElement(1)], tab[idx.GetElement(2)], tab[idx.GetElement(3)],
                            tab[idx.GetElement(4)], tab[idx.GetElement(5)], tab[idx.GetElement(6)], tab[idx.GetElement(7)]);

    // Transpose 8 block-contiguous blocks (px8[b*16 + i]) into pixel-major packed uints (tU[i*8 + b]).
    private static void Transpose8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<uint> tU)
    {
        ReadOnlySpan<uint> srcU = MemoryMarshal.Cast<ColorRgba, uint>(px8);
        for (int b = 0; b < 8; b++)
            for (int i = 0; i < 16; i++)
                tU[i * 8 + b] = srcU[b * 16 + i];
    }

    // per-lane block statistics from the setup pass.
    private struct Blk8
    {
        public Vector256<int> minR, minG, minB, maxR, maxG, maxB;
        public Vector256<int> totR, totG, totB, avgR, avgG, avgB;
        public Vector256<int> gray, solid, r0, g0, b0;
    }

    private static Blk8 Setup8(ref uint t0, Vector256<uint> mask8)
    {
        Blk8 s;
        s.r0 = Load8R(ref t0, 0, mask8, out s.g0, out s.b0);
        s.minR = s.r0; s.minG = s.g0; s.minB = s.b0; s.maxR = s.r0; s.maxG = s.g0; s.maxB = s.b0;
        s.totR = s.r0; s.totG = s.g0; s.totB = s.b0;
        s.gray = Vector256.Equals(s.r0, s.g0) & Vector256.Equals(s.g0, s.b0);
        s.solid = Vector256<int>.AllBitsSet;
        for (int i = 1; i < 16; i++)
        {
            Vector256<int> R = Load8R(ref t0, i, mask8, out var G, out var B);
            s.minR = Vector256.Min(s.minR, R); s.minG = Vector256.Min(s.minG, G); s.minB = Vector256.Min(s.minB, B);
            s.maxR = Vector256.Max(s.maxR, R); s.maxG = Vector256.Max(s.maxG, G); s.maxB = Vector256.Max(s.maxB, B);
            s.totR += R; s.totG += G; s.totB += B;
            s.gray &= Vector256.Equals(R, G) & Vector256.Equals(G, B);
            s.solid &= Vector256.Equals(R, s.r0) & Vector256.Equals(G, s.g0) & Vector256.Equals(B, s.b0);
        }
        s.avgR = Vector256.ShiftRightArithmetic(s.totR + Vector256.Create(8), 4);
        s.avgG = Vector256.ShiftRightArithmetic(s.totG + Vector256.Create(8), 4);
        s.avgB = Vector256.ShiftRightArithmetic(s.totB + Vector256.Create(8), 4);
        return s;
    }

    // PCA init from the setup stats (covariance -> power iteration -> extreme projected pixels), grayscale blend.
    private static void PickInitialPcaCore8(ref uint t0, Vector256<uint> mask8, in Blk8 s, int powerIters,
        out Vector256<int> lr, out Vector256<int> lg, out Vector256<int> lb,
        out Vector256<int> hr, out Vector256<int> hg, out Vector256<int> hb)
    {
        Vector256<int> c0 = Vector256<int>.Zero, c1 = c0, c2 = c0, c3 = c0, c4 = c0, c5 = c0;
        for (int i = 0; i < 16; i++)
        {
            Vector256<int> R = Load8R(ref t0, i, mask8, out var G, out var B);
            Vector256<int> r = R - s.avgR, g = G - s.avgG, b = B - s.avgB;
            c0 += r * r; c1 += r * g; c2 += r * b; c3 += g * g; c4 += g * b; c5 += b * b;
        }
        Vector256<float> xr = Vector256.ConvertToSingle(s.maxR - s.minR);
        Vector256<float> xg = Vector256.ConvertToSingle(s.maxG - s.minG);
        Vector256<float> xb = Vector256.ConvertToSingle(s.maxB - s.minB);
        xr = Vector256.ConditionalSelect(Vector256.LessThan(c2, Vector256<int>.Zero).AsSingle(), Neg8(xr), xr);
        xg = Vector256.ConditionalSelect(Vector256.LessThan(c4, Vector256<int>.Zero).AsSingle(), Neg8(xg), xg);
        Vector256<float> inv255 = Vector256.Create(1.0f / 255.0f);
        Vector256<float> fc0 = Vector256.ConvertToSingle(c0) * inv255, fc1 = Vector256.ConvertToSingle(c1) * inv255,
            fc2 = Vector256.ConvertToSingle(c2) * inv255, fc3 = Vector256.ConvertToSingle(c3) * inv255,
            fc4 = Vector256.ConvertToSingle(c4) * inv255, fc5 = Vector256.ConvertToSingle(c5) * inv255;
        for (int p = 0; p < powerIters; p++)
        {
            Vector256<float> nr = MulAdd(xb, fc2, MulAdd(xg, fc1, xr * fc0));
            Vector256<float> ng = MulAdd(xb, fc4, MulAdd(xg, fc3, xr * fc1));
            Vector256<float> nb = MulAdd(xb, fc5, MulAdd(xg, fc4, xr * fc2));
            xr = nr; xg = ng; xb = nb;
        }
        Vector256<float> k = Vector256.Max(Vector256.Max(Vector256.Abs(xr), Vector256.Abs(xg)), Vector256.Abs(xb));
        Vector256<int> kMask = Vector256.GreaterThanOrEqual(k, Vector256.Create(2.0f)).AsInt32();
        Vector256<float> m = Vector256.Create(2048.0f) / k;
        Vector256<int> saxisR = Vector256.ConditionalSelect(kMask, Vector256.ConvertToInt32(xr * m), Vector256.Create(306));
        Vector256<int> saxisG = Vector256.ConditionalSelect(kMask, Vector256.ConvertToInt32(xg * m), Vector256.Create(601));
        Vector256<int> saxisB = Vector256.ConditionalSelect(kMask, Vector256.ConvertToInt32(xb * m), Vector256.Create(117));
        saxisR *= Vector256.Create(16); saxisG *= Vector256.Create(16); saxisB *= Vector256.Create(16);

        Vector256<int> notF = Vector256.Create(~0xF);
        Vector256<int> vLow = Vector256.Create(int.MaxValue), vHigh = Vector256.Create(int.MinValue);
        Vector256<int> loR = s.r0, loG = s.g0, loB = s.b0, hiR = s.r0, hiG = s.g0, hiB = s.b0;
        for (int i = 0; i < 16; i++)
        {
            Vector256<int> R = Load8R(ref t0, i, mask8, out var G, out var B);
            Vector256<int> dot = ((R * saxisR + G * saxisG + B * saxisB) & notF) + Vector256.Create(i);
            Vector256<int> loM = Vector256.LessThan(dot, vLow);
            loR = Vector256.ConditionalSelect(loM, R, loR); loG = Vector256.ConditionalSelect(loM, G, loG); loB = Vector256.ConditionalSelect(loM, B, loB);
            vLow = Vector256.Min(vLow, dot);
            Vector256<int> hiM = Vector256.GreaterThan(dot, vHigh);
            hiR = Vector256.ConditionalSelect(hiM, R, hiR); hiG = Vector256.ConditionalSelect(hiM, G, hiG); hiB = Vector256.ConditionalSelect(hiM, B, hiB);
            vHigh = Vector256.Max(vHigh, dot);
        }
        Vector256<int> pcaLr = To5x8(loR), pcaLg = To6x8(loG), pcaLb = To5x8(loB);
        Vector256<int> pcaHr = To5x8(hiR), pcaHg = To6x8(hiG), pcaHb = To5x8(hiB);

        Vector256<int> subMask = Vector256.LessThan(s.maxR - s.minR, Vector256.Create(2));
        Vector256<int> sglR = To5x8(s.r0), sglG = To6x8(s.r0);
        Vector256<int> mmLoR = To5x8(s.minR), mmLoG = To6x8(s.minR), mmHiR = To5x8(s.maxR), mmHiG = To6x8(s.maxR);
        Vector256<int> gLr = Vector256.ConditionalSelect(subMask, sglR, mmLoR), gLg = Vector256.ConditionalSelect(subMask, sglG, mmLoG);
        Vector256<int> gHr = Vector256.ConditionalSelect(subMask, sglR, mmHiR), gHg = Vector256.ConditionalSelect(subMask, sglG, mmHiG);
        lr = Vector256.ConditionalSelect(s.gray, gLr, pcaLr); lb = Vector256.ConditionalSelect(s.gray, gLr, pcaLb); lg = Vector256.ConditionalSelect(s.gray, gLg, pcaLg);
        hr = Vector256.ConditionalSelect(s.gray, gHr, pcaHr); hb = Vector256.ConditionalSelect(s.gray, gHr, pcaHb); hg = Vector256.ConditionalSelect(s.gray, gHg, pcaHg);
    }

    // per-lane 4-color palette (ideal mode), bit-identical to GetBlockColors4.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetBlockColors4x8(Vector256<int> lr, Vector256<int> lg, Vector256<int> lb,
        Vector256<int> hr, Vector256<int> hg, Vector256<int> hb,
        out Vector256<int> br0, out Vector256<int> br1, out Vector256<int> br2, out Vector256<int> br3,
        out Vector256<int> bg0, out Vector256<int> bg1, out Vector256<int> bg2, out Vector256<int> bg3,
        out Vector256<int> bb0, out Vector256<int> bb1, out Vector256<int> bb2, out Vector256<int> bb3)
    {
        br0 = Scale5To8x8(lr); bg0 = Scale6To8x8(lg); bb0 = Scale5To8x8(lb);
        br3 = Scale5To8x8(hr); bg3 = Scale6To8x8(hg); bb3 = Scale5To8x8(hb);
        Vector256<int> two = Vector256.Create(2);
        br1 = Div3x8(br0 * two + br3); bg1 = Div3x8(bg0 * two + bg3); bb1 = Div3x8(bb0 * two + bb3);
        br2 = Div3x8(br3 * two + br0); bg2 = Div3x8(bg3 * two + bg0); bb2 = Div3x8(bb3 * two + bb0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Sel4(Vector256<int> sel, Vector256<int> v0, Vector256<int> v1, Vector256<int> v2, Vector256<int> v3)
        => Vector256.ConditionalSelect(Vector256.Equals(sel, Vector256<int>.Zero), v0,
           Vector256.ConditionalSelect(Vector256.Equals(sel, Vector256.Create(1)), v1,
           Vector256.ConditionalSelect(Vector256.Equals(sel, Vector256.Create(2)), v2, v3)));

    // find_sels (lane = block). No early-out: a committed trial never bails, so the full-sum error matches
    // scalar; a rejected trial's selectors are discarded by the caller regardless. Writes sels[i] per pixel.
    private static Vector256<int> FindSels8(ref uint t0, Vector256<uint> mask8, bool withErr,
        Vector256<int> lr, Vector256<int> lg, Vector256<int> lb, Vector256<int> hr, Vector256<int> hg, Vector256<int> hb,
        scoped Span<Vector256<int>> sels)
    {
        GetBlockColors4x8(lr, lg, lb, hr, hg, hb, out var br0, out var br1, out var br2, out var br3,
            out var bg0, out var bg1, out var bg2, out var bg3, out var bb0, out var bb1, out var bb2, out var bb3);
        Vector256<int> ar = br3 - br0, ag = bg3 - bg0, ab = bb3 - bb0;
        Vector256<int> d0p = br0 * ar + bg0 * ag + bb0 * ab, d1p = br1 * ar + bg1 * ag + bb1 * ab;
        Vector256<int> d2p = br2 * ar + bg2 * ag + bb2 * ab, d3p = br3 * ar + bg3 * ag + bb3 * ab;
        Vector256<int> t0c = d0p + d1p, t1c = d1p + d2p, t2c = d2p + d3p;
        Vector256<int> two = Vector256.Create(2); ar *= two; ag *= two; ab *= two;
        Vector256<int> three = Vector256.Create(3);
        Vector256<int> total = Vector256<int>.Zero;
        for (int i = 0; i < 16; i++)
        {
            Vector256<int> R = Load8R(ref t0, i, mask8, out var G, out var B);
            Vector256<int> d = R * ar + G * ag + B * ab;
            Vector256<int> sel = three + Vector256.LessThanOrEqual(d, t0c) + Vector256.LessThan(d, t1c) + Vector256.LessThan(d, t2c);
            sels[i] = sel;
            if (withErr)
            {
                Vector256<int> dr = R - Sel4(sel, br0, br1, br2, br3);
                Vector256<int> dg = G - Sel4(sel, bg0, bg1, bg2, bg3);
                Vector256<int> db = B - Sel4(sel, bb0, bb1, bb2, bb3);
                total += dr * dr + dg * dg + db * db;
            }
        }
        return total;
    }

    // 2x2 least-squares solve from the current selectors (lane = block). Returns float endpoints xl/xh and a
    // per-lane okMask (false where the determinant is degenerate -> caller uses the Match-table fallback).
    private static void ComputeLeastSquares8(ref uint t0, Vector256<uint> mask8, scoped ReadOnlySpan<Vector256<int>> sels,
        Vector256<int> totR, Vector256<int> totG, Vector256<int> totB,
        out Vector256<float> xlR, out Vector256<float> xlG, out Vector256<float> xlB,
        out Vector256<float> xhR, out Vector256<float> xhG, out Vector256<float> xhB, out Vector256<int> okMask)
    {
        Vector256<int> uqR = Vector256<int>.Zero, uqG = uqR, uqB = uqR, wacc = uqR;
        Vector256<int> w0 = Vector256.Create(0x000009), w1 = Vector256.Create(0x010204), w2 = Vector256.Create(0x040201), w3 = Vector256.Create(0x090000);
        for (int i = 0; i < 16; i++)
        {
            Vector256<int> R = Load8R(ref t0, i, mask8, out var G, out var B);
            Vector256<int> sel = sels[i];
            wacc += Sel4(sel, w0, w1, w2, w3);
            uqR += sel * R; uqG += sel * G; uqB += sel * B;
        }
        Vector256<int> q10R = totR * Vector256.Create(3) - uqR, q10G = totG * Vector256.Create(3) - uqG, q10B = totB * Vector256.Create(3) - uqB;
        Vector256<int> ff = Vector256.Create(0xFF);
        Vector256<float> z00 = Vector256.ConvertToSingle(Vector256.ShiftRightArithmetic(wacc, 16) & ff);
        Vector256<float> z10 = Vector256.ConvertToSingle(Vector256.ShiftRightArithmetic(wacc, 8) & ff);
        Vector256<float> z11 = Vector256.ConvertToSingle(wacc & ff);
        Vector256<float> det = z00 * z11 - z10 * z10;
        okMask = Vector256.GreaterThanOrEqual(Vector256.Abs(det), Vector256.Create(1e-8f)).AsInt32();
        Vector256<float> idet = Vector256.Create(3.0f / 255.0f) / det;
        Vector256<float> iz00 = z11 * idet, iz01 = Neg8(z10) * idet, iz10 = iz01, iz11 = z00 * idet;
        Vector256<float> fuqR = Vector256.ConvertToSingle(uqR), fq10R = Vector256.ConvertToSingle(q10R);
        Vector256<float> fuqG = Vector256.ConvertToSingle(uqG), fq10G = Vector256.ConvertToSingle(q10G);
        Vector256<float> fuqB = Vector256.ConvertToSingle(uqB), fq10B = Vector256.ConvertToSingle(q10B);
        xlR = MulAdd(iz00, fuqR, iz01 * fq10R); xhR = MulAdd(iz10, fuqR, iz11 * fq10R);
        xlG = MulAdd(iz00, fuqG, iz01 * fq10G); xhG = MulAdd(iz10, fuqG, iz11 * fq10G);
        xlB = MulAdd(iz00, fuqB, iz01 * fq10B); xhB = MulAdd(iz10, fuqB, iz11 * fq10B);
    }

    // float endpoint component -> 5/6-bit with clamp + midpoint rounding (Midpoint*[31]/[63] == 1e37 caps the top).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Round5x8(Vector256<float> a)
    {
        Vector256<int> lvl = Vector256.Max(Vector256.Min(Vector256.ConvertToInt32(a * Vector256.Create(31.0f)), Vector256.Create(31)), Vector256<int>.Zero);
        Vector256<float> mid = GatherF(Midpoint5, lvl);
        return (lvl - Vector256.GreaterThan(a, mid).AsInt32()) & Vector256.Create(31);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Round6x8(Vector256<float> a)
    {
        Vector256<int> lvl = Vector256.Max(Vector256.Min(Vector256.ConvertToInt32(a * Vector256.Create(63.0f)), Vector256.Create(63)), Vector256<int>.Zero);
        Vector256<float> mid = GatherF(Midpoint6, lvl);
        return (lvl - Vector256.GreaterThan(a, mid).AsInt32()) & Vector256.Create(63);
    }

    // MSE-keep-best descent (lane = block): fixed 2 passes with masked commit. A non-improving / unchanged
    // pass produces trialErr >= err so commitMask is false (matches the scalar early-break exactly).
    private static void RunDescent8(ref uint t0, Vector256<uint> mask8, in Blk8 s,
        ref Vector256<int> lr, ref Vector256<int> lg, ref Vector256<int> lb,
        ref Vector256<int> hr, ref Vector256<int> hg, ref Vector256<int> hb, scoped Span<Vector256<int>> sels)
    {
        Span<Vector256<int>> trialSels = stackalloc Vector256<int>[16];
        Vector256<int> err = FindSels8(ref t0, mask8, true, lr, lg, lb, hr, hg, hb, sels);
        for (int pass = 0; pass < 2; pass++)
        {
            ComputeLeastSquares8(ref t0, mask8, sels, s.totR, s.totG, s.totB,
                out var xlR, out var xlG, out var xlB, out var xhR, out var xhG, out var xhB, out var okMask);
            // solve path (note the high/low naming flip: xl -> trial-high, xh -> trial-low)
            Vector256<int> sThr = Round5x8(xlR), sThg = Round6x8(xlG), sThb = Round5x8(xlB);
            Vector256<int> sTlr = Round5x8(xhR), sTlg = Round6x8(xhG), sTlb = Round5x8(xhB);
            // degenerate fallback: single-color match tables indexed by the block average
            Vector256<int> fTlr = GatherB(Match5Hi, s.avgR), fTlg = GatherB(Match6Hi, s.avgG), fTlb = GatherB(Match5Hi, s.avgB);
            Vector256<int> fThr = GatherB(Match5Lo, s.avgR), fThg = GatherB(Match6Lo, s.avgG), fThb = GatherB(Match5Lo, s.avgB);
            Vector256<int> tlr = Vector256.ConditionalSelect(okMask, sTlr, fTlr), tlg = Vector256.ConditionalSelect(okMask, sTlg, fTlg), tlb = Vector256.ConditionalSelect(okMask, sTlb, fTlb);
            Vector256<int> thr = Vector256.ConditionalSelect(okMask, sThr, fThr), thg = Vector256.ConditionalSelect(okMask, sThg, fThg), thb = Vector256.ConditionalSelect(okMask, sThb, fThb);

            Vector256<int> trialErr = FindSels8(ref t0, mask8, true, tlr, tlg, tlb, thr, thg, thb, trialSels);
            Vector256<int> commit = Vector256.LessThan(trialErr, err);
            lr = Vector256.ConditionalSelect(commit, tlr, lr); lg = Vector256.ConditionalSelect(commit, tlg, lg); lb = Vector256.ConditionalSelect(commit, tlb, lb);
            hr = Vector256.ConditionalSelect(commit, thr, hr); hg = Vector256.ConditionalSelect(commit, thg, hg); hb = Vector256.ConditionalSelect(commit, thb, hb);
            err = Vector256.ConditionalSelect(commit, trialErr, err);
            for (int i = 0; i < 16; i++) sels[i] = Vector256.ConditionalSelect(commit, trialSels[i], sels[i]);
        }
    }

    // per-lane Encode4: 565 pack, color0>color1 swap + selector inversion, and the 16x2-bit selector pack.
    // Returns the two output words per lane: w0 = color0 | (color1 << 16), w1 = the packed selectors.
    private static void Encode4x8(Vector256<int> lr, Vector256<int> lg, Vector256<int> lb,
        Vector256<int> hr, Vector256<int> hg, Vector256<int> hb, scoped ReadOnlySpan<Vector256<int>> sels,
        out Vector256<int> w0, out Vector256<int> w1)
    {
        Vector256<int> lc = lr * Vector256.Create(2048) | lg * Vector256.Create(32) | lb;
        Vector256<int> hc = hr * Vector256.Create(2048) | hg * Vector256.Create(32) | hb;
        Vector256<int> zero = Vector256<int>.Zero, one = Vector256.Create(1), rep = Vector256.Create(0x01010101);

        // degenerate (lc == hc): make color0 > color1, force the all-same selector word
        Vector256<int> pos = Vector256.GreaterThan(hc, zero);
        Vector256<int> degC0 = Vector256.ConditionalSelect(pos, lc, one);
        Vector256<int> degC1 = Vector256.ConditionalSelect(pos, hc - one, zero);
        Vector256<int> degW1 = Vector256.ConditionalSelect(pos, zero, Vector256.Create(0x55)) * rep;

        // non-degenerate: swap so color0 > color1, invert selectors when swapped
        Vector256<int> lt = Vector256.LessThan(lc, hc);
        Vector256<int> ndC0 = Vector256.ConditionalSelect(lt, hc, lc);
        Vector256<int> ndC1 = Vector256.ConditionalSelect(lt, lc, hc);
        Vector256<int> invert = Vector256.ConditionalSelect(lt, Vector256.Create(0x55), zero) * rep;
        Vector256<int> t0v = zero, t2v = Vector256.Create(2), t3v = Vector256.Create(3);
        Vector256<int> packed = zero;
        for (int i = 0; i < 16; i++)
            packed |= Sel4(sels[i], t0v, t2v, t3v, one) * Vector256.Create(1 << (2 * i)); // trans = {0,2,3,1}
        Vector256<int> ndW1 = packed ^ invert;

        Vector256<int> eq = Vector256.Equals(lc, hc);
        Vector256<int> c0 = Vector256.ConditionalSelect(eq, degC0, ndC0);
        Vector256<int> c1 = Vector256.ConditionalSelect(eq, degC1, ndC1);
        w0 = c0 | (c1 * Vector256.Create(0x10000));
        w1 = Vector256.ConditionalSelect(eq, degW1, ndW1);
    }

    // per-lane EncodeSolid: optimal single-color (Match tables), color0 > color1, uniform selector word.
    private static void EncodeSolid8(Vector256<int> r0, Vector256<int> g0, Vector256<int> b0,
        out Vector256<int> w0, out Vector256<int> w1)
    {
        Vector256<int> p2048 = Vector256.Create(2048), p32 = Vector256.Create(32), zero = Vector256<int>.Zero, one = Vector256.Create(1), rep = Vector256.Create(0x01010101);
        Vector256<int> max = GatherB(Match5Hi, r0) * p2048 | GatherB(Match6Hi, g0) * p32 | GatherB(Match5Hi, b0);
        Vector256<int> min = GatherB(Match5Lo, r0) * p2048 | GatherB(Match6Lo, g0) * p32 | GatherB(Match5Lo, b0);
        Vector256<int> mask = Vector256.Create(0xAA);

        Vector256<int> eq = Vector256.Equals(min, max);
        Vector256<int> pos = Vector256.GreaterThan(min, zero);
        Vector256<int> eqMin = Vector256.ConditionalSelect(pos, min - one, zero);
        Vector256<int> eqMax = Vector256.ConditionalSelect(pos, max, one);
        Vector256<int> eqMask = Vector256.ConditionalSelect(pos, zero, Vector256.Create(0x55));
        min = Vector256.ConditionalSelect(eq, eqMin, min);
        max = Vector256.ConditionalSelect(eq, eqMax, max);
        mask = Vector256.ConditionalSelect(eq, eqMask, mask);

        Vector256<int> lt = Vector256.LessThan(max, min);
        Vector256<int> c0 = Vector256.ConditionalSelect(lt, min, max);
        Vector256<int> c1 = Vector256.ConditionalSelect(lt, max, min);
        mask = Vector256.ConditionalSelect(lt, mask ^ Vector256.Create(0x55), mask);
        w0 = c0 | (c1 * Vector256.Create(0x10000));
        w1 = mask * rep;
    }

    // Fast mode (lane = block): single committed LS pass, no error tracking (matches the scalar Fast path).
    private static void RunFast8(ref uint t0, Vector256<uint> mask8, in Blk8 s,
        out Vector256<int> lr, out Vector256<int> lg, out Vector256<int> lb,
        out Vector256<int> hr, out Vector256<int> hg, out Vector256<int> hb, scoped Span<Vector256<int>> sels)
    {
        PickInitialPcaCore8(ref t0, mask8, in s, 4, out lr, out lg, out lb, out hr, out hg, out hb);
        FindSels8(ref t0, mask8, false, lr, lg, lb, hr, hg, hb, sels);
        ComputeLeastSquares8(ref t0, mask8, sels, s.totR, s.totG, s.totB,
            out var xlR, out var xlG, out var xlB, out var xhR, out var xhG, out var xhB, out var okMask);
        Vector256<int> sThr = Round5x8(xlR), sThg = Round6x8(xlG), sThb = Round5x8(xlB);
        Vector256<int> sTlr = Round5x8(xhR), sTlg = Round6x8(xhG), sTlb = Round5x8(xhB);
        Vector256<int> fTlr = GatherB(Match5Hi, s.avgR), fTlg = GatherB(Match6Hi, s.avgG), fTlb = GatherB(Match5Hi, s.avgB);
        Vector256<int> fThr = GatherB(Match5Lo, s.avgR), fThg = GatherB(Match6Lo, s.avgG), fThb = GatherB(Match5Lo, s.avgB);
        Vector256<int> tlr = Vector256.ConditionalSelect(okMask, sTlr, fTlr), tlg = Vector256.ConditionalSelect(okMask, sTlg, fTlg), tlb = Vector256.ConditionalSelect(okMask, sTlb, fTlb);
        Vector256<int> thr = Vector256.ConditionalSelect(okMask, sThr, fThr), thg = Vector256.ConditionalSelect(okMask, sThg, fThg), thb = Vector256.ConditionalSelect(okMask, sThb, fThb);

        Vector256<int> allEq = Vector256.Equals(lr, tlr) & Vector256.Equals(lg, tlg) & Vector256.Equals(lb, tlb)
            & Vector256.Equals(hr, thr) & Vector256.Equals(hg, thg) & Vector256.Equals(hb, thb);
        Vector256<int> changed = allEq ^ Vector256<int>.AllBitsSet;
        Span<Vector256<int>> sels2 = stackalloc Vector256<int>[16];
        FindSels8(ref t0, mask8, false, tlr, tlg, tlb, thr, thg, thb, sels2);
        lr = Vector256.ConditionalSelect(changed, tlr, lr); lg = Vector256.ConditionalSelect(changed, tlg, lg); lb = Vector256.ConditionalSelect(changed, tlb, lb);
        hr = Vector256.ConditionalSelect(changed, thr, hr); hg = Vector256.ConditionalSelect(changed, thg, hg); hb = Vector256.ConditionalSelect(changed, thb, hb);
        for (int i = 0; i < 16; i++) sels[i] = Vector256.ConditionalSelect(changed, sels2[i], sels[i]);
    }

    // Encode the BC1 color half of 8 block-contiguous blocks into 8 lane words (w0 = color0|color1<<16, w1 =
    // packed selectors), bit-identical to 8x EncodeBlock. Caller scatters; see EncodeColorStrided8.
    private static void EncodeColor8(scoped ReadOnlySpan<ColorRgba> px8, Bc1Quality quality,
        out Vector256<int> w0, out Vector256<int> w1)
    {
        Span<uint> tU = stackalloc uint[128];
        Transpose8(px8, tU);
        ref uint t0 = ref MemoryMarshal.GetReference(tU);
        Vector256<uint> mask8 = Vector256.Create(0xFFu);
        Blk8 s = Setup8(ref t0, mask8);

        Span<Vector256<int>> sels = stackalloc Vector256<int>[16];
        Vector256<int> lr, lg, lb, hr, hg, hb;
        if (quality == Bc1Quality.Fast)
            RunFast8(ref t0, mask8, in s, out lr, out lg, out lb, out hr, out hg, out hb, sels);
        else
        {
            PickInitialPcaCore8(ref t0, mask8, in s, quality == Bc1Quality.HighQuality ? 6 : 4, out lr, out lg, out lb, out hr, out hg, out hb);
            RunDescent8(ref t0, mask8, in s, ref lr, ref lg, ref lb, ref hr, ref hg, ref hb, sels);
        }

        Encode4x8(lr, lg, lb, hr, hg, hb, sels, out w0, out w1);
        EncodeSolid8(s.r0, s.g0, s.b0, out var sw0, out var sw1);
        w0 = Vector256.ConditionalSelect(s.solid, sw0, w0);
        w1 = Vector256.ConditionalSelect(s.solid, sw1, w1);
    }

    // Scatter the 8 color words into a (possibly strided) output: BC1 = 8-byte blocks at offset 0; BC3/BC2 =
    // 16-byte blocks with the color half at offset 8. blockBytes and colorOffset must be 4-byte aligned.
    internal static void EncodeColorStrided8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> output,
        int blockBytes, int colorOffset, Bc1Quality quality)
    {
        EncodeColor8(px8, quality, out var w0, out var w1);
        ref uint outU = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, uint>(output));
        int u = blockBytes / 4, co = colorOffset / 4;
        for (int b = 0; b < 8; b++)
        {
            Unsafe.Add(ref outU, (nuint)(b * u + co)) = (uint)w0.GetElement(b);
            Unsafe.Add(ref outU, (nuint)(b * u + co + 1)) = (uint)w1.GetElement(b);
        }
    }

    // Encode 8 block-contiguous 4x4 blocks (128 texels) to 8 packed BC1 blocks (64 bytes). V256-only.
    internal static void EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8, Bc1Quality quality)
        => EncodeColorStrided8(px8, out8, 8, 0, quality);

    // ---- test entry points (validate each stage against the scalar per-block path) ----
    internal static void PickInitialPca8(scoped ReadOnlySpan<ColorRgba> px8, int powerIters,
        out Vector256<int> lr, out Vector256<int> lg, out Vector256<int> lb,
        out Vector256<int> hr, out Vector256<int> hg, out Vector256<int> hb)
    {
        Span<uint> tU = stackalloc uint[128];
        Transpose8(px8, tU);
        ref uint t0 = ref MemoryMarshal.GetReference(tU);
        Vector256<uint> mask8 = Vector256.Create(0xFFu);
        Blk8 s = Setup8(ref t0, mask8);
        PickInitialPcaCore8(ref t0, mask8, in s, powerIters, out lr, out lg, out lb, out hr, out hg, out hb);
    }
}
#endif
