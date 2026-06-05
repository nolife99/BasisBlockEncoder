namespace Bcn.Bc7;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

static partial class Bc7Block
{
    // ---- thresholds (faithful to bc7f; this is one of its two #if config sets) ----
    const int TrivialBlockThreshRgb = 20 * 16; // below this variance: skip PCA, use trivial mode 6
    const int DpBlockVarThresh = 2 * 16; // consider dual plane above this
    const float StrongCorrThresh = 0.85f; // channel-decorrelation cutoff for dual plane
    const int MinBlockMaxVar23Subsets = 100 * 16; // consider 2/3 subsets above this
    const float OrthoRatio23SubsetThresh = 0.004f; // ...and only if this fraction of energy is off-axis
    const float HighOrthoEnergyThresh = 1.0f * 16; // gate for actually attempting 2+ subsets
    const int MinBlockMaxVar3Subsets = 500 * 16; // consider 3 subsets above this
    const float SmallFloat = 1e-7f; // basisu::SMALL_FLOAT_VAL
    const long Mode6PruneMargin = 32; // >= mean-rounding inflation (<=12) + fp slack; keeps the bound sound

    // ================== analytical core — the bc7f speed technique ==================
    //
    // This is the technique that makes bc7f near-instant and is not present in conventional encoders:
    // the speed comes from the estimate-and-bail cascade below, not from the per-mode encoders. The
    // per-mode encoders on their own produce a correct but several-times-slower BC7 codec.

    const float UniquePbitDiscount = 0.85f;
    const float SharedPbitDiscount = 0.95f;

    // --- HQ-path inverted mode-6 prune: skip mode 6 when a candidate provably beats its lower bound ---
    internal static bool Mode6PruneEnabled = false; // prune disabled by default; the code path is retained but inert

    internal static void Encode(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output, Bc7Flags flags)
    {
        // Any non-opaque pixel? ColorRgba reinterprets to uint as A<<24|B<<16|G<<8|R, so alpha is the high byte
        // and "all opaque" == every uint has its top byte set. One SIMD test (16 px = 2x Vector256) replaces the
        // bounds-checked per-pixel scan that ran on every block. Byte-identical (same hasAlpha => same routing).
        var pu = MemoryMarshal.Cast<ColorRgba, uint>(pixels);
        bool hasAlpha;
        if (Vector256.IsHardwareAccelerated)
        {
            var top = Vector256.Create(0xFF000000u);
            hasAlpha = !(Vector256.EqualsAll(Vector256.Create(pu) & top, top)
                && Vector256.EqualsAll(Vector256.Create(pu.Slice(8)) & top, top));
        }
        else
        {
            hasAlpha = false;
            for (var i = 0; i < 16; i++)
                if ((pu[i] & 0xFF000000u) != 0xFF000000u)
                {
                    hasAlpha = true;
                    break;
                }
        }

        if (hasAlpha)
        {
            if ((flags & Bc7Flags.PartiallyAnalyticalRgba) != 0) PackRgbaPartialAnalytical(pixels, output, flags);
            else PackRgbaAnalytical(pixels, output, flags);
        }
        else
        {
            if ((flags & Bc7Flags.PartiallyAnalyticalRgb) != 0) PackRgbPartialAnalytical(pixels, output, flags);
            else PackRgbAnalytical(pixels, output, flags);
        }
    }

    // Reinterpret a 4-byte ColorRgba as uint (== R|G<<8|B<<16|A<<24). net7-safe; replaces Unsafe.BitCast (net8+).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint AsU32(in ColorRgba c) => Unsafe.As<ColorRgba, uint>(ref Unsafe.AsRef(in c));

    static void PackRgbAnalytical(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block, Bc7Flags flags)
    {
        // solid fast path: all 16 pixels identical as uint
        var fc = AsU32(in px[0]);
        var solid = true;
        for (var k = 1; k < 16; k++)
            if (AsU32(in px[k]) != fc)
            {
                solid = false;
                break;
            }

        if (solid)
        {
            PackMode5Solid(block, px[0]);
            return;
        }

        // --- statistics: mean + min/max (branchless SIMD Min/Max, integer => byte-identical), then covariance ---
        Span<int> bs = stackalloc int[9];
        BlockStatsRgb(px, bs);
        int totR = bs[0], totG = bs[1], totB = bs[2];
        int minR = bs[3], minG = bs[4], minB = bs[5], maxR = bs[6], maxG = bs[7], maxB = bs[8];
        int meanR = totR + 8 >> 4, meanG = totG + 8 >> 4, meanB = totB + 8 >> 4;

        Span<int> icov = stackalloc int[6];
        CovarianceRgb(px, meanR, meanG, meanB, icov);
        var blockMaxVar = Math.Max(icov[0], Math.Max(icov[3], icov[5]));
        if (blockMaxVar == 0)
        {
            PackMode5Solid(block, px[0]);
            return;
        }

        // --- dual-plane decorrelation detection (modes 4/5) ---
        var desiredDpChan = -1;
        if ((flags & Bc7Flags.UseDualPlaneRgb) != 0 && blockMaxVar >= DpBlockVarThresh)
        {
            bool hasR = icov[0] > 16, hasG = icov[3] > 16, hasB = icov[5] > 16;
            var active = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
            if (active >= 2)
            {
                float rv = icov[0], gv = icov[3], bv = icov[5];
                var rg = hasR && hasG ? MathF.Abs(icov[1] / MathF.Sqrt(rv * gv)) : 1f;
                var rb = hasR && hasB ? MathF.Abs(icov[2] / MathF.Sqrt(rv * bv)) : 1f;
                var gb = hasG && hasB ? MathF.Abs(icov[4] / MathF.Sqrt(gv * bv)) : 1f;
                if (MathF.Min(rg, MathF.Min(rb, gb)) < StrongCorrThresh)
                {
                    if (active == 2) desiredDpChan = !hasR ? 1 : 0; // the decorrelated channel
                    else if (rg < gb && rb < gb) desiredDpChan = 0;
                    else if (rg < rb && gb < rb) desiredDpChan = 1;
                    else desiredDpChan = 2;
                }
            }
        }

        // --- trivial mode 6 (flat block: skip PCA entirely) ---
        if ((flags & Bc7Flags.UseTrivialMode6) != 0 && desiredDpChan == -1 && blockMaxVar < TrivialBlockThreshRgb)
        {
            PackTrivialMode6(px, block, flags);
            return;
        }

        // ===================== the bc7f analytical brain =====================
        Span<float> cov = stackalloc float[6];
        for (var i = 0; i < 6; i++) cov[i] = icov[i];

        // (1) one-step power-iteration estimate of the principal axis.
        EstimatePrincipalAxis(cov, blockMaxVar, out var axR, out var axG, out var axB);

        // (2) slam-to-line residual + ortho ratio, and mode 6's predicted SSE (the baseline to beat).
        Span<int> spans = stackalloc int[4];
        spans[0] = maxR - minR;
        spans[1] = maxG - minG;
        spans[2] = maxB - minB;
        spans[3] = 0;
        var needEst = (flags & Bc7Flags.Use2SubsetsRgb) != 0 || desiredDpChan >= 0;
        float orthoRatio = 0, mode6Slam = 0, mode6Est = 0;
        if (needEst)
        {
            mode6Slam = EstimateSlamToLineSse(cov, axR, axG, axB, out orthoRatio);
            mode6Est = mode6Slam + AnalyticalQuantEstSse(128, 16, 3, spans, default, 1.0f, 16);
        }

        // (3) escalation GATE: only spend on multi-subset modes when the block is geometrically
        //     poorly fit by one line (high off-axis energy) AND has enough variance. Most blocks
        //     fail this and go straight to mode 6 — a big part of why bc7f is near-instant.
        if ((flags & Bc7Flags.Use2SubsetsRgb) != 0 &&
            blockMaxVar >= MinBlockMaxVar23Subsets && orthoRatio > OrthoRatio23SubsetThresh &&
            mode6Slam >= HighOrthoEnergyThresh)
        {
            // (4) estimate -> bail cascade. Each evaluator commits only if its estimate beats the
            //     current ceiling, and tightens the ceiling for the next, more expensive candidate.
            if ((flags & Bc7Flags.Use3SubsetsRgb) != 0 && blockMaxVar >= MinBlockMaxVar3Subsets)
            {
                if (PackMode0Or2Rgb(block, px, axR, axG, axB, meanR, meanG, meanB, mode6Est, flags, false, out var est02, out _))
                {
                    Span<byte> tmp = stackalloc byte[16];
                    if (PackMode1Or3Rgb(tmp, px, axR, axG, axB, meanR, meanG, meanB, est02, flags, false, out _, out _))
                        tmp.CopyTo(block); // 2-subset beat the 3-subset estimate

                    return;
                }
            }

            if (PackMode1Or3Rgb(block, px, axR, axG, axB, meanR, meanG, meanB, mode6Est, flags, false, out _, out _))
                return;
        }

        if (desiredDpChan >= 0 && PackMode4Or5(block, px, desiredDpChan, mode6Est, flags, false, out _, out _))
            return;

        // (5) default: mode 6 with PCA-projected endpoints (the common-case commit).
        PackMode6Default(px, block, axR, axG, axB, totR, totG, totB, flags, false, out _);
    }

    // bc7f::fast_pack_bc7_rgb_partial_analytical — the partially-analytical (HighQuality) RGB path. Same
    // setup and gating as PackRgbAnalytical, but every gated candidate is actually encoded and its true
    // reconstruction SSE computed (decode + sum), then the genuine minimum is chosen (tie-break 4/5 > 0/2
    // > 1/3 > 6, matching upstream), instead of committing the first mode whose estimate beats a ceiling.
    static void PackRgbPartialAnalytical(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block, Bc7Flags flags)
    {
        var fc = AsU32(in px[0]);
        var solid = true;
        for (var k = 1; k < 16; k++)
            if (AsU32(in px[k]) != fc)
            {
                solid = false;
                break;
            }

        if (solid)
        {
            PackMode5Solid(block, px[0]);
            return;
        }

        Span<int> bs = stackalloc int[9];
        BlockStatsRgb(px, bs);
        int totR = bs[0], totG = bs[1], totB = bs[2];
        int minR = bs[3], minG = bs[4], minB = bs[5], maxR = bs[6], maxG = bs[7], maxB = bs[8];
        int meanR = totR + 8 >> 4, meanG = totG + 8 >> 4, meanB = totB + 8 >> 4;

        Span<int> icov = stackalloc int[6];
        CovarianceRgb(px, meanR, meanG, meanB, icov);
        var blockMaxVar = Math.Max(icov[0], Math.Max(icov[3], icov[5]));
        if (blockMaxVar == 0)
        {
            PackMode5Solid(block, px[0]);
            return;
        }

        var desiredDpChan = -1;
        if ((flags & Bc7Flags.UseDualPlaneRgb) != 0 && blockMaxVar >= DpBlockVarThresh)
        {
            bool hasR = icov[0] > 16, hasG = icov[3] > 16, hasB = icov[5] > 16;
            var active = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
            if (active >= 2)
            {
                float rv = icov[0], gv = icov[3], bv = icov[5];
                var rg = hasR && hasG ? MathF.Abs(icov[1] / MathF.Sqrt(rv * gv)) : 1f;
                var rb = hasR && hasB ? MathF.Abs(icov[2] / MathF.Sqrt(rv * bv)) : 1f;
                var gb = hasG && hasB ? MathF.Abs(icov[4] / MathF.Sqrt(gv * bv)) : 1f;
                if (MathF.Min(rg, MathF.Min(rb, gb)) < StrongCorrThresh)
                {
                    if (active == 2) desiredDpChan = !hasR ? 1 : 0;
                    else if (rg < gb && rb < gb) desiredDpChan = 0;
                    else if (rg < rb && gb < rb) desiredDpChan = 1;
                    else desiredDpChan = 2;
                }
            }
        }

        if ((flags & Bc7Flags.UseTrivialMode6) != 0 && desiredDpChan == -1 && blockMaxVar < TrivialBlockThreshRgb)
        {
            PackTrivialMode6(px, block, flags);
            return;
        }

        Span<float> cov = stackalloc float[6];
        for (var i = 0; i < 6; i++) cov[i] = icov[i];
        EstimatePrincipalAxis(cov, blockMaxVar, out var axR, out var axG, out var axB);

        // ---- Evaluate the gated candidate modes first, then skip mode 6 when it provably
        //      cannot win — i.e. when a candidate's true SSE is already <= mode 6's exact off-axis residual
        //      lower bound (trace(cov) - lambdaMax). bound > 0 also proves mode 6 imperfect, so this fires
        //      exactly where the original (mode6Sse != 0, tie-break 4/5 > 0/2 > 1/3 > 6) picks a candidate. ----
        var mode6Slam = EstimateSlamToLineSse(cov, axR, axG, axB, out var orthoRatio);

        Span<byte> cand02 = stackalloc byte[16];
        var sse02 = uint.MaxValue;
        Span<byte> cand13 = stackalloc byte[16];
        var sse13 = uint.MaxValue;
        Span<byte> cand45 = stackalloc byte[16];
        var sse45 = uint.MaxValue;

        if ((flags & Bc7Flags.Use2SubsetsRgb) != 0 &&
            blockMaxVar >= MinBlockMaxVar23Subsets && orthoRatio > OrthoRatio23SubsetThresh &&
            mode6Slam >= HighOrthoEnergyThresh)
        {
            if ((flags & Bc7Flags.Use3SubsetsRgb) != 0 && blockMaxVar >= MinBlockMaxVar3Subsets)
            {
                if (PackMode0Or2Rgb(cand02, px, axR, axG, axB, meanR, meanG, meanB, 1e9f, flags, true, out _, out var t02)) sse02 = t02;
                if (PackMode1Or3Rgb(cand13, px, axR, axG, axB, meanR, meanG, meanB, 1e9f, flags, true, out _, out var t13)) sse13 = t13;
            }
            else
            {
                if (PackMode1Or3Rgb(cand13, px, axR, axG, axB, meanR, meanG, meanB, 1e9f, flags, true, out _, out var t13)) sse13 = t13;
            }
        }

        if (desiredDpChan >= 0)
        {
            if (PackMode4Or5(cand45, px, desiredDpChan, 1e9f, flags, true, out _, out var t45)) sse45 = t45;
        }

        var candBest = Math.Min(sse45, Math.Min(sse02, sse13));

        if (candBest != uint.MaxValue && Mode6PruneEnabled)
        {
            var lam = LambdaMax3x3Sym(icov[0], icov[1], icov[2], icov[3], icov[4], icov[5]);
            var bound = (long)Math.Floor(icov[0] + icov[3] + icov[5] - lam) - Mode6PruneMargin;
            if (bound > 0 && candBest <= (ulong)bound) // mode 6 provably loses and is imperfect
            {
                if (sse45 != uint.MaxValue && candBest == sse45) cand45.CopyTo(block);
                else if (sse02 != uint.MaxValue && candBest == sse02) cand02.CopyTo(block);
                else cand13.CopyTo(block);

                return;
            }
        }

        // mode 6 needed: evaluate it, then take the genuine minimum (same tie-break as upstream).
        PackMode6Default(px, block, axR, axG, axB, totR, totG, totB, flags, true, out var mode6Sse);
        if (mode6Sse == 0) return; // perfect mode 6 wins (replicates the original mode6Sse != 0 guard)

        var best = Math.Min(mode6Sse, candBest);
        if (sse45 != uint.MaxValue && best == sse45) cand45.CopyTo(block);
        else if (sse02 != uint.MaxValue && best == sse02) cand02.CopyTo(block);
        else if (sse13 != uint.MaxValue && best == sse13) cand13.CopyTo(block);
        // else: mode 6 (already written to block) wins
    }

    // The RGBA analytical path (PackRgbaAnalytical) + mode-6 RGBA helpers live in Bc7Rgba.cs.

    // Flat-block path: endpoints from the darkest/brightest pixel by luma (index packed in low nibble).
    static void PackTrivialMode6(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> block, Bc7Flags flags)
    {
        int lo = int.MaxValue, hi = 0;
        ref var pr = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
        {
            ref readonly var q = ref Unsafe.Add(ref pr, i);
            var y = 32 * q.R + 64 * q.G + 16 * q.B + i;
            if (y < lo) lo = y;
            if (y > hi) hi = y;
        }

        int loIdx = lo & 0xF, hiIdx = hi & 0xF;
        uint p0 = 1, p1 = 1;
        int lr = To7(px[loIdx].R, p0), lg = To7(px[loIdx].G, p0), lb = To7(px[loIdx].B, p0);
        int hr = To7(px[hiIdx].R, p1), hg = To7(px[hiIdx].G, p1), hb = To7(px[hiIdx].B, p1);
        Span<int> w = stackalloc int[16];
        EvalWeightsMode6(px, w, lr, lg, lb, hr, hg, hb, p0, p1);
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

    // ============ analytical estimators (the geometric half of the brain) ============

    // One power-iteration step: axis = Cov * (diag(Cov)/maxVar). Cheap dominant-eigenvector estimate.
    static void EstimatePrincipalAxis(scoped ReadOnlySpan<float> cov,
        int blockMaxVar,
        out float axR,
        out float axG,
        out float axB)
    {
        var sc = blockMaxVar != 0 ? 1.0f / blockMaxVar : 0f;
        float wx = sc * cov[0], wy = sc * cov[3], wz = sc * cov[5];
        axR = cov[0] * wx + cov[1] * wy + cov[2] * wz;
        axG = cov[1] * wx + cov[3] * wy + cov[4] * wz;
        axB = cov[2] * wx + cov[4] * wy + cov[5] * wz;
    }

    // Largest eigenvalue of the symmetric 3x3 covariance [a b c; b d e; c e f] (cov[0..5]=a,b,c,d,e,f),
    // via Smith's closed-form trig method. Unlike the one-step power-iteration axis, this is the *true*
    // lambda_max, so totalVar - lambda_max is the exact off-axis (slam-to-line) residual: a provable lower
    // bound on any single-line fit's SSE, hence on mode 6's SSE. Used to prune mode 6 when a multi-subset
    // candidate already beats it. ~30 flops + acos/cos/sqrt; negligible vs a mode-6 evaluation.
    internal static float MaxEig3x3Sym(scoped ReadOnlySpan<float> cov)
    {
        float a = cov[0], b = cov[1], c = cov[2], d = cov[3], e = cov[4], f = cov[5];
        var p1 = b * b + c * c + e * e;
        if (p1 <= 0f) return MathF.Max(a, MathF.Max(d, f)); // already diagonal

        var q = (a + d + f) * (1f / 3f);
        float aq = a - q, dq = d - q, fq = f - q;
        var p2 = aq * aq + dq * dq + fq * fq + 2f * p1;
        var p = MathF.Sqrt(p2 * (1f / 6f));
        var ip = 1f / p;
        float b00 = aq * ip, b11 = dq * ip, b22 = fq * ip, b01 = b * ip, b02 = c * ip, b12 = e * ip;
        var detB = b00 * (b11 * b22 - b12 * b12) - b01 * (b01 * b22 - b12 * b02) + b02 * (b01 * b12 - b11 * b02);
        var r = detB * 0.5f;
        r = r < -1f ? -1f :
            r > 1f ? 1f : r;

        var phi = MathF.Acos(r) * (1f / 3f);
        var lam = q + 2f * p * MathF.Cos(phi); // largest of the three eigenvalues
        // numerical safety: lambda_max is provably in [q, q + 2p] (q = trace/3, cos(phi) in [-1,1]).
        // Clamp to that range so any float blow-up can't corrupt the downstream pruning bound.
        var hi = q + 2f * p;
        return lam < q ? q :
            lam > hi ? hi : lam;
    }

    // Off-axis (slam-to-line) residual SSE = total variance - variance explained by the axis
    // (Rayleigh quotient). Also returns orthoRatio = residual / total, the escalation-gate input.
    static float EstimateSlamToLineSse(scoped ReadOnlySpan<float> cov, float xr, float yr, float zr, out float orthoRatio)
    {
        var totalVar = cov[0] + cov[3] + cov[5];
        var l = MathF.Sqrt(xr * xr + yr * yr + zr * zr);
        if (l < SmallFloat) xr = yr = zr = 0.577350269f;
        else l = 1f / l;

        xr *= l;
        yr *= l;
        zr *= l;

        var xr2 = cov[0] * xr + cov[1] * yr + cov[2] * zr;
        var xg2 = cov[1] * xr + cov[3] * yr + cov[4] * zr;
        var xb2 = cov[2] * xr + cov[4] * yr + cov[5] * zr;

        var principalAxisVar = xr2 * xr + xg2 * yr + xb2 * zr;
        var orthoVar = MathF.Max(0f, totalVar - principalAxisVar);
        orthoRatio = totalVar > SmallFloat ? orthoVar / totalVar : 0f;
        return orthoVar;
    }

    // Largest eigenvalue of the 3x3 symmetric matrix [[a,b,c],[b,d,e],[c,e,f]] (double, closed-form trig
    // method). trace - lambdaMax is the exact off-axis (slam-to-line) residual, the lower bound on mode-6
    // SSE used by the HQ prune. Float/mean-rounding slack is absorbed by Mode6PruneMargin at the call site.
    static double LambdaMax3x3Sym(double a, double b, double c, double d, double e, double f)
    {
        var p1 = b * b + c * c + e * e;
        if (p1 <= 0.0) return Math.Max(a, Math.Max(d, f)); // already diagonal

        var q = (a + d + f) / 3.0;
        double aq = a - q, dq = d - q, fq = f - q;
        var p = Math.Sqrt((aq * aq + dq * dq + fq * fq + 2.0 * p1) / 6.0);
        var ip = 1.0 / p;
        double ba = aq * ip, bd = dq * ip, bf = fq * ip, bb = b * ip, bc = c * ip, be = e * ip;
        var detB = ba * (bd * bf - be * be) - bb * (bb * bf - be * bc) + bc * (bb * be - bd * bc);
        var r = detB * 0.5;
        r = r < -1.0 ? -1.0 :
            r > 1.0 ? 1.0 : r;

        return q + 2.0 * p * Math.Cos(Math.Acos(r) / 3.0); // phi in [0, pi/3] -> largest root
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To7(int c8, uint pbit)
    {
        var e = (uint)(c8 + (int)(pbit ^ 1)) >> 1;
        return (int)Math.Min(127u, e);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From7(int v, uint p) => v << 1 | (int)p;

    // Decode-free true SSE: reconstruct each pixel as Lerp(expanded endpoint, weight[selector]) and sum
    // squared RGBA error vs source. Bit-identical to DecodeBlock+SsdRgba (same From7-expanded endpoints,
    // same integer Lerp, same accumulation) but skips re-parsing the 128-bit block the packer just wrote.
    // Endpoints passed in are already 8-bit (From7-expanded); selectors index the mode's weight table.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int LerpC(int a, int b, int w) => a * (64 - w) + b * w + 32 >> 6;

    // Multi-subset (modes 0/1/2/3) decode-free SSE: per pixel, subset = partition[base+i], reconstruct via
    // Lerp(expanded endpoint[subset], weight[selector]). RGB modes decode alpha to 255 (so da = A-255 matches
    // bc7f's RGBA bc7_sse exactly). Endpoints passed in are already From6/From7-expanded per subset.
    internal static long SseMultiSubsetRgb(
        scoped ReadOnlySpan<int> loR,
        scoped ReadOnlySpan<int> loG,
        scoped ReadOnlySpan<int> loB,
        scoped ReadOnlySpan<int> hiR,
        scoped ReadOnlySpan<int> hiG,
        scoped ReadOnlySpan<int> hiB,
        scoped ReadOnlySpan<byte> part,
        int partBase,
        scoped ReadOnlySpan<int> wt,
        scoped ReadOnlySpan<int> sel,
        scoped ReadOnlySpan<ColorRgba> px)
    {
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        ref var d = ref MemoryMarshal.GetReference(dec);
        ref var se = ref MemoryMarshal.GetReference(sel);
        ref var wtab = ref MemoryMarshal.GetReference(wt);
        ref var pt = ref MemoryMarshal.GetReference(part);
        for (var i = 0; i < 16; i++)
        {
            int sub = Unsafe.Add(ref pt, partBase + i);
            var w = Unsafe.Add(ref wtab, Unsafe.Add(ref se, i));
            Unsafe.Add(ref d, i) = new((byte)LerpC(loR[sub], hiR[sub], w), (byte)LerpC(loG[sub], hiG[sub], w), (byte)LerpC(loB[sub], hiB[sub], w), 255);
        }

        return SsdRgba(px, dec);
    }

    // Multi-subset RGBA (mode 7) decode-free SSE — like SseMultiSubsetRgb but with a real per-subset alpha
    // endpoint pair (alpha is reconstructed, not forced to 255).
    internal static long SseMultiSubsetRgba(
        scoped ReadOnlySpan<int> loR,
        scoped ReadOnlySpan<int> loG,
        scoped ReadOnlySpan<int> loB,
        scoped ReadOnlySpan<int> loA,
        scoped ReadOnlySpan<int> hiR,
        scoped ReadOnlySpan<int> hiG,
        scoped ReadOnlySpan<int> hiB,
        scoped ReadOnlySpan<int> hiA,
        scoped ReadOnlySpan<byte> part,
        int partBase,
        scoped ReadOnlySpan<int> wt,
        scoped ReadOnlySpan<int> sel,
        scoped ReadOnlySpan<ColorRgba> px)
    {
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        ref var d = ref MemoryMarshal.GetReference(dec);
        ref var se = ref MemoryMarshal.GetReference(sel);
        ref var wtab = ref MemoryMarshal.GetReference(wt);
        ref var pt = ref MemoryMarshal.GetReference(part);
        for (var i = 0; i < 16; i++)
        {
            int sub = Unsafe.Add(ref pt, partBase + i);
            var w = Unsafe.Add(ref wtab, Unsafe.Add(ref se, i));
            Unsafe.Add(ref d, i) = new((byte)LerpC(loR[sub], hiR[sub], w), (byte)LerpC(loG[sub], hiG[sub], w), (byte)LerpC(loB[sub], hiB[sub], w), (byte)LerpC(loA[sub], hiA[sub], w));
        }

        return SsdRgba(px, dec);
    }

    // Dual-plane (modes 4/5) decode-free SSE. The packer works in ROTATED channel space; since un-rotation
    // is a channel permutation, SSE is permutation-invariant, so we sum vs the rotated pixels directly.
    // RGB channels use the colour plane (w0/colourWt); the rotated-alpha slot uses the scalar plane (w1/alphaWt).
    internal static long SseDualPlane(
        int r0,
        int g0,
        int b0,
        int a0,
        int r1,
        int g1,
        int b1,
        int a1,
        scoped ReadOnlySpan<int> colourWt,
        scoped ReadOnlySpan<int> alphaWt,
        scoped ReadOnlySpan<int> w0,
        scoped ReadOnlySpan<int> w1,
        scoped ReadOnlySpan<ColorRgba> px)
    {
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        if (Ssse3.IsSupported)
            LerpDualPlaneX86(r0, g0, b0, a0, r1, g1, b1, a1, colourWt, alphaWt, w0, w1, dec);
        else if (AdvSimd.Arm64.IsSupported)
            LerpDualPlaneArm(r0, g0, b0, a0, r1, g1, b1, a1, colourWt, alphaWt, w0, w1, dec);
        else
        {
            ref var d = ref MemoryMarshal.GetReference(dec);
            ref var cw = ref MemoryMarshal.GetReference(colourWt);
            ref var aw = ref MemoryMarshal.GetReference(alphaWt);
            ref var s0 = ref MemoryMarshal.GetReference(w0);
            ref var s1 = ref MemoryMarshal.GetReference(w1);
            for (var i = 0; i < 16; i++)
            {
                var wc = Unsafe.Add(ref cw, Unsafe.Add(ref s0, i));
                var wa = Unsafe.Add(ref aw, Unsafe.Add(ref s1, i));
                Unsafe.Add(ref d, i) = new((byte)LerpC(r0, r1, wc), (byte)LerpC(g0, g1, wc), (byte)LerpC(b0, b1, wc), (byte)LerpC(a0, a1, wa));
            }
        }

        return SsdRgba(px, dec);
    }

    // x86 dual-plane PMADDUBSW lerp: R,G,B share the colour-plane weight operand [64-wc,wc,...]; A uses the
    // alpha-plane operand [64-wa,wa,...]. Same constant [lo,hi] a-operands and RGBA weave as the mode-6 path.
    static void LerpDualPlaneX86(
        int r0,
        int g0,
        int b0,
        int a0,
        int r1,
        int g1,
        int b1,
        int a1,
        scoped ReadOnlySpan<int> colourWt,
        scoped ReadOnlySpan<int> alphaWt,
        scoped ReadOnlySpan<int> w0,
        scoped ReadOnlySpan<int> w1,
        scoped Span<ColorRgba> dec)
    {
        ref var cw = ref MemoryMarshal.GetReference(colourWt);
        ref var aw = ref MemoryMarshal.GetReference(alphaWt);
        ref var s0 = ref MemoryMarshal.GetReference(w0);
        ref var s1 = ref MemoryMarshal.GetReference(w1);
        var db = MemoryMarshal.AsBytes(dec);
        var aR = Vector128.Create((ushort)((uint)r1 << 8 | (uint)r0)).AsByte();
        var aG = Vector128.Create((ushort)((uint)g1 << 8 | (uint)g0)).AsByte();
        var aB = Vector128.Create((ushort)((uint)b1 << 8 | (uint)b0)).AsByte();
        var aA = Vector128.Create((ushort)((uint)a1 << 8 | (uint)a0)).AsByte();
        var c64 = Vector128.Create((byte)64);
        var c32 = Vector128.Create((short)32);
        for (var o = 0; o < 16; o += 8)
        {
            var wcv = Vector128.Create(
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 1)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 2)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 3)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 4)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 5)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 6)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 7)),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);

            var wav = Vector128.Create(
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 1)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 2)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 3)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 4)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 5)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 6)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 7)),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);

            var bc = Sse2.UnpackLow(c64 - wcv, wcv).AsSByte();
            var bal = Sse2.UnpackLow(c64 - wav, wav).AsSByte();
            var rR = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aR, bc) + c32, 6);
            var rG = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aG, bc) + c32, 6);
            var rB = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aB, bc) + c32, 6);
            var rA = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aA, bal) + c32, 6);
            WeaveStoreX86(rR, rG, rB, rA, db, o);
        }
    }

    // ARM dual-plane: R,G,B use the colour-plane weights via vmull+vmlal; A uses the alpha-plane weights. Same
    // xtn-narrow + vzip RGBA weave as the mode-6 ARM path.
    static void LerpDualPlaneArm(
        int r0,
        int g0,
        int b0,
        int a0,
        int r1,
        int g1,
        int b1,
        int a1,
        scoped ReadOnlySpan<int> colourWt,
        scoped ReadOnlySpan<int> alphaWt,
        scoped ReadOnlySpan<int> w0,
        scoped ReadOnlySpan<int> w1,
        scoped Span<ColorRgba> dec)
    {
        ref var cw = ref MemoryMarshal.GetReference(colourWt);
        ref var aw = ref MemoryMarshal.GetReference(alphaWt);
        ref var s0 = ref MemoryMarshal.GetReference(w0);
        ref var s1 = ref MemoryMarshal.GetReference(w1);
        var db = MemoryMarshal.AsBytes(dec);
        Vector64<byte> r0v = Vector64.Create((byte)r0), r1v = Vector64.Create((byte)r1);
        Vector64<byte> g0v = Vector64.Create((byte)g0), g1v = Vector64.Create((byte)g1);
        Vector64<byte> b0v = Vector64.Create((byte)b0), b1v = Vector64.Create((byte)b1);
        Vector64<byte> a0v = Vector64.Create((byte)a0), a1v = Vector64.Create((byte)a1);
        var c64 = Vector64.Create((byte)64);
        var c32 = Vector128.Create((ushort)32);
        for (var o = 0; o < 16; o += 8)
        {
            var wcv = Vector64.Create(
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 1)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 2)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 3)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 4)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 5)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 6)),
                (byte)Unsafe.Add(ref cw, Unsafe.Add(ref s0, o + 7)));

            var wav = Vector64.Create(
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 1)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 2)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 3)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 4)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 5)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 6)),
                (byte)Unsafe.Add(ref aw, Unsafe.Add(ref s1, o + 7)));

            var invc = AdvSimd.Subtract(c64, wcv);
            var inva = AdvSimd.Subtract(c64, wav);
            var rR = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(r0v, invc), r1v, wcv), c32), 6);
            var rG = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(g0v, invc), g1v, wcv), c32), 6);
            var rB = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(b0v, invc), b1v, wcv), c32), 6);
            var rA = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(a0v, inva), a1v, wav), c32), 6);
            Vector64<byte> pR = AdvSimd.ExtractNarrowingLower(rR), pG = AdvSimd.ExtractNarrowingLower(rG);
            Vector64<byte> pB = AdvSimd.ExtractNarrowingLower(rB), pA = AdvSimd.ExtractNarrowingLower(rA);
            WeaveStoreArm(pR, pG, pB, pA, db, o);
        }
    }

    // Fused mode-6 final pass (x86): computes the 16 selectors (into w, for the packer) AND the
    // reconstruction SSE in ONE traversal of the block, replacing EvalWeightsMode6 (project->index) +
    // SseSingleSubsetRgba (reconstruct->sum), which together walked the 16 px three times (project, lerp,
    // ssd) through a dec[16] scratch buffer. Byte-identical to that composition on x86: same FMA index
    // (as EvalSelRgbX86), same integer lerp (== the PMADDUBSW lo*(64-w)+hi*w path, +32>>6), same integer
    // RGBA SSD (as SsdRgba). Endpoints are the 7-bit pair + p-bits; alpha = From7(127, p), as in mode 6.
    // Weights4[sel] (sel in 0..15) is gathered with two vpermd over the table halves + a blend on sel>=8.
    internal static long EvalSelSseRgb6X86(scoped ReadOnlySpan<ColorRgba> px,
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
        int Lr = From7(lr, p0), Lg = From7(lg, p0), Lb = From7(lb, p0), La = From7(127, p0);
        int Hr = From7(hr, p1), Hg = From7(hg, p1), Hb = From7(hb, p1), Ha = From7(127, p1);
        int dr = Hr - Lr, dg = Hg - Lg, db = Hb - Lb;
        var f = 15.0f / (dr * dr + dg * dg + db * db + 0.00000125f); // identical to EvalWeightsMode6
        var aofs = -(Lr * dr + Lg * dg + Lb * db);

        var pru = MemoryMarshal.Cast<ColorRgba, uint>(px);
        var mask = Vector256.Create(0xFFu);
        Vector256<int> drv = Vector256.Create(dr), dgv = Vector256.Create(dg), dbv = Vector256.Create(db), ofsv = Vector256.Create(aofs);
        Vector256<float> fv = Vector256.Create(f), halfv = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(15), eight = Vector256.Create(8);
        var loW = Vector256.Create(0, 4, 9, 13, 17, 21, 26, 30); // Weights4[0..7]
        var hiW = Vector256.Create(34, 38, 43, 47, 51, 55, 60, 64); // Weights4[8..15]
        Vector256<int> LrV = Vector256.Create(Lr), HrV = Vector256.Create(Hr), LgV = Vector256.Create(Lg), HgV = Vector256.Create(Hg),
            LbV = Vector256.Create(Lb), HbV = Vector256.Create(Hb), LaV = Vector256.Create(La), HaV = Vector256.Create(Ha);

        Vector256<int> c64 = Vector256.Create(64), c32 = Vector256.Create(32);
        var acc = Vector256<int>.Zero;
        for (var o = 0; o < 16; o += 8)
        {
            var p = Vector256.Create(pru);
            var R = (p & mask).AsInt32();
            var G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32();
            var B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
            var A = (Vector256.ShiftRightLogical(p, 24) & mask).AsInt32();
            var dot = R * drv + G * dgv + B * dbv + ofsv;
            var selF = Fma.MultiplyAdd(Vector256.ConvertToSingle(dot), fv, halfv);
            var sel = Avx.ConvertToVector256Int32WithTruncation(selF);
            sel = Vector256.Min(Vector256.Max(sel, zero), mxv);
            sel.CopyTo(w.Slice(o));
            // weight = Weights4[sel]: low half indexed by sel, high half by sel-8, blended on sel>=8
            var wt = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(sel, eight),
                Vector256.Shuffle(hiW, sel - eight),
                Vector256.Shuffle(loW, sel));

            var iw = c64 - wt;
            var recR = Vector256.ShiftRightArithmetic(LrV * iw + HrV * wt + c32, 6);
            var recG = Vector256.ShiftRightArithmetic(LgV * iw + HgV * wt + c32, 6);
            var recB = Vector256.ShiftRightArithmetic(LbV * iw + HbV * wt + c32, 6);
            var recA = Vector256.ShiftRightArithmetic(LaV * iw + HaV * wt + c32, 6);
            Vector256<int> dR = R - recR, dG = G - recG, dB = B - recB, dA = A - recA;
            acc += dR * dR + dG * dG + dB * dB + dA * dA;
        }

        return Vector256.Sum(acc);
    }

    // Fused 2-subset RGB (modes 1 & 3) final pass (x86): selectors (into w) + reconstruction SSE in one
    // traversal, replacing EvalWeightsMode1/3 (project) + SseMultiSubsetRgb (scalar reconstruct -> SsdRgba).
    // Both subsets' projection AND reconstruction are computed for every pixel and blended per-pixel on the
    // partition bitmask -- the same compute-both-and-select that EvalSelRgb2X86 already uses, now extended
    // through the lerp so the previously SCALAR reconstruction is vectorized too. Byte-identical to that
    // composition on x86 (same FMA index, same integer lerp == scalar LerpC, same integer RGBA SSD; alpha is
    // forced to 255 as the RGB-mode SSE does). Endpoints are already From-expanded (as the SSE receives them);
    // f-scale numerator == clamp max == mx (7 for mode 1, 3 for mode 3). wt has mx+1 entries (<=8 => 1 vpermd).
    internal static long EvalSelSseRgb2X86(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<int> w,
        scoped ReadOnlySpan<int> eLoR,
        scoped ReadOnlySpan<int> eLoG,
        scoped ReadOnlySpan<int> eLoB,
        scoped ReadOnlySpan<int> eHiR,
        scoped ReadOnlySpan<int> eHiG,
        scoped ReadOnlySpan<int> eHiB,
        int subsetMask,
        scoped ReadOnlySpan<int> wt,
        int mx)
    {
        int dr0 = eHiR[0] - eLoR[0], dg0 = eHiG[0] - eLoG[0], db0 = eHiB[0] - eLoB[0];
        int dr1 = eHiR[1] - eLoR[1], dg1 = eHiG[1] - eLoG[1], db1 = eHiB[1] - eLoB[1];
        var f0 = mx / (dr0 * dr0 + dg0 * dg0 + db0 * db0 + 0.00000125f); // identical to EvalWeightsMode1/3
        var f1 = mx / (dr1 * dr1 + dg1 * dg1 + db1 * db1 + 0.00000125f);
        var so0 = eLoR[0] * dr0 + eLoG[0] * dg0 + eLoB[0] * db0;
        var so1 = eLoR[1] * dr1 + eLoG[1] * dg1 + eLoB[1] * db1;

        ref var pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        var m8 = Vector256.Create(0xFFu);
        Vector256<int> dr0v = Vector256.Create(dr0), dg0v = Vector256.Create(dg0), db0v = Vector256.Create(db0), so0v = Vector256.Create(so0);
        Vector256<int> dr1v = Vector256.Create(dr1), dg1v = Vector256.Create(dg1), db1v = Vector256.Create(db1), so1v = Vector256.Create(so1);
        Vector256<float> f0v = Vector256.Create(f0), f1v = Vector256.Create(f1), halfv = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx), bitsel = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        var wtv = Vector256.Create(wt[0],
            wt[1],
            wt[2],
            wt[3],
            mx >= 4 ? wt[4] : 0,
            mx >= 5 ? wt[5] : 0,
            mx >= 6 ? wt[6] : 0,
            mx >= 7 ? wt[7] : 0);

        Vector256<int> LoR0 = Vector256.Create(eLoR[0]), HiR0 = Vector256.Create(eHiR[0]), LoR1 = Vector256.Create(eLoR[1]), HiR1 = Vector256.Create(eHiR[1]);
        Vector256<int> LoG0 = Vector256.Create(eLoG[0]), HiG0 = Vector256.Create(eHiG[0]), LoG1 = Vector256.Create(eLoG[1]), HiG1 = Vector256.Create(eHiG[1]);
        Vector256<int> LoB0 = Vector256.Create(eLoB[0]), HiB0 = Vector256.Create(eHiB[0]), LoB1 = Vector256.Create(eLoB[1]), HiB1 = Vector256.Create(eHiB[1]);
        Vector256<int> c64 = Vector256.Create(64), c32 = Vector256.Create(32), c255 = Vector256.Create(255);
        var acc = Vector256<int>.Zero;
        for (var o = 0; o < 16; o += 8)
        {
            var p = Vector256.Create(pru);
            Vector256<int> R = (p & m8).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & m8).AsInt32();
            Vector256<int> B = (Vector256.ShiftRightLogical(p, 16) & m8).AsInt32(), A = (Vector256.ShiftRightLogical(p, 24) & m8).AsInt32();
            var s0 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr0v + G * dg0v + B * db0v - so0v), f0v, halfv));
            var s1 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr1v + G * dg1v + B * db1v - so1v), f1v, halfv));
            var m = Vector256.Equals(Vector256.Create(subsetMask >> (int)o & 0xFF) & bitsel, zero); // true => subset 0
            var sel = Vector256.Min(Vector256.Max(Vector256.ConditionalSelect(m, s0, s1), zero), mxv);
            sel.CopyTo(w.Slice(o));
            var wsel = Vector256.Shuffle(wtv, sel); // wt[sel], sel in 0..mx<=7
            var iw = c64 - wsel;
            var recR = Vector256.ConditionalSelect(m,
                Vector256.ShiftRightArithmetic(LoR0 * iw + HiR0 * wsel + c32, 6),
                Vector256.ShiftRightArithmetic(LoR1 * iw + HiR1 * wsel + c32, 6));

            var recG = Vector256.ConditionalSelect(m,
                Vector256.ShiftRightArithmetic(LoG0 * iw + HiG0 * wsel + c32, 6),
                Vector256.ShiftRightArithmetic(LoG1 * iw + HiG1 * wsel + c32, 6));

            var recB = Vector256.ConditionalSelect(m,
                Vector256.ShiftRightArithmetic(LoB0 * iw + HiB0 * wsel + c32, 6),
                Vector256.ShiftRightArithmetic(LoB1 * iw + HiB1 * wsel + c32, 6));

            Vector256<int> dR = R - recR, dG = G - recG, dB = B - recB, dA = A - c255;
            acc += dR * dR + dG * dG + dB * dB + dA * dA;
        }

        return Vector256.Sum(acc);
    }

    // Single-subset (mode 6) decode-free SSE. Endpoints are already From7-expanded (8-bit) for the one subset
    // (alpha included — mode 6 carries a real alpha pair). Each pixel reconstructs as one lerp per channel; the
    // per-channel lerp lo*(64-w)+hi*w is exactly a u8*s8 adjacent-pair multiply-add, so it fuses to one
    // PMADDUBSW on x86 / vmull+vmlal on ARM (the [64-w,w] weight operand is built once and reused across all
    // four channels), with a scalar fallback. Bit-identical to DecodeMode6+SsdRgba (swap-invariant SSE).
    internal static long SseSingleSubsetRgba(
        int loR,
        int loG,
        int loB,
        int loA,
        int hiR,
        int hiG,
        int hiB,
        int hiA,
        scoped ReadOnlySpan<int> wt,
        scoped ReadOnlySpan<int> sel,
        scoped ReadOnlySpan<ColorRgba> px)
    {
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        if (Ssse3.IsSupported)
            LerpSingleSubsetX86(loR, loG, loB, loA, hiR, hiG, hiB, hiA, wt, sel, dec);
        else if (AdvSimd.Arm64.IsSupported)
            LerpSingleSubsetArm(loR, loG, loB, loA, hiR, hiG, hiB, hiA, wt, sel, dec);
        else
        {
            ref var d = ref MemoryMarshal.GetReference(dec);
            ref var se = ref MemoryMarshal.GetReference(sel);
            ref var wtab = ref MemoryMarshal.GetReference(wt);
            for (var i = 0; i < 16; i++)
            {
                var w = Unsafe.Add(ref wtab, Unsafe.Add(ref se, i));
                Unsafe.Add(ref d, i) = new(
                    (byte)LerpC(loR, hiR, w),
                    (byte)LerpC(loG, hiG, w),
                    (byte)LerpC(loB, hiB, w),
                    (byte)LerpC(loA, hiA, w));
            }
        }

        return SsdRgba(px, dec);
    }

    // x86 PMADDUBSW lerp: a-operand per channel is the constant pair [lo,hi,lo,hi,...]; the shared b-operand is
    // [64-w0,w0,64-w1,w1,...] from the selectors. PMADDUBSW(a,b) = lo*(64-w)+hi*w per pair, then (+32)>>6.
    // Four channel results are PUNPCK-woven into RGBA and stored straight into dec (AoS) for SsdRgba.
    static void LerpSingleSubsetX86(
        int loR,
        int loG,
        int loB,
        int loA,
        int hiR,
        int hiG,
        int hiB,
        int hiA,
        scoped ReadOnlySpan<int> wt,
        scoped ReadOnlySpan<int> sel,
        scoped Span<ColorRgba> dec)
    {
        ref var se = ref MemoryMarshal.GetReference(sel);
        ref var wtab = ref MemoryMarshal.GetReference(wt);
        var db = MemoryMarshal.AsBytes(dec);
        var aR = Vector128.Create((ushort)((uint)hiR << 8 | (uint)loR)).AsByte();
        var aG = Vector128.Create((ushort)((uint)hiG << 8 | (uint)loG)).AsByte();
        var aB = Vector128.Create((ushort)((uint)hiB << 8 | (uint)loB)).AsByte();
        var aA = Vector128.Create((ushort)((uint)hiA << 8 | (uint)loA)).AsByte();
        var c64 = Vector128.Create((byte)64);
        var c32 = Vector128.Create((short)32);
        for (var o = 0; o < 16; o += 8)
        {
            var wv = Vector128.Create(
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 1)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 2)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 3)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 4)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 5)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 6)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 7)),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);

            var b = Sse2.UnpackLow(c64 - wv, wv).AsSByte(); // [64-w0,w0,64-w1,w1,...]
            var rR = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aR, b) + c32, 6);
            var rG = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aG, b) + c32, 6);
            var rB = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aB, b) + c32, 6);
            var rA = Vector128.ShiftRightArithmetic(Ssse3.MultiplyAddAdjacent(aA, b) + c32, 6);
            WeaveStoreX86(rR, rG, rB, rA, db, o);
        }
    }

    // PACKUSWB the four channel results to bytes, then PUNPCK-weave R,G,B,A into RGBA AoS and store 8 px.
    static void WeaveStoreX86(Vector128<short> rR, Vector128<short> rG, Vector128<short> rB, Vector128<short> rA, scoped Span<byte> db, int o)
    {
        var rg = Sse2.UnpackLow(Sse2.PackUnsignedSaturate(rR, rR), Sse2.PackUnsignedSaturate(rG, rG));
        var ba = Sse2.UnpackLow(Sse2.PackUnsignedSaturate(rB, rB), Sse2.PackUnsignedSaturate(rA, rA));
        Sse2.UnpackLow(rg.AsUInt16(), ba.AsUInt16()).AsByte().CopyTo(db.Slice(o * 4));
        Sse2.UnpackHigh(rg.AsUInt16(), ba.AsUInt16()).AsByte().CopyTo(db.Slice((o + 4) * 4));
    }

    // ARM lerp: per channel lo*(64-w)+hi*w via vmull (lo*inv) + vmlal (+= hi*w) into u16, then (+32)>>6 and
    // xtn-narrow to bytes; the four channel bytes are ZIP-woven (vzip) into RGBA and stored into dec (AoS).
    static void LerpSingleSubsetArm(
        int loR,
        int loG,
        int loB,
        int loA,
        int hiR,
        int hiG,
        int hiB,
        int hiA,
        scoped ReadOnlySpan<int> wt,
        scoped ReadOnlySpan<int> sel,
        scoped Span<ColorRgba> dec)
    {
        ref var se = ref MemoryMarshal.GetReference(sel);
        ref var wtab = ref MemoryMarshal.GetReference(wt);
        var db = MemoryMarshal.AsBytes(dec);
        Vector64<byte> loRv = Vector64.Create((byte)loR), hiRv = Vector64.Create((byte)hiR);
        Vector64<byte> loGv = Vector64.Create((byte)loG), hiGv = Vector64.Create((byte)hiG);
        Vector64<byte> loBv = Vector64.Create((byte)loB), hiBv = Vector64.Create((byte)hiB);
        Vector64<byte> loAv = Vector64.Create((byte)loA), hiAv = Vector64.Create((byte)hiA);
        var c64 = Vector64.Create((byte)64);
        var c32 = Vector128.Create((ushort)32);
        for (var o = 0; o < 16; o += 8)
        {
            var wv = Vector64.Create(
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 1)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 2)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 3)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 4)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 5)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 6)),
                (byte)Unsafe.Add(ref wtab, Unsafe.Add(ref se, o + 7)));

            var invv = AdvSimd.Subtract(c64, wv);
            var rR = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(loRv, invv), hiRv, wv), c32), 6);
            var rG = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(loGv, invv), hiGv, wv), c32), 6);
            var rB = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(loBv, invv), hiBv, wv), c32), 6);
            var rA = AdvSimd.ShiftRightLogical(AdvSimd.Add(AdvSimd.MultiplyWideningLowerAndAdd(AdvSimd.MultiplyWideningLower(loAv, invv), hiAv, wv), c32), 6);
            Vector64<byte> pR = AdvSimd.ExtractNarrowingLower(rR), pG = AdvSimd.ExtractNarrowingLower(rG);
            Vector64<byte> pB = AdvSimd.ExtractNarrowingLower(rB), pA = AdvSimd.ExtractNarrowingLower(rA);
            WeaveStoreArm(pR, pG, pB, pA, db, o);
        }
    }

    // xtn-narrowed channel bytes (R,G,B,A; 8 px each) ZIP-woven (vzip) into RGBA AoS and stored as 4x 2-px halves.
    static void WeaveStoreArm(Vector64<byte> pR, Vector64<byte> pG, Vector64<byte> pB, Vector64<byte> pA, scoped Span<byte> db, int o)
    {
        Vector64<byte> rgLo = AdvSimd.Arm64.ZipLow(pR, pG), rgHi = AdvSimd.Arm64.ZipHigh(pR, pG);
        Vector64<byte> baLo = AdvSimd.Arm64.ZipLow(pB, pA), baHi = AdvSimd.Arm64.ZipHigh(pB, pA);
        AdvSimd.Arm64.ZipLow(rgLo.AsUInt16(), baLo.AsUInt16()).AsByte().CopyTo(db.Slice(o * 4));
        AdvSimd.Arm64.ZipHigh(rgLo.AsUInt16(), baLo.AsUInt16()).AsByte().CopyTo(db.Slice((o + 2) * 4));
        AdvSimd.Arm64.ZipLow(rgHi.AsUInt16(), baHi.AsUInt16()).AsByte().CopyTo(db.Slice((o + 4) * 4));
        AdvSimd.Arm64.ZipHigh(rgHi.AsUInt16(), baHi.AsUInt16()).AsByte().CopyTo(db.Slice((o + 6) * 4));
    }

    // Analytical 2-subset partition selection (modes 1/3): split the pixels by block-axis sign into a 16-bit
    // mask, then choose the canned partition with the smallest inversion-aware Hamming distance (ties -> lowest
    // index). The 64-entry scan fuses popcount with a packed (dist<<8)|idx horizontal-min: x86 uses PSHUFB
    // nibble-popcount + PHMINPOSUW (min value AND its lane in one op); ARM uses vcnt + vpaddl + vminv; scalar
    // otherwise. The packed key makes the min select the lowest index on ties, so it's byte-identical to the
    // scalar argmin.
    internal static int SelectPartition2(scoped ReadOnlySpan<ColorRgba> px,
        int meanR,
        int meanG,
        int meanB,
        float axR,
        float axG,
        float axB)
    {
        var desired = 0;
        ref var p0 = ref MemoryMarshal.GetReference(px);
        for (var i = 0; i < 16; i++)
        {
            ref readonly var q = ref Unsafe.Add(ref p0, i);
            float r = q.R - meanR, g = q.G - meanG, b = q.B - meanB;
            var subset = r * axR + g * axG + b * axB > 0f ? 1 : 0;
            desired |= subset << i;
        }

        var masks = Bc7Tables.Part2Bitmasks;
        if (Sse41.IsSupported) return HammingMinX86(masks, desired);
        if (AdvSimd.Arm64.IsSupported) return HammingMinArm(masks, desired);

        int bestDiff = int.MaxValue, bestPat = 0;
        for (var p = 0; p < 64; p++)
        {
            var diff = BitOperations.PopCount((uint)((masks[p] ^ desired) & 0xFFFF));
            diff = Math.Min(diff, 16 - diff);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestPat = p;
            }
        }

        return bestPat;
    }

    static int HammingMinX86(scoped ReadOnlySpan<ushort> masks, int desired)
    {
        var lut = Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4); // popcount(0..15)
        var lowMask = Vector128.Create((byte)0x0F);
        var ones = Vector128.Create((sbyte)1);
        var desv = Vector128.Create((ushort)desired);
        var c16 = Vector128.Create((ushort)16);
        var ramp = Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7);
        var minKey = Vector128.Create((ushort)0xFFFF);
        for (var c = 0; c < 64; c += 8)
        {
            var x = (Vector128.Create(masks.Slice(c)) ^ desv).AsByte();
            var lo = Ssse3.Shuffle(lut, x & lowMask);
            var hi = Ssse3.Shuffle(lut, Sse2.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & lowMask);
            var d = Ssse3.MultiplyAddAdjacent(lo + hi, ones).AsUInt16(); // popcount per 16-bit lane (0..16)
            var dmin = Sse41.Min(d, c16 - d);
            var key = Vector128.ShiftLeft(dmin, 8) | ramp + Vector128.Create((ushort)c);
            minKey = Sse41.Min(minKey, key);
        }

        return Sse41.MinHorizontal(minKey).ToScalar() & 0xFF; // PHMINPOSUW: low lane = min key
    }

    static int HammingMinArm(scoped ReadOnlySpan<ushort> masks, int desired)
    {
        var desv = Vector128.Create((ushort)desired);
        var c16 = Vector128.Create((ushort)16);
        var ramp = Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7);
        var minKey = Vector128.Create((ushort)0xFFFF);
        for (var c = 0; c < 64; c += 8)
        {
            var x = (Vector128.Create(masks.Slice(c)) ^ desv).AsByte();
            var d = AdvSimd.AddPairwiseWidening(AdvSimd.PopCount(x)); // vcnt + vpaddl.u8 -> popcount per 16-bit lane
            var dmin = AdvSimd.Min(d, AdvSimd.Subtract(c16, d));
            var key = AdvSimd.Or(Vector128.ShiftLeft(dmin, 8), AdvSimd.Add(ramp, Vector128.Create((ushort)c)));
            minKey = AdvSimd.Min(minKey, key);
        }

        return AdvSimd.Arm64.MinAcross(minKey).ToScalar() & 0xFF; // vminv.u16: min across lanes
    }

    // 6-bit endpoint quantization. To6 picks the 6-bit value whose (value,pbit) pair is closest to c8.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To6(int c8, uint pbit)
    {
        var q7 = (c8 * 127 + 127) / 255;
        if ((q7 & 1) != (int)pbit)
        {
            if (c8 * 127 >= 255 * q7) q7 = q7 < 127 ? q7 + 1 : q7 - 1;
            else q7 = q7 > 0 ? q7 - 1 : q7 + 1;
        }

        return q7 >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From6(int v, uint p)
    {
        var t = (uint)v << 1 | p; // 6-bit + pbit -> 7-bit
        t = t << 1 | t >> 6; // -> 8-bit
        return (int)t;
    }

    // 5-bit endpoint quantization (no p-bit) — mode 2.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To5(int c8) => (c8 * 31 + 127) / 255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From5(int v)
    {
        var t = (uint)v;
        t = t << 3 | t >> 2;
        return (int)t;
    }

    // 5-bit endpoint quantization with a p-bit — mode 7 (5-bit value + p-bit = 6-bit precision).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To5(int c8, uint pbit)
    {
        var q6 = (c8 * 63 + 127) / 255;
        if ((q6 & 1) != (int)pbit)
        {
            if (c8 * 63 >= 255 * q6) q6 = q6 < 63 ? q6 + 1 : q6 - 1;
            else q6 = q6 > 0 ? q6 - 1 : q6 + 1;
        }

        return q6 >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From5(int v, uint p)
    {
        var t = (uint)v << 1 | p; // 5-bit + pbit -> 6-bit
        t = t << 2 | t >> 4; // -> 8-bit
        return (int)t;
    }

    // --- Least-squares endpoint refinement (compute_least_squares_endpoints_*). Endpoints returned in [0,255]. ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float ClampF(float v) => v < 0f ? 0f :
        v > 255f ? 255f : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int RoundPos(float c) => (int)(c + 0.5f);

    // Single subset, RGB (all 16 pixels). Used by mode 6.
    static bool ComputeLs3D(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        scoped ReadOnlySpan<float> tab,
        float tr,
        float tg,
        float tb,
        scoped Span<float> xl,
        scoped Span<float> xh)
    {
        Span<float> sums = stackalloc float[6];
        LsAccum3D(w, px, tab, sums);
        float z00 = sums[0], z10 = sums[1], z11 = sums[2], q0r = sums[3], q0g = sums[4], q0b = sums[5];
        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        float q1r = tr - q0r, q1g = tg - q0g, q1b = tb - q0b;
        xh[0] = ClampF(iz00 * q0r + iz01 * q1r);
        xl[0] = ClampF(iz10 * q0r + iz11 * q1r);
        xh[1] = ClampF(iz00 * q0g + iz01 * q1g);
        xl[1] = ClampF(iz10 * q0g + iz11 * q1g);
        xh[2] = ClampF(iz00 * q0b + iz01 * q1b);
        xl[2] = ClampF(iz10 * q0b + iz11 * q1b);
        return true;
    }

    // Single subset, RGBA (all 16 pixels). Used by mode 6 RGBA.
    static bool ComputeLs4D(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        scoped ReadOnlySpan<float> tab,
        float tr,
        float tg,
        float tb,
        float ta,
        scoped Span<float> xl,
        scoped Span<float> xh)
    {
        Span<float> sums = stackalloc float[7];
        LsAccum4D(w, px, tab, sums);
        float z00 = sums[0], z10 = sums[1], z11 = sums[2], q0r = sums[3], q0g = sums[4], q0b = sums[5], q0a = sums[6];
        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        float q1r = tr - q0r, q1g = tg - q0g, q1b = tb - q0b, q1a = ta - q0a;
        xh[0] = ClampF(iz00 * q0r + iz01 * q1r);
        xl[0] = ClampF(iz10 * q0r + iz11 * q1r);
        xh[1] = ClampF(iz00 * q0g + iz01 * q1g);
        xl[1] = ClampF(iz10 * q0g + iz11 * q1g);
        xh[2] = ClampF(iz00 * q0b + iz01 * q1b);
        xl[2] = ClampF(iz10 * q0b + iz11 * q1b);
        xh[3] = ClampF(iz00 * q0a + iz01 * q1a);
        xl[3] = ClampF(iz10 * q0a + iz11 * q1a);
        return true;
    }

    // Single channel (all 16 pixels). Used by modes 4/5 for the scalar (rotated) plane.
    static bool ComputeLs1D(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        int comp,
        scoped ReadOnlySpan<float> tab,
        float t,
        out float xl,
        out float xh)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0 = 0;
        ref var wr = ref MemoryMarshal.GetReference(w);
        ref var pr = ref MemoryMarshal.GetReference(px);
        ref var tabr = ref MemoryMarshal.GetReference(tab);
        for (var i = 0; i < 16; i++)
        {
            var o = Unsafe.Add(ref wr, i) * 4;
            z00 += Unsafe.Add(ref tabr, o);
            z10 += Unsafe.Add(ref tabr, o + 1);
            z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly var q = ref Unsafe.Add(ref pr, i);
            var c = comp == 0 ? q.R :
                comp == 1 ? q.G :
                comp == 2 ? q.B : q.A;

            q0 += Unsafe.Add(ref tabr, o + 3) * c;
        }

        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f)
        {
            xl = xh = 0;
            return false;
        }

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        var q1 = t - q0;
        xh = ClampF(iz00 * q0 + iz01 * q1);
        xl = ClampF(iz10 * q0 + iz11 * q1);
        return true;
    }

    // Per-subset RGB (pixels with subsetMask bit i == subset). Used by modes 0/1/2/3.
    static bool ComputeLs3DSub(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        int subsetMask,
        int subset,
        scoped ReadOnlySpan<float> tab,
        float tr,
        float tg,
        float tb,
        scoped Span<float> xl,
        scoped Span<float> xh)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0r = 0, q0g = 0, q0b = 0;
        ref var wr = ref MemoryMarshal.GetReference(w);
        ref var pr = ref MemoryMarshal.GetReference(px);
        ref var tabr = ref MemoryMarshal.GetReference(tab);
        for (var i = 0; i < 16; i++)
        {
            if ((subsetMask >> i & 1) != subset) continue;

            var o = Unsafe.Add(ref wr, i) * 4;
            z00 += Unsafe.Add(ref tabr, o);
            z10 += Unsafe.Add(ref tabr, o + 1);
            z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly var q = ref Unsafe.Add(ref pr, i);
            var ww = Unsafe.Add(ref tabr, o + 3);
            q0r += ww * q.R;
            q0g += ww * q.G;
            q0b += ww * q.B;
        }

        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        float q1r = tr - q0r, q1g = tg - q0g, q1b = tb - q0b;
        xh[0] = ClampF(iz00 * q0r + iz01 * q1r);
        xl[0] = ClampF(iz10 * q0r + iz11 * q1r);
        xh[1] = ClampF(iz00 * q0g + iz01 * q1g);
        xl[1] = ClampF(iz10 * q0g + iz11 * q1g);
        xh[2] = ClampF(iz00 * q0b + iz01 * q1b);
        xl[2] = ClampF(iz10 * q0b + iz11 * q1b);
        return true;
    }

    // --- p-bit optimization (determine_unique_pbits / determine_shared_pbits). xl/xh in [0,1]; endpoints out are comp_bits-bit. ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Clampi(int v, int lo, int hi) => v < lo ? lo :
        v > hi ? hi : v;

    static void DetermineUniquePbits(int totalComps,
        int compBits,
        scoped ReadOnlySpan<float> xl,
        scoped ReadOnlySpan<float> xh,
        scoped Span<int> minC,
        scoped Span<int> maxC,
        scoped Span<uint> pbits)
    {
        int totalBits = compBits + 1, iscalep = (1 << totalBits) - 1;
        float scalep = iscalep;
        float bestErr0 = 1e9f, bestErr1 = 1e9f;
        Span<int> xmin = stackalloc int[4];
        Span<int> xmax = stackalloc int[4];
        for (var p = 0; p < 2; p++)
        {
            for (var c = 0; c < 4; c++)
            {
                xmin[c] = Clampi((int)((xl[c] * scalep - p) / 2f + 0.5f) * 2 + p, p, iscalep - 1 + p);
                xmax[c] = Clampi((int)((xh[c] * scalep - p) / 2f + 0.5f) * 2 + p, p, iscalep - 1 + p);
            }

            float err0 = 0, err1 = 0;
            for (var i = 0; i < totalComps; i++)
            {
                var sl = xmin[i] << 8 - totalBits;
                sl |= sl >> totalBits;
                var sh = xmax[i] << 8 - totalBits;
                sh |= sh >> totalBits;
                float d0 = sl - xl[i] * 255f, d1 = sh - xh[i] * 255f;
                err0 += d0 * d0;
                err1 += d1 * d1;
            }

            if (err0 < bestErr0)
            {
                bestErr0 = err0;
                pbits[0] = (uint)p;
                for (var j = 0; j < 4; j++) minC[j] = xmin[j] >> 1;
            }

            if (err1 < bestErr1)
            {
                bestErr1 = err1;
                pbits[1] = (uint)p;
                for (var j = 0; j < 4; j++) maxC[j] = xmax[j] >> 1;
            }
        }
    }

    static void DetermineSharedPbits(int totalComps,
        int compBits,
        scoped ReadOnlySpan<float> xl,
        scoped ReadOnlySpan<float> xh,
        scoped Span<int> minC,
        scoped Span<int> maxC,
        scoped Span<uint> pbits)
    {
        int totalBits = compBits + 1, iscalep = (1 << totalBits) - 1;
        float scalep = iscalep;
        var bestErr = 1e9f;
        Span<int> xmin = stackalloc int[4];
        Span<int> xmax = stackalloc int[4];
        for (var p = 0; p < 2; p++)
        {
            for (var c = 0; c < 4; c++)
            {
                xmin[c] = Clampi((int)((xl[c] * scalep - p) / 2f + 0.5f) * 2 + p, p, iscalep - 1 + p);
                xmax[c] = Clampi((int)((xh[c] * scalep - p) / 2f + 0.5f) * 2 + p, p, iscalep - 1 + p);
            }

            float err = 0;
            for (var i = 0; i < totalComps; i++)
            {
                var sl = xmin[i] << 8 - totalBits;
                sl |= sl >> totalBits;
                var sh = xmax[i] << 8 - totalBits;
                sh |= sh >> totalBits;
                float a = sl / 255f - xl[i], b = sh / 255f - xh[i];
                err += a * a + b * b;
            }

            if (err < bestErr)
            {
                bestErr = err;
                pbits[0] = (uint)p;
                pbits[1] = (uint)p;
                for (var j = 0; j < 4; j++)
                {
                    minC[j] = xmin[j] >> 1;
                    maxC[j] = xmax[j] >> 1;
                }
            }
        }
    }

    // Per-subset RGB keyed on a 3-subset partition (Partition3[pat3*16+i] == subset). Used by modes 0/2.
    static bool ComputeLs3DSubP(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        int pat3,
        int subset,
        scoped ReadOnlySpan<float> tab,
        float tr,
        float tg,
        float tb,
        scoped Span<float> xl,
        scoped Span<float> xh)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0r = 0, q0g = 0, q0b = 0;
        ref var wr = ref MemoryMarshal.GetReference(w);
        ref var pr = ref MemoryMarshal.GetReference(px);
        ref var tabr = ref MemoryMarshal.GetReference(tab);
        ref var p3r = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition3);
        for (var i = 0; i < 16; i++)
        {
            if (Unsafe.Add(ref p3r, pat3 * 16 + i) != subset) continue;

            var o = Unsafe.Add(ref wr, i) * 4;
            z00 += Unsafe.Add(ref tabr, o);
            z10 += Unsafe.Add(ref tabr, o + 1);
            z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly var q = ref Unsafe.Add(ref pr, i);
            var ww = Unsafe.Add(ref tabr, o + 3);
            q0r += ww * q.R;
            q0g += ww * q.G;
            q0b += ww * q.B;
        }

        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        float q1r = tr - q0r, q1g = tg - q0g, q1b = tb - q0b;
        xh[0] = ClampF(iz00 * q0r + iz01 * q1r);
        xl[0] = ClampF(iz10 * q0r + iz11 * q1r);
        xh[1] = ClampF(iz00 * q0g + iz01 * q1g);
        xl[1] = ClampF(iz10 * q0g + iz11 * q1g);
        xh[2] = ClampF(iz00 * q0b + iz01 * q1b);
        xl[2] = ClampF(iz10 * q0b + iz11 * q1b);
        return true;
    }

    // Per-subset RGBA (pixels with patBits bit i == subset). Used by mode 7.
    static bool ComputeLs4DSub(scoped ReadOnlySpan<int> w,
        scoped ReadOnlySpan<ColorRgba> px,
        int patBits,
        int subset,
        scoped ReadOnlySpan<float> tab,
        float tr,
        float tg,
        float tb,
        float ta,
        scoped Span<float> xl,
        scoped Span<float> xh)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0r = 0, q0g = 0, q0b = 0, q0a = 0;
        ref var wr = ref MemoryMarshal.GetReference(w);
        ref var pr = ref MemoryMarshal.GetReference(px);
        ref var tabr = ref MemoryMarshal.GetReference(tab);
        for (var i = 0; i < 16; i++)
        {
            if ((patBits >> i & 1) != subset) continue;

            var o = Unsafe.Add(ref wr, i) * 4;
            z00 += Unsafe.Add(ref tabr, o);
            z10 += Unsafe.Add(ref tabr, o + 1);
            z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly var q = ref Unsafe.Add(ref pr, i);
            var ww = Unsafe.Add(ref tabr, o + 3);
            q0r += ww * q.R;
            q0g += ww * q.G;
            q0b += ww * q.B;
            q0a += ww * q.A;
        }

        var det = z00 * z11 - z10 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;

        det = 1f / det;
        float iz00 = z11 * det, iz01 = -z10 * det, iz10 = -z10 * det, iz11 = z00 * det;
        float q1r = tr - q0r, q1g = tg - q0g, q1b = tb - q0b, q1a = ta - q0a;
        xh[0] = ClampF(iz00 * q0r + iz01 * q1r);
        xl[0] = ClampF(iz10 * q0r + iz11 * q1r);
        xh[1] = ClampF(iz00 * q0g + iz01 * q1g);
        xl[1] = ClampF(iz10 * q0g + iz11 * q1g);
        xh[2] = ClampF(iz00 * q0b + iz01 * q1b);
        xl[2] = ClampF(iz10 * q0b + iz11 * q1b);
        xh[3] = ClampF(iz00 * q0a + iz01 * q1a);
        xl[3] = ClampF(iz10 * q0a + iz11 * q1a);
        return true;
    }

    // 4-bit endpoint quantization with a shared/unique p-bit — mode 0 (4-bit value + p-bit = 5-bit precision).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To4(int c8, uint pbit)
    {
        var q5 = (c8 * 31 + 127) / 255;
        if ((q5 & 1) != (int)pbit)
        {
            if (c8 * 31 >= 255 * q5) q5 = q5 < 31 ? q5 + 1 : q5 - 1;
            else q5 = q5 > 0 ? q5 - 1 : q5 + 1;
        }

        return q5 >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From4(int v, uint p)
    {
        var t = (uint)v << 1 | p; // 4-bit + pbit -> 5-bit
        t = t << 3 | t >> 2; // -> 8-bit
        return (int)t;
    }

    // No-p-bit endpoint quantizers/expanders for the dual-plane modes (4: 5-bit RGB / 6-bit A; 5: 7-bit RGB).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To7(int c8) => (c8 * 127 + 127) / 255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int To6(int c8) => (c8 * 63 + 127) / 255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From7(int v)
    {
        var t = (uint)v;
        return (int)(t << 1 | t >> 6);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int From6(int v)
    {
        var t = (uint)v;
        return (int)(t << 2 | t >> 4);
    }

    // Lossless solid-block encoder: bc7f::pack_mode5_solid. Writes a mode-5 block whose 7-7-7 RGB endpoints
    // (from the brute-forced optimal table) plus 8-bit alpha endpoints, with every index fixed to 1, reproduce
    // the solid colour exactly. Byte-identical to upstream.
    static void PackMode5Solid(scoped Span<byte> block, ColorRgba c)
    {
        var er = Bc7Tables.Mode5OptimalEndpoints[c.R];
        var eg = Bc7Tables.Mode5OptimalEndpoints[c.G];
        var eb = Bc7Tables.Mode5OptimalEndpoints[c.B];
        uint lr = er.Lo, hr = er.Hi, lg = eg.Lo, hg = eg.Hi, lb = eb.Lo, hb = eb.Hi, a = c.A;

        block[0] = 0b00100000; // mode 5, rotation 0

        // 58 endpoint bits: lr|hr<<7|lg<<14|hg<<21|lb<<28|hb<<35 (6x7) then a<<42 and a<<50 (two 8-bit alpha copies)
        var x = lr | (ulong)hr << 7 | (ulong)lg << 14 | (ulong)hg << 21
            | (ulong)lb << 28 | (ulong)hb << 35 | (ulong)a << 42 | (ulong)a << 50;

        block[1] = (byte)x;
        block[2] = (byte)(x >> 8);
        block[3] = (byte)(x >> 16);
        block[4] = (byte)(x >> 24);
        block[5] = (byte)(x >> 32);
        block[6] = (byte)(x >> 40);
        block[7] = (byte)(x >> 48);
        x >>= 56; // 2 bits left over (top of the second alpha endpoint)

        // index tail: 0xac|leftover, then 0xaa,0xaa,0xaa,0,0,0,0 — encodes index 1 for every colour and alpha pixel
        block[8] = (byte)(0xac | (byte)x);
        block[9] = 0xaa;
        block[10] = 0xaa;
        block[11] = 0xaa;
        block[12] = 0;
        block[13] = 0;
        block[14] = 0;
        block[15] = 0;
    }

    /// <summary>
    ///     Predicted SSE of a candidate mode/subset WITHOUT encoding it — the step^2/12 uniform
    ///     quantization-noise model. Faithful port of analytical_quant_est_sse.
    ///     eLevels   = endpoint quant levels (256 = 8-bit/no quant, 128 = 7-bit, 64 = 6-bit, 32 = 5-bit)
    ///     wLevels   = index/weight levels (2,4,8,16 for 1/2/3/4-bit indices)
    ///     spans     = per-channel axis extent (max-min projection)
    ///     endpointWeightScale = p-bit discount (Unique/SharedPbitDiscount) or 1.0
    /// </summary>
    static float AnalyticalQuantEstSse(int eLevels,
        int wLevels,
        int numChans,
        scoped ReadOnlySpan<int> spans,
        scoped ReadOnlySpan<float> spanWeights,
        float endpointWeightScale,
        int numPixels)
    {
        var dep = 1.0f / (eLevels - 1); // endpoint quant step
        var dw = 1.0f / (wLevels - 1); // weight quant step

        float n = wLevels;
        var abSum = (2.0f * n - 1.0f) / (3.0f * (n - 1.0f));

        var pixelSse = eLevels == 256
            ? 0.0f
            : dep * dep * (1.0f / 12.0f * abSum * (255.0f * 255.0f)) * numChans * endpointWeightScale;

        var k = dw * dw * (1.0f / 12.0f);
        for (var i = 0; i < numChans; i++)
            pixelSse += k * (spans[i] * spans[i]) * (spanWeights.IsEmpty ? 1.0f : spanWeights[i]);

        return pixelSse * numPixels;
    }

    // Single-channel variant (the dual-plane scalar/rotated plane). numChans == 1.
    static float AnalyticalQuantEstSse(int eLevels,
        int wLevels,
        int span,
        float spanWeight,
        float endpointWeightScale,
        int numPixels)
    {
        var dep = 1.0f / (eLevels - 1);
        var dw = 1.0f / (wLevels - 1);
        float n = wLevels;
        var abSum = (2.0f * n - 1.0f) / (3.0f * (n - 1.0f));
        var pixelSse = eLevels == 256
            ? 0.0f
            : dep * dep * (1.0f / 12.0f * abSum * (255.0f * 255.0f)) * endpointWeightScale;

        pixelSse += dw * dw * (1.0f / 12.0f) * (span * span) * spanWeight;
        return pixelSse * numPixels;
    }
}