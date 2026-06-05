// Bc1Block8.cs — BC1 multi-block ("lane = block") encoder path (namespace Bcn.Ldr).
//
// Up to Vector<int>.Count independent 4x4 blocks are encoded in parallel, one per SIMD lane, at the
// hardware's native width (4 on NEON / SSE, 8 on AVX2, 16 on AVX-512). Each per-block scalar step
// (power iteration, 2x2 solve, 565 rounding) becomes a single SIMD op across the lanes; each 16-pixel
// loop becomes 16 vector iterations that accumulate along the pixel dimension, one lane per block.
//
// Because SIMD float/int ops are per-lane IEEE-identical to the scalar path (no cross-lane reduction
// reorder; FMA is avoided to keep the two-rounding form) and every control-flow divergence is turned into
// a masked blend, the result is bit-identical to the per-block encoder, stage by stage — and identical
// across lane widths, since a lane's bytes never depend on the lane count. Gated on
// Vector.IsHardwareAccelerated; the driver falls back to the per-block path when SIMD is unavailable.

namespace Bcn.Ldr;

using System;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;

static partial class Bc1Block
{
    // ---- vectorized scalar-helper equivalents (bit-identical to the per-block scalar forms) ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> To5x8(Vector<int> v)
    {
        var t = v * new Vector<int>(31) + new Vector<int>(128);
        return Vector.ShiftRightArithmetic(t + Vector.ShiftRightArithmetic(t, 8), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> To6x8(Vector<int> v)
    {
        var t = v * new Vector<int>(63) + new Vector<int>(128);
        return Vector.ShiftRightArithmetic(t + Vector.ShiftRightArithmetic(t, 8), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Scale5To8x8(Vector<int> v) => v * new Vector<int>(8) | Vector.ShiftRightArithmetic(v, 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Scale6To8x8(Vector<int> v) => v * new Vector<int>(4) | Vector.ShiftRightArithmetic(v, 4);

    // floor(x/3) for 0 <= x <= 765 via the standard round-up reciprocal (exact in that range).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Div3x8(Vector<int> x) => Vector.ShiftRightArithmetic(x * new Vector<int>(43691), 17);

    // exact IEEE negate (sign-bit flip), matching scalar -x including on +/-0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Neg8(Vector<float> x) => Vector.AsVectorSingle(Vector.AsVectorInt32(x) ^ new Vector<int>(unchecked((int)0x80000000)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> MulAdd(Vector<float> a, Vector<float> b, Vector<float> c)
#if NET9_0_OR_GREATER
        => Vector.MultiplyAddEstimate(a, b, c);
#else
        => a * b + c;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Split8(Vector<uint> u, Vector<uint> mask8, out Vector<int> r, out Vector<int> g, out Vector<int> b)
    {
        r = Vector.AsVectorInt32(u & mask8);
        g = Vector.AsVectorInt32(Vector.ShiftRightLogical(u, 8) & mask8);
        b = Vector.AsVectorInt32(Vector.ShiftRightLogical(u, 16) & mask8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Load8R(scoped ReadOnlySpan<uint> tU, int i, Vector<uint> mask8, out Vector<int> g, out Vector<int> b)
    {
        Split8(new Vector<uint>(tU.Slice(i * Vector<int>.Count)), mask8, out var r, out g, out b);
        return r;
    }

    // per-lane gather from a small float / byte table (scalar gather; indices are in range by construction).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> GatherF(float[] tab, Vector<int> idx)
    {
        Span<float> tmp = stackalloc float[Vector<int>.Count];
        for (var l = 0; l < Vector<int>.Count; l++) tmp[l] = tab[idx[l]];
        return new Vector<float>(tmp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> GatherB(byte[] tab, Vector<int> idx)
    {
        Span<int> tmp = stackalloc int[Vector<int>.Count];
        for (var l = 0; l < Vector<int>.Count; l++) tmp[l] = tab[idx[l]];
        return new Vector<int>(tmp);
    }

    // Transpose Vector<int>.Count block-contiguous blocks (px8[b*16 + i]) into pixel-major packed uints (tU[i*n + b]).
    static void Transpose8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<uint> tU)
    {
        int n = Vector<int>.Count;
        var srcU = MemoryMarshal.Cast<ColorRgba, uint>(px8);
        for (var b = 0; b < n; b++)
        for (var i = 0; i < 16; i++)
            tU[i * n + b] = srcU[b * 16 + i];
    }

    // per-lane block statistics from the setup pass.
    struct Blk8
    {
        public Vector<int> minR, minG, minB, maxR, maxG, maxB;
        public Vector<int> totR, totG, totB, avgR, avgG, avgB;
        public Vector<int> gray, solid, r0, g0, b0;
    }

    static Blk8 Setup8(scoped ReadOnlySpan<uint> tU, Vector<uint> mask8)
    {
        Blk8 s;
        s.r0 = Load8R(tU, 0, mask8, out s.g0, out s.b0);
        s.minR = s.r0;
        s.minG = s.g0;
        s.minB = s.b0;
        s.maxR = s.r0;
        s.maxG = s.g0;
        s.maxB = s.b0;
        s.totR = s.r0;
        s.totG = s.g0;
        s.totB = s.b0;
        s.gray = Vector.Equals(s.r0, s.g0) & Vector.Equals(s.g0, s.b0);
        s.solid = new Vector<int>(-1);
        for (var i = 1; i < 16; i++)
        {
            var R = Load8R(tU, i, mask8, out var G, out var B);
            s.minR = Vector.Min(s.minR, R);
            s.minG = Vector.Min(s.minG, G);
            s.minB = Vector.Min(s.minB, B);
            s.maxR = Vector.Max(s.maxR, R);
            s.maxG = Vector.Max(s.maxG, G);
            s.maxB = Vector.Max(s.maxB, B);
            s.totR += R;
            s.totG += G;
            s.totB += B;
            s.gray &= Vector.Equals(R, G) & Vector.Equals(G, B);
            s.solid &= Vector.Equals(R, s.r0) & Vector.Equals(G, s.g0) & Vector.Equals(B, s.b0);
        }

        s.avgR = Vector.ShiftRightArithmetic(s.totR + new Vector<int>(8), 4);
        s.avgG = Vector.ShiftRightArithmetic(s.totG + new Vector<int>(8), 4);
        s.avgB = Vector.ShiftRightArithmetic(s.totB + new Vector<int>(8), 4);
        return s;
    }

    // PCA init from the setup stats (covariance -> power iteration -> extreme projected pixels), grayscale blend.
    static void PickInitialPcaCore8(scoped ReadOnlySpan<uint> tU,
        Vector<uint> mask8,
        in Blk8 s,
        int powerIters,
        out Vector<int> lr,
        out Vector<int> lg,
        out Vector<int> lb,
        out Vector<int> hr,
        out Vector<int> hg,
        out Vector<int> hb)
    {
        Vector<int> c0 = Vector<int>.Zero, c1 = c0, c2 = c0, c3 = c0, c4 = c0, c5 = c0;
        for (var i = 0; i < 16; i++)
        {
            var R = Load8R(tU, i, mask8, out var G, out var B);
            Vector<int> r = R - s.avgR, g = G - s.avgG, b = B - s.avgB;
            c0 += r * r;
            c1 += r * g;
            c2 += r * b;
            c3 += g * g;
            c4 += g * b;
            c5 += b * b;
        }

        var xr = Vector.ConvertToSingle(s.maxR - s.minR);
        var xg = Vector.ConvertToSingle(s.maxG - s.minG);
        var xb = Vector.ConvertToSingle(s.maxB - s.minB);
        xr = Vector.ConditionalSelect(Vector.AsVectorSingle(Vector.LessThan(c2, Vector<int>.Zero)), Neg8(xr), xr);
        xg = Vector.ConditionalSelect(Vector.AsVectorSingle(Vector.LessThan(c4, Vector<int>.Zero)), Neg8(xg), xg);
        var inv255 = new Vector<float>(1.0f / 255.0f);
        Vector<float> fc0 = Vector.ConvertToSingle(c0) * inv255, fc1 = Vector.ConvertToSingle(c1) * inv255,
            fc2 = Vector.ConvertToSingle(c2) * inv255, fc3 = Vector.ConvertToSingle(c3) * inv255,
            fc4 = Vector.ConvertToSingle(c4) * inv255, fc5 = Vector.ConvertToSingle(c5) * inv255;

        for (var p = 0; p < powerIters; p++)
        {
            var nr = MulAdd(xb, fc2, MulAdd(xg, fc1, xr * fc0));
            var ng = MulAdd(xb, fc4, MulAdd(xg, fc3, xr * fc1));
            var nb = MulAdd(xb, fc5, MulAdd(xg, fc4, xr * fc2));
            xr = nr;
            xg = ng;
            xb = nb;
        }

        var k = Vector.Max(Vector.Max(Vector.Abs(xr), Vector.Abs(xg)), Vector.Abs(xb));
        var kMask = Vector.AsVectorInt32(Vector.GreaterThanOrEqual(k, new Vector<float>(2.0f)));
        var m = new Vector<float>(2048.0f) / k;
        var saxisR = Vector.ConditionalSelect(kMask, Vector.ConvertToInt32(xr * m), new Vector<int>(306));
        var saxisG = Vector.ConditionalSelect(kMask, Vector.ConvertToInt32(xg * m), new Vector<int>(601));
        var saxisB = Vector.ConditionalSelect(kMask, Vector.ConvertToInt32(xb * m), new Vector<int>(117));
        saxisR *= new Vector<int>(16);
        saxisG *= new Vector<int>(16);
        saxisB *= new Vector<int>(16);

        var notF = new Vector<int>(~0xF);
        Vector<int> vLow = new Vector<int>(int.MaxValue), vHigh = new Vector<int>(int.MinValue);
        Vector<int> loR = s.r0, loG = s.g0, loB = s.b0, hiR = s.r0, hiG = s.g0, hiB = s.b0;
        for (var i = 0; i < 16; i++)
        {
            var R = Load8R(tU, i, mask8, out var G, out var B);
            var dot = (R * saxisR + G * saxisG + B * saxisB & notF) + new Vector<int>(i);
            var loM = Vector.LessThan(dot, vLow);
            loR = Vector.ConditionalSelect(loM, R, loR);
            loG = Vector.ConditionalSelect(loM, G, loG);
            loB = Vector.ConditionalSelect(loM, B, loB);
            vLow = Vector.Min(vLow, dot);
            var hiM = Vector.GreaterThan(dot, vHigh);
            hiR = Vector.ConditionalSelect(hiM, R, hiR);
            hiG = Vector.ConditionalSelect(hiM, G, hiG);
            hiB = Vector.ConditionalSelect(hiM, B, hiB);
            vHigh = Vector.Max(vHigh, dot);
        }

        Vector<int> pcaLr = To5x8(loR), pcaLg = To6x8(loG), pcaLb = To5x8(loB);
        Vector<int> pcaHr = To5x8(hiR), pcaHg = To6x8(hiG), pcaHb = To5x8(hiB);

        var subMask = Vector.LessThan(s.maxR - s.minR, new Vector<int>(2));
        Vector<int> sglR = To5x8(s.r0), sglG = To6x8(s.r0);
        Vector<int> mmLoR = To5x8(s.minR), mmLoG = To6x8(s.minR), mmHiR = To5x8(s.maxR), mmHiG = To6x8(s.maxR);
        Vector<int> gLr = Vector.ConditionalSelect(subMask, sglR, mmLoR), gLg = Vector.ConditionalSelect(subMask, sglG, mmLoG);
        Vector<int> gHr = Vector.ConditionalSelect(subMask, sglR, mmHiR), gHg = Vector.ConditionalSelect(subMask, sglG, mmHiG);
        lr = Vector.ConditionalSelect(s.gray, gLr, pcaLr);
        lb = Vector.ConditionalSelect(s.gray, gLr, pcaLb);
        lg = Vector.ConditionalSelect(s.gray, gLg, pcaLg);
        hr = Vector.ConditionalSelect(s.gray, gHr, pcaHr);
        hb = Vector.ConditionalSelect(s.gray, gHr, pcaHb);
        hg = Vector.ConditionalSelect(s.gray, gHg, pcaHg);
    }

    // per-lane 4-color palette (ideal mode), bit-identical to GetBlockColors4.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void GetBlockColors4x8(Vector<int> lr,
        Vector<int> lg,
        Vector<int> lb,
        Vector<int> hr,
        Vector<int> hg,
        Vector<int> hb,
        out Vector<int> br0,
        out Vector<int> br1,
        out Vector<int> br2,
        out Vector<int> br3,
        out Vector<int> bg0,
        out Vector<int> bg1,
        out Vector<int> bg2,
        out Vector<int> bg3,
        out Vector<int> bb0,
        out Vector<int> bb1,
        out Vector<int> bb2,
        out Vector<int> bb3)
    {
        br0 = Scale5To8x8(lr);
        bg0 = Scale6To8x8(lg);
        bb0 = Scale5To8x8(lb);
        br3 = Scale5To8x8(hr);
        bg3 = Scale6To8x8(hg);
        bb3 = Scale5To8x8(hb);
        var two = new Vector<int>(2);
        br1 = Div3x8(br0 * two + br3);
        bg1 = Div3x8(bg0 * two + bg3);
        bb1 = Div3x8(bb0 * two + bb3);
        br2 = Div3x8(br3 * two + br0);
        bg2 = Div3x8(bg3 * two + bg0);
        bb2 = Div3x8(bb3 * two + bb0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Sel4(Vector<int> sel, Vector<int> v0, Vector<int> v1, Vector<int> v2, Vector<int> v3)
        => Vector.ConditionalSelect(Vector.Equals(sel, Vector<int>.Zero),
            v0,
            Vector.ConditionalSelect(Vector.Equals(sel, new Vector<int>(1)),
                v1,
                Vector.ConditionalSelect(Vector.Equals(sel, new Vector<int>(2)), v2, v3)));

    // find_sels (lane = block). No early-out: a committed trial never bails, so the full-sum error matches
    // scalar; a rejected trial's selectors are discarded by the caller regardless. Writes sels[i] per pixel.
    static Vector<int> FindSels8(scoped ReadOnlySpan<uint> tU,
        Vector<uint> mask8,
        bool withErr,
        Vector<int> lr,
        Vector<int> lg,
        Vector<int> lb,
        Vector<int> hr,
        Vector<int> hg,
        Vector<int> hb,
        scoped Span<Vector<int>> sels)
    {
        GetBlockColors4x8(lr,
            lg,
            lb,
            hr,
            hg,
            hb,
            out var br0,
            out var br1,
            out var br2,
            out var br3,
            out var bg0,
            out var bg1,
            out var bg2,
            out var bg3,
            out var bb0,
            out var bb1,
            out var bb2,
            out var bb3);

        Vector<int> ar = br3 - br0, ag = bg3 - bg0, ab = bb3 - bb0;
        Vector<int> d0p = br0 * ar + bg0 * ag + bb0 * ab, d1p = br1 * ar + bg1 * ag + bb1 * ab;
        Vector<int> d2p = br2 * ar + bg2 * ag + bb2 * ab, d3p = br3 * ar + bg3 * ag + bb3 * ab;
        Vector<int> t0c = d0p + d1p, t1c = d1p + d2p, t2c = d2p + d3p;
        var two = new Vector<int>(2);
        ar *= two;
        ag *= two;
        ab *= two;
        var three = new Vector<int>(3);
        var total = Vector<int>.Zero;
        for (var i = 0; i < 16; i++)
        {
            var R = Load8R(tU, i, mask8, out var G, out var B);
            var d = R * ar + G * ag + B * ab;
            var sel = three + Vector.LessThanOrEqual(d, t0c) + Vector.LessThan(d, t1c) + Vector.LessThan(d, t2c);
            sels[i] = sel;
            if (withErr)
            {
                var dr = R - Sel4(sel, br0, br1, br2, br3);
                var dg = G - Sel4(sel, bg0, bg1, bg2, bg3);
                var db = B - Sel4(sel, bb0, bb1, bb2, bb3);
                total += dr * dr + dg * dg + db * db;
            }
        }

        return total;
    }

    // 2x2 least-squares solve from the current selectors (lane = block). Returns float endpoints xl/xh and a
    // per-lane okMask (false where the determinant is degenerate -> caller uses the Match-table fallback).
    static void ComputeLeastSquares8(scoped ReadOnlySpan<uint> tU,
        Vector<uint> mask8,
        scoped ReadOnlySpan<Vector<int>> sels,
        Vector<int> totR,
        Vector<int> totG,
        Vector<int> totB,
        out Vector<float> xlR,
        out Vector<float> xlG,
        out Vector<float> xlB,
        out Vector<float> xhR,
        out Vector<float> xhG,
        out Vector<float> xhB,
        out Vector<int> okMask)
    {
        Vector<int> uqR = Vector<int>.Zero, uqG = uqR, uqB = uqR, wacc = uqR;
        Vector<int> w0 = new Vector<int>(0x000009), w1 = new Vector<int>(0x010204), w2 = new Vector<int>(0x040201), w3 = new Vector<int>(0x090000);
        for (var i = 0; i < 16; i++)
        {
            var R = Load8R(tU, i, mask8, out var G, out var B);
            var sel = sels[i];
            wacc += Sel4(sel, w0, w1, w2, w3);
            uqR += sel * R;
            uqG += sel * G;
            uqB += sel * B;
        }

        Vector<int> q10R = totR * new Vector<int>(3) - uqR, q10G = totG * new Vector<int>(3) - uqG, q10B = totB * new Vector<int>(3) - uqB;
        var ff = new Vector<int>(0xFF);
        var z00 = Vector.ConvertToSingle(Vector.ShiftRightArithmetic(wacc, 16) & ff);
        var z10 = Vector.ConvertToSingle(Vector.ShiftRightArithmetic(wacc, 8) & ff);
        var z11 = Vector.ConvertToSingle(wacc & ff);
        var det = z00 * z11 - z10 * z10;
        okMask = Vector.AsVectorInt32(Vector.GreaterThanOrEqual(Vector.Abs(det), new Vector<float>(1e-8f)));
        var idet = new Vector<float>(3.0f / 255.0f) / det;
        Vector<float> iz00 = z11 * idet, iz01 = Neg8(z10) * idet, iz10 = iz01, iz11 = z00 * idet;
        Vector<float> fuqR = Vector.ConvertToSingle(uqR), fq10R = Vector.ConvertToSingle(q10R);
        Vector<float> fuqG = Vector.ConvertToSingle(uqG), fq10G = Vector.ConvertToSingle(q10G);
        Vector<float> fuqB = Vector.ConvertToSingle(uqB), fq10B = Vector.ConvertToSingle(q10B);
        xlR = MulAdd(iz00, fuqR, iz01 * fq10R);
        xhR = MulAdd(iz10, fuqR, iz11 * fq10R);
        xlG = MulAdd(iz00, fuqG, iz01 * fq10G);
        xhG = MulAdd(iz10, fuqG, iz11 * fq10G);
        xlB = MulAdd(iz00, fuqB, iz01 * fq10B);
        xhB = MulAdd(iz10, fuqB, iz11 * fq10B);
    }

    // float endpoint component -> 5/6-bit with clamp + midpoint rounding (Midpoint*[31]/[63] == 1e37 caps the top).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Round5x8(Vector<float> a)
    {
        var lvl = Vector.Max(Vector.Min(Vector.ConvertToInt32(a * new Vector<float>(31.0f)), new Vector<int>(31)), Vector<int>.Zero);
        var mid = GatherF(Midpoint5, lvl);
        return lvl - Vector.AsVectorInt32(Vector.GreaterThan(a, mid)) & new Vector<int>(31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<int> Round6x8(Vector<float> a)
    {
        var lvl = Vector.Max(Vector.Min(Vector.ConvertToInt32(a * new Vector<float>(63.0f)), new Vector<int>(63)), Vector<int>.Zero);
        var mid = GatherF(Midpoint6, lvl);
        return lvl - Vector.AsVectorInt32(Vector.GreaterThan(a, mid)) & new Vector<int>(63);
    }

    // MSE-keep-best descent (lane = block): fixed 2 passes with masked commit. A non-improving / unchanged
    // pass produces trialErr >= err so commitMask is false (matches the scalar early-break exactly).
    static void RunDescent8(scoped ReadOnlySpan<uint> tU,
        Vector<uint> mask8,
        in Blk8 s,
        ref Vector<int> lr,
        ref Vector<int> lg,
        ref Vector<int> lb,
        ref Vector<int> hr,
        ref Vector<int> hg,
        ref Vector<int> hb,
        scoped Span<Vector<int>> sels)
    {
        Span<Vector<int>> trialSels = MemoryMarshal.Cast<byte, Vector<int>>(stackalloc byte[16 * Vector<byte>.Count]);
        var err = FindSels8(tU, mask8, true, lr, lg, lb, hr, hg, hb, sels);
        for (var pass = 0; pass < 2; pass++)
        {
            ComputeLeastSquares8(tU,
                mask8,
                sels,
                s.totR,
                s.totG,
                s.totB,
                out var xlR,
                out var xlG,
                out var xlB,
                out var xhR,
                out var xhG,
                out var xhB,
                out var okMask);

            // solve path (note the high/low naming flip: xl -> trial-high, xh -> trial-low)
            Vector<int> sThr = Round5x8(xlR), sThg = Round6x8(xlG), sThb = Round5x8(xlB);
            Vector<int> sTlr = Round5x8(xhR), sTlg = Round6x8(xhG), sTlb = Round5x8(xhB);
            // degenerate fallback: single-color match tables indexed by the block average
            Vector<int> fTlr = GatherB(Match5Hi, s.avgR), fTlg = GatherB(Match6Hi, s.avgG), fTlb = GatherB(Match5Hi, s.avgB);
            Vector<int> fThr = GatherB(Match5Lo, s.avgR), fThg = GatherB(Match6Lo, s.avgG), fThb = GatherB(Match5Lo, s.avgB);
            Vector<int> tlr = Vector.ConditionalSelect(okMask, sTlr, fTlr), tlg = Vector.ConditionalSelect(okMask, sTlg, fTlg), tlb = Vector.ConditionalSelect(okMask, sTlb, fTlb);
            Vector<int> thr = Vector.ConditionalSelect(okMask, sThr, fThr), thg = Vector.ConditionalSelect(okMask, sThg, fThg), thb = Vector.ConditionalSelect(okMask, sThb, fThb);

            var trialErr = FindSels8(tU, mask8, true, tlr, tlg, tlb, thr, thg, thb, trialSels);
            var commit = Vector.LessThan(trialErr, err);
            lr = Vector.ConditionalSelect(commit, tlr, lr);
            lg = Vector.ConditionalSelect(commit, tlg, lg);
            lb = Vector.ConditionalSelect(commit, tlb, lb);
            hr = Vector.ConditionalSelect(commit, thr, hr);
            hg = Vector.ConditionalSelect(commit, thg, hg);
            hb = Vector.ConditionalSelect(commit, thb, hb);
            err = Vector.ConditionalSelect(commit, trialErr, err);
            for (var i = 0; i < 16; i++) sels[i] = Vector.ConditionalSelect(commit, trialSels[i], sels[i]);
        }
    }

    // per-lane Encode4: 565 pack, color0>color1 swap + selector inversion, and the 16x2-bit selector pack.
    // Returns the two output words per lane: w0 = color0 | (color1 << 16), w1 = the packed selectors.
    static void Encode4x8(Vector<int> lr,
        Vector<int> lg,
        Vector<int> lb,
        Vector<int> hr,
        Vector<int> hg,
        Vector<int> hb,
        scoped ReadOnlySpan<Vector<int>> sels,
        out Vector<int> w0,
        out Vector<int> w1)
    {
        var lc = lr * new Vector<int>(2048) | lg * new Vector<int>(32) | lb;
        var hc = hr * new Vector<int>(2048) | hg * new Vector<int>(32) | hb;
        Vector<int> zero = Vector<int>.Zero, one = new Vector<int>(1), rep = new Vector<int>(0x01010101);

        // degenerate (lc == hc): make color0 > color1, force the all-same selector word
        var pos = Vector.GreaterThan(hc, zero);
        var degC0 = Vector.ConditionalSelect(pos, lc, one);
        var degC1 = Vector.ConditionalSelect(pos, hc - one, zero);
        var degW1 = Vector.ConditionalSelect(pos, zero, new Vector<int>(0x55)) * rep;

        // non-degenerate: swap so color0 > color1, invert selectors when swapped
        var lt = Vector.LessThan(lc, hc);
        var ndC0 = Vector.ConditionalSelect(lt, hc, lc);
        var ndC1 = Vector.ConditionalSelect(lt, lc, hc);
        var invert = Vector.ConditionalSelect(lt, new Vector<int>(0x55), zero) * rep;
        Vector<int> t0v = zero, t2v = new Vector<int>(2), t3v = new Vector<int>(3);
        var packed = zero;
        for (var i = 0; i < 16; i++)
            packed |= Sel4(sels[i], t0v, t2v, t3v, one) * new Vector<int>(1 << 2 * i); // trans = {0,2,3,1}

        var ndW1 = packed ^ invert;

        var eq = Vector.Equals(lc, hc);
        var c0 = Vector.ConditionalSelect(eq, degC0, ndC0);
        var c1 = Vector.ConditionalSelect(eq, degC1, ndC1);
        w0 = c0 | c1 * new Vector<int>(0x10000);
        w1 = Vector.ConditionalSelect(eq, degW1, ndW1);
    }

    // per-lane EncodeSolid: optimal single-color (Match tables), color0 > color1, uniform selector word.
    static void EncodeSolid8(Vector<int> r0,
        Vector<int> g0,
        Vector<int> b0,
        out Vector<int> w0,
        out Vector<int> w1)
    {
        Vector<int> p2048 = new Vector<int>(2048), p32 = new Vector<int>(32), zero = Vector<int>.Zero, one = new Vector<int>(1), rep = new Vector<int>(0x01010101);
        var max = GatherB(Match5Hi, r0) * p2048 | GatherB(Match6Hi, g0) * p32 | GatherB(Match5Hi, b0);
        var min = GatherB(Match5Lo, r0) * p2048 | GatherB(Match6Lo, g0) * p32 | GatherB(Match5Lo, b0);
        var mask = new Vector<int>(0xAA);

        var eq = Vector.Equals(min, max);
        var pos = Vector.GreaterThan(min, zero);
        var eqMin = Vector.ConditionalSelect(pos, min - one, zero);
        var eqMax = Vector.ConditionalSelect(pos, max, one);
        var eqMask = Vector.ConditionalSelect(pos, zero, new Vector<int>(0x55));
        min = Vector.ConditionalSelect(eq, eqMin, min);
        max = Vector.ConditionalSelect(eq, eqMax, max);
        mask = Vector.ConditionalSelect(eq, eqMask, mask);

        var lt = Vector.LessThan(max, min);
        var c0 = Vector.ConditionalSelect(lt, min, max);
        var c1 = Vector.ConditionalSelect(lt, max, min);
        mask = Vector.ConditionalSelect(lt, mask ^ new Vector<int>(0x55), mask);
        w0 = c0 | c1 * new Vector<int>(0x10000);
        w1 = mask * rep;
    }

    // Fast mode (lane = block): single committed LS pass, no error tracking (matches the scalar Fast path).
    static void RunFast8(scoped ReadOnlySpan<uint> tU,
        Vector<uint> mask8,
        in Blk8 s,
        out Vector<int> lr,
        out Vector<int> lg,
        out Vector<int> lb,
        out Vector<int> hr,
        out Vector<int> hg,
        out Vector<int> hb,
        scoped Span<Vector<int>> sels)
    {
        PickInitialPcaCore8(tU, mask8, in s, 4, out lr, out lg, out lb, out hr, out hg, out hb);
        FindSels8(tU, mask8, false, lr, lg, lb, hr, hg, hb, sels);
        ComputeLeastSquares8(tU,
            mask8,
            sels,
            s.totR,
            s.totG,
            s.totB,
            out var xlR,
            out var xlG,
            out var xlB,
            out var xhR,
            out var xhG,
            out var xhB,
            out var okMask);

        Vector<int> sThr = Round5x8(xlR), sThg = Round6x8(xlG), sThb = Round5x8(xlB);
        Vector<int> sTlr = Round5x8(xhR), sTlg = Round6x8(xhG), sTlb = Round5x8(xhB);
        Vector<int> fTlr = GatherB(Match5Hi, s.avgR), fTlg = GatherB(Match6Hi, s.avgG), fTlb = GatherB(Match5Hi, s.avgB);
        Vector<int> fThr = GatherB(Match5Lo, s.avgR), fThg = GatherB(Match6Lo, s.avgG), fThb = GatherB(Match5Lo, s.avgB);
        Vector<int> tlr = Vector.ConditionalSelect(okMask, sTlr, fTlr), tlg = Vector.ConditionalSelect(okMask, sTlg, fTlg), tlb = Vector.ConditionalSelect(okMask, sTlb, fTlb);
        Vector<int> thr = Vector.ConditionalSelect(okMask, sThr, fThr), thg = Vector.ConditionalSelect(okMask, sThg, fThg), thb = Vector.ConditionalSelect(okMask, sThb, fThb);

        var allEq = Vector.Equals(lr, tlr) & Vector.Equals(lg, tlg) & Vector.Equals(lb, tlb)
            & Vector.Equals(hr, thr) & Vector.Equals(hg, thg) & Vector.Equals(hb, thb);

        var changed = allEq ^ new Vector<int>(-1);
        Span<Vector<int>> sels2 = MemoryMarshal.Cast<byte, Vector<int>>(stackalloc byte[16 * Vector<byte>.Count]);
        FindSels8(tU, mask8, false, tlr, tlg, tlb, thr, thg, thb, sels2);
        lr = Vector.ConditionalSelect(changed, tlr, lr);
        lg = Vector.ConditionalSelect(changed, tlg, lg);
        lb = Vector.ConditionalSelect(changed, tlb, lb);
        hr = Vector.ConditionalSelect(changed, thr, hr);
        hg = Vector.ConditionalSelect(changed, thg, hg);
        hb = Vector.ConditionalSelect(changed, thb, hb);
        for (var i = 0; i < 16; i++) sels[i] = Vector.ConditionalSelect(changed, sels2[i], sels[i]);
    }

    // Encode the BC1 color half of Vector<int>.Count block-contiguous blocks into per-lane words (w0 =
    // color0|color1<<16, w1 = packed selectors), bit-identical to EncodeBlock per lane. Caller scatters; see EncodeColorStrided8.
    static void EncodeColor8(scoped ReadOnlySpan<ColorRgba> px8,
        Bc1Quality quality,
        out Vector<int> w0,
        out Vector<int> w1)
    {
        Span<uint> tU = stackalloc uint[16 * Vector<int>.Count];
        Transpose8(px8, tU);
        var mask8 = new Vector<uint>(0xFFu);
        var s = Setup8(tU, mask8);

        Span<Vector<int>> sels = MemoryMarshal.Cast<byte, Vector<int>>(stackalloc byte[16 * Vector<byte>.Count]);
        Vector<int> lr, lg, lb, hr, hg, hb;
        if (quality == Bc1Quality.Fast)
            RunFast8(tU, mask8, in s, out lr, out lg, out lb, out hr, out hg, out hb, sels);
        else
        {
            PickInitialPcaCore8(tU, mask8, in s, quality == Bc1Quality.HighQuality ? 6 : 4, out lr, out lg, out lb, out hr, out hg, out hb);
            RunDescent8(tU, mask8, in s, ref lr, ref lg, ref lb, ref hr, ref hg, ref hb, sels);
        }

        Encode4x8(lr, lg, lb, hr, hg, hb, sels, out w0, out w1);
        EncodeSolid8(s.r0, s.g0, s.b0, out var sw0, out var sw1);
        w0 = Vector.ConditionalSelect(s.solid, sw0, w0);
        w1 = Vector.ConditionalSelect(s.solid, sw1, w1);
    }

    // Scatter the per-lane color words into a (possibly strided) output: BC1 = 8-byte blocks at offset 0; BC3/BC2 =
    // 16-byte blocks with the color half at offset 8. blockBytes and colorOffset must be 4-byte aligned.
    internal static void EncodeColorStrided8(scoped ReadOnlySpan<ColorRgba> px8,
        scoped Span<byte> output,
        int blockBytes,
        int colorOffset,
        Bc1Quality quality)
    {
        EncodeColor8(px8, quality, out var w0, out var w1);
        ref var outU = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, uint>(output));
        int u = blockBytes / 4, co = colorOffset / 4;
        for (var b = 0; b < Vector<int>.Count; b++)
        {
            Unsafe.Add(ref outU, (nuint)(b * u + co)) = (uint)w0[b];
            Unsafe.Add(ref outU, (nuint)(b * u + co + 1)) = (uint)w1[b];
        }
    }

    // Encode Vector<int>.Count block-contiguous 4x4 blocks to that many packed BC1 blocks (lane=block).
    internal static void EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8, Bc1Quality quality)
        => EncodeColorStrided8(px8, out8, 8, 0, quality);

    // ---- test entry points (validate each stage against the scalar per-block path) ----
    internal static void PickInitialPca8(scoped ReadOnlySpan<ColorRgba> px8,
        int powerIters,
        out Vector<int> lr,
        out Vector<int> lg,
        out Vector<int> lb,
        out Vector<int> hr,
        out Vector<int> hg,
        out Vector<int> hb)
    {
        Span<uint> tU = stackalloc uint[16 * Vector<int>.Count];
        Transpose8(px8, tU);
        var mask8 = new Vector<uint>(0xFFu);
        var s = Setup8(tU, mask8);
        PickInitialPcaCore8(tU, mask8, in s, powerIters, out lr, out lg, out lb, out hr, out hg, out hb);
    }
}