// Bc7Simd.cs — portable, lane-widened helpers for the per-block hot paths, replacing scalar loops in the
// analytical brains. All are INTEGER, so every tier is bit-identical to the scalar fallback (reduction
// order is irrelevant for sum/min/max). Tier dispatch uses Vector*.IsHardwareAccelerated, which the JIT
// treats as a compile-time constant and folds. The unused tiers are eliminated, so the branches cost
// nothing. Each tier is also exposed internally so that any tier can be exercised on a single
// machine, not only the one the JIT selects for the current CPU.
using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;

namespace Bcn.Bc7;

internal static partial class Bc7Block
{
    // PEXT/PDEP (Bmi2) are MICROCODED on pre-Zen3 AMD — Bulldozer (family 0x15), Jaguar (0x16), Zen1/Zen2
    // (0x17), Hygon (0x18) — running ~18-300x slower than a scalar shift/or, whereas Zen3+ (0x19+) and all
    // BMI2-capable Intel implement them in hardware (~3-cycle, 1/clock). A PEXT/PDEP path (e.g. the bitstream
    // writer) must therefore gate on THIS flag, not on Bmi2.IsSupported, or it is a performance regression
    // on those parts. The check uses CPU vendor+family via CPUID; setting BC7_DISABLE_PDEP=1 forces the
    // scalar fallback.
    internal static readonly bool Bmi2FastPdep = Bmi2.IsSupported && !IsSlowPdepCpu()
        && !(System.Environment.GetEnvironmentVariable("BC7_DISABLE_PDEP") is "1" or "true" or "TRUE");

    private static bool IsSlowPdepCpu()
    {
        if (!X86Base.IsSupported) return false;                                   // non-x86: flag is moot (Bmi2 already false)
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);                             // leaf 0: vendor string in EBX,EDX,ECX
        if (!(ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163)) return false;  // not "AuthenticAMD" => HW PEXT/PDEP
        var (eax, _, _, _) = X86Base.CpuId(1, 0);                                 // leaf 1: family/model/stepping
        int baseFamily = (eax >> 8) & 0xF;
        int family = baseFamily == 0xF ? baseFamily + ((eax >> 20) & 0xFF) : baseFamily;
        return family < 0x19;                                                     // below Zen3 (0x19) => microcoded PEXT/PDEP
    }

    // ---- axis projection (bc7f bc7_proj_minmax_indices): weights pre-<<4 so index is in the low nibble ----
    private static void ProjMinMax3(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, out int loIdx, out int hiIdx)
    {
        if (Vector256.IsHardwareAccelerated) ProjMinMax3V256(px, wr, wg, wb, out loIdx, out hiIdx);
        else if (Vector128.IsHardwareAccelerated) ProjMinMax3V128(px, wr, wg, wb, out loIdx, out hiIdx);
        else ProjMinMax3Scalar(px, wr, wg, wb, out loIdx, out hiIdx);
    }
    internal static void ProjMinMax3Scalar(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, out int loIdx, out int hiIdx)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (int i = 0; i < 16; i++)
        {
            int dot = px[i].R * wr + px[i].G * wg + px[i].B * wb + i;
            if (dot < lo) lo = dot; if (dot > hi) hi = dot;
        }
        loIdx = lo & 0xF; hiIdx = hi & 0xF;
    }
    internal static void ProjMinMax3V256(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, out int loIdx, out int hiIdx)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> wrv = Vector256.Create(wr), wgv = Vector256.Create(wg), wbv = Vector256.Create(wb);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0);
        Vector256<int> d0 = (p0 & mask).AsInt32() * wrv + (Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() * wgv
                          + (Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() * wbv + Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<uint> p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<int> d1 = (p1 & mask).AsInt32() * wrv + (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() * wgv
                          + (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() * wbv + Vector256.Create(8, 9, 10, 11, 12, 13, 14, 15);
        loIdx = HMin(Vector256.Min(d0, d1)) & 0xF; hiIdx = HMax(Vector256.Max(d0, d1)) & 0xF;
    }
    internal static void ProjMinMax3V128(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, out int loIdx, out int hiIdx)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<int> wrv = Vector128.Create(wr), wgv = Vector128.Create(wg), wbv = Vector128.Create(wb);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> vmin = default, vmax = default;
        for (uint g = 0; g < 16; g += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pr, g);
            Vector128<int> d = (p & mask).AsInt32() * wrv + (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32() * wgv
                             + (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32() * wbv
                             + Vector128.Create((int)g, (int)g + 1, (int)g + 2, (int)g + 3);
            vmin = g == 0 ? d : Vector128.Min(vmin, d);
            vmax = g == 0 ? d : Vector128.Max(vmax, d);
        }
        loIdx = HMin(vmin) & 0xF; hiIdx = HMax(vmax) & 0xF;
    }

    private static void ProjMinMax4(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, int wa, out int loIdx, out int hiIdx)
    {
        if (Vector256.IsHardwareAccelerated) ProjMinMax4V256(px, wr, wg, wb, wa, out loIdx, out hiIdx);
        else if (Vector128.IsHardwareAccelerated) ProjMinMax4V128(px, wr, wg, wb, wa, out loIdx, out hiIdx);
        else ProjMinMax4Scalar(px, wr, wg, wb, wa, out loIdx, out hiIdx);
    }
    internal static void ProjMinMax4Scalar(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, int wa, out int loIdx, out int hiIdx)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (int i = 0; i < 16; i++)
        {
            int dot = px[i].R * wr + px[i].G * wg + px[i].B * wb + px[i].A * wa + i;
            if (dot < lo) lo = dot; if (dot > hi) hi = dot;
        }
        loIdx = lo & 0xF; hiIdx = hi & 0xF;
    }
    internal static void ProjMinMax4V256(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, int wa, out int loIdx, out int hiIdx)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> wrv = Vector256.Create(wr), wgv = Vector256.Create(wg), wbv = Vector256.Create(wb), wav = Vector256.Create(wa);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0);
        Vector256<int> d0 = (p0 & mask).AsInt32() * wrv + (Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() * wgv
                          + (Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() * wbv + Vector256.ShiftRightLogical(p0, 24).AsInt32() * wav
                          + Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<uint> p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<int> d1 = (p1 & mask).AsInt32() * wrv + (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() * wgv
                          + (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() * wbv + Vector256.ShiftRightLogical(p1, 24).AsInt32() * wav
                          + Vector256.Create(8, 9, 10, 11, 12, 13, 14, 15);
        loIdx = HMin(Vector256.Min(d0, d1)) & 0xF; hiIdx = HMax(Vector256.Max(d0, d1)) & 0xF;
    }
    internal static void ProjMinMax4V128(scoped ReadOnlySpan<ColorRgba> px, int wr, int wg, int wb, int wa, out int loIdx, out int hiIdx)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<int> wrv = Vector128.Create(wr), wgv = Vector128.Create(wg), wbv = Vector128.Create(wb), wav = Vector128.Create(wa);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> vmin = default, vmax = default;
        for (uint g = 0; g < 16; g += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pr, g);
            Vector128<int> d = (p & mask).AsInt32() * wrv + (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32() * wgv
                             + (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32() * wbv + Vector128.ShiftRightLogical(p, 24).AsInt32() * wav
                             + Vector128.Create((int)g, (int)g + 1, (int)g + 2, (int)g + 3);
            vmin = g == 0 ? d : Vector128.Min(vmin, d);
            vmax = g == 0 ? d : Vector128.Max(vmax, d);
        }
        loIdx = HMin(vmin) & 0xF; hiIdx = HMax(vmax) & 0xF;
    }

    // ---- integer covariance (mean-centred), the heaviest per-block loop. icov rows: 0,1,2 / 1,3,4 / 2,4,5 ----
    // ---- Block statistics: per-channel sum + min + max over the 16 pixels. Integer (order-independent) => byte-identical across tiers.
    // RGB layout:  s[0..2]=sum RGB,   s[3..5]=min RGB,   s[6..8]=max RGB.
    // RGBA layout: s[0..3]=sum RGBA,  s[4..7]=min RGBA,  s[8..11]=max RGBA.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HMin(Vector128<int> m){ if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.MinAcross(m).ToScalar(); m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1))); m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))); return m.ToScalar(); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HMax(Vector128<int> m){ if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.MaxAcross(m).ToScalar(); m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1))); m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))); return m.ToScalar(); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HMin(Vector256<int> v) => HMin(Vector128.Min(v.GetLower(), v.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HMax(Vector256<int> v) => HMax(Vector128.Max(v.GetLower(), v.GetUpper()));

    internal static void BlockStatsRgb(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        if (Vector256.IsHardwareAccelerated) BlockStatsRgbV256(px, s);
        else if (Vector128.IsHardwareAccelerated) BlockStatsRgbV128(px, s);
        else BlockStatsRgbScalar(px, s);
    }
    internal static void BlockStatsRgbScalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        int totR = 0, totG = 0, totB = 0, minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        ref ColorRgba pr = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba q = ref Unsafe.Add(ref pr, i); int r = q.R, g = q.G, b = q.B;
            totR += r; totG += g; totB += b;
            if (r < minR) minR = r; if (g < minG) minG = g; if (b < minB) minB = b;
            if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (b > maxB) maxB = b;
        }
        s[0] = totR; s[1] = totG; s[2] = totB; s[3] = minR; s[4] = minG; s[5] = minB; s[6] = maxR; s[7] = maxG; s[8] = maxB;
    }
    internal static void BlockStatsRgbV256(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> sR = default, sG = default, sB = default;
        Vector256<int> nR = Vector256.Create(255), nG = Vector256.Create(255), nB = Vector256.Create(255), xR = default, xG = default, xB = default;
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
            sR += R; sG += G; sB += B; nR = Vector256.Min(nR, R); nG = Vector256.Min(nG, G); nB = Vector256.Min(nB, B); xR = Vector256.Max(xR, R); xG = Vector256.Max(xG, G); xB = Vector256.Max(xB, B);
        }
        s[0] = Vector256.Sum(sR); s[1] = Vector256.Sum(sG); s[2] = Vector256.Sum(sB); s[3] = HMin(nR); s[4] = HMin(nG); s[5] = HMin(nB); s[6] = HMax(xR); s[7] = HMax(xG); s[8] = HMax(xB);
    }
    internal static void BlockStatsRgbV128(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> sR = default, sG = default, sB = default;
        Vector128<int> nR = Vector128.Create(255), nG = Vector128.Create(255), nB = Vector128.Create(255), xR = default, xG = default, xB = default;
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32();
            sR += R; sG += G; sB += B; nR = Vector128.Min(nR, R); nG = Vector128.Min(nG, G); nB = Vector128.Min(nB, B); xR = Vector128.Max(xR, R); xG = Vector128.Max(xG, G); xB = Vector128.Max(xB, B);
        }
        s[0] = Vector128.Sum(sR); s[1] = Vector128.Sum(sG); s[2] = Vector128.Sum(sB); s[3] = HMin(nR); s[4] = HMin(nG); s[5] = HMin(nB); s[6] = HMax(xR); s[7] = HMax(xG); s[8] = HMax(xB);
    }

    internal static void BlockStatsRgba(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        if (Vector256.IsHardwareAccelerated) BlockStatsRgbaV256(px, s);
        else if (Vector128.IsHardwareAccelerated) BlockStatsRgbaV128(px, s);
        else BlockStatsRgbaScalar(px, s);
    }
    internal static void BlockStatsRgbaScalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        int tR = 0, tG = 0, tB = 0, tA = 0, nR = 255, nG = 255, nB = 255, nA = 255, xR = 0, xG = 0, xB = 0, xA = 0;
        ref ColorRgba pr = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba q = ref Unsafe.Add(ref pr, i); int r = q.R, g = q.G, b = q.B, a = q.A;
            tR += r; tG += g; tB += b; tA += a;
            if (r < nR) nR = r; if (g < nG) nG = g; if (b < nB) nB = b; if (a < nA) nA = a;
            if (r > xR) xR = r; if (g > xG) xG = g; if (b > xB) xB = b; if (a > xA) xA = a;
        }
        s[0] = tR; s[1] = tG; s[2] = tB; s[3] = tA; s[4] = nR; s[5] = nG; s[6] = nB; s[7] = nA; s[8] = xR; s[9] = xG; s[10] = xB; s[11] = xA;
    }
    internal static void BlockStatsRgbaV256(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> sR = default, sG = default, sB = default, sA = default;
        Vector256<int> nR = Vector256.Create(255), nG = Vector256.Create(255), nB = Vector256.Create(255), nA = Vector256.Create(255), xR = default, xG = default, xB = default, xA = default;
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector256.ShiftRightLogical(p, 24).AsInt32();
            sR += R; sG += G; sB += B; sA += A;
            nR = Vector256.Min(nR, R); nG = Vector256.Min(nG, G); nB = Vector256.Min(nB, B); nA = Vector256.Min(nA, A);
            xR = Vector256.Max(xR, R); xG = Vector256.Max(xG, G); xB = Vector256.Max(xB, B); xA = Vector256.Max(xA, A);
        }
        s[0] = Vector256.Sum(sR); s[1] = Vector256.Sum(sG); s[2] = Vector256.Sum(sB); s[3] = Vector256.Sum(sA);
        s[4] = HMin(nR); s[5] = HMin(nG); s[6] = HMin(nB); s[7] = HMin(nA); s[8] = HMax(xR); s[9] = HMax(xG); s[10] = HMax(xB); s[11] = HMax(xA);
    }
    internal static void BlockStatsRgbaV128(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> s)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> sR = default, sG = default, sB = default, sA = default;
        Vector128<int> nR = Vector128.Create(255), nG = Vector128.Create(255), nB = Vector128.Create(255), nA = Vector128.Create(255), xR = default, xG = default, xB = default, xA = default;
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector128.ShiftRightLogical(p, 24).AsInt32();
            sR += R; sG += G; sB += B; sA += A;
            nR = Vector128.Min(nR, R); nG = Vector128.Min(nG, G); nB = Vector128.Min(nB, B); nA = Vector128.Min(nA, A);
            xR = Vector128.Max(xR, R); xG = Vector128.Max(xG, G); xB = Vector128.Max(xB, B); xA = Vector128.Max(xA, A);
        }
        s[0] = Vector128.Sum(sR); s[1] = Vector128.Sum(sG); s[2] = Vector128.Sum(sB); s[3] = Vector128.Sum(sA);
        s[4] = HMin(nR); s[5] = HMin(nG); s[6] = HMin(nB); s[7] = HMin(nA); s[8] = HMax(xR); s[9] = HMax(xG); s[10] = HMax(xB); s[11] = HMax(xA);
    }

    private static void CovarianceRgb(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
#if NET8_0_OR_GREATER
        if (Avx512BW.IsSupported && Vector512.IsHardwareAccelerated) CovarianceRgbV512(px, meanR, meanG, meanB, icov);
        else
#endif
        if (Avx2.IsSupported) CovarianceRgbAvx2(px, meanR, meanG, meanB, icov);
        else if (Vector256.IsHardwareAccelerated) CovarianceRgbV256(px, meanR, meanG, meanB, icov);
        else if (Vector128.IsHardwareAccelerated) CovarianceRgbV128(px, meanR, meanG, meanB, icov);
        else CovarianceRgbScalar(px, meanR, meanG, meanB, icov);
    }
    internal static void CovarianceRgbScalar(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
        int c0 = 0, c1 = 0, c2 = 0, c3 = 0, c4 = 0, c5 = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i].R - meanR, g = px[i].G - meanG, b = px[i].B - meanB;
            c0 += r * r; c1 += r * g; c2 += r * b; c3 += g * g; c4 += g * b; c5 += b * b;
        }
        icov[0] = c0; icov[1] = c1; icov[2] = c2; icov[3] = c3; icov[4] = c4; icov[5] = c5;
    }
    // AVX2 fast path: 6 covariance reductions via vpmaddwd. Pack is lane-crossing, but covariance is an
    // order-independent reduction with the SAME permutation across channels => byte-identical.
    internal static void CovarianceRgbAvx2(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0), p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<short> dr = Avx2.PackSignedSaturate((p0 & mask).AsInt32() - mr, (p1 & mask).AsInt32() - mr);
        Vector256<short> dg = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() - mg, (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() - mg);
        Vector256<short> db = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() - mb, (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() - mb);
        icov[0] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, dr));
        icov[1] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, dg));
        icov[2] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, db));
        icov[3] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dg, dg));
        icov[4] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dg, db));
        icov[5] = Vector256.Sum(Avx2.MultiplyAddAdjacent(db, db));
    }
#if NET8_0_OR_GREATER
    // AVX-512: pair the 6 cross-products two-per-VPMADDWD-512 (3 madds vs 6), split each result's two
    // 256-halves and reduce. Integer-exact -> byte-identical to CovarianceRgbAvx2.
    internal static void CovarianceRgbV512(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0), p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<short> dr = Avx2.PackSignedSaturate((p0 & mask).AsInt32() - mr, (p1 & mask).AsInt32() - mr);
        Vector256<short> dg = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() - mg, (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() - mg);
        Vector256<short> db = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() - mb, (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() - mb);
        Vector512<short> drdg = Vector512.Create(dr, dg);
        Vector512<int> m1 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dr, dr), drdg);                       // rr | rg
        Vector512<int> m2 = Avx512BW.MultiplyAddAdjacent(drdg, Vector512.Create(db, dg));                        // rb | gg
        Vector512<int> m3 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dg, db), Vector512.Create(db, db));    // gb | bb
        icov[0] = Vector256.Sum(m1.GetLower()); icov[1] = Vector256.Sum(m1.GetUpper());
        icov[2] = Vector256.Sum(m2.GetLower()); icov[3] = Vector256.Sum(m2.GetUpper());
        icov[4] = Vector256.Sum(m3.GetLower()); icov[5] = Vector256.Sum(m3.GetUpper());
    }
#endif
    internal static void CovarianceRgbV256(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default;
        for (uint g = 0; g < 16; g += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pr, g);
            Vector256<int> dr = (p & mask).AsInt32() - mr;
            Vector256<int> dg = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32() - mg;
            Vector256<int> db = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32() - mb;
            a0 += dr * dr; a1 += dr * dg; a2 += dr * db; a3 += dg * dg; a4 += dg * db; a5 += db * db;
        }
        icov[0] = Vector256.Sum(a0); icov[1] = Vector256.Sum(a1); icov[2] = Vector256.Sum(a2);
        icov[3] = Vector256.Sum(a3); icov[4] = Vector256.Sum(a4); icov[5] = Vector256.Sum(a5);
    }
    internal static void CovarianceRgbV128(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<int> mr = Vector128.Create(meanR), mg = Vector128.Create(meanG), mb = Vector128.Create(meanB);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default;
        for (uint g = 0; g < 16; g += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pr, g);
            Vector128<int> dr = (p & mask).AsInt32() - mr;
            Vector128<int> dg = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32() - mg;
            Vector128<int> db = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32() - mb;
            a0 += dr * dr; a1 += dr * dg; a2 += dr * db; a3 += dg * dg; a4 += dg * db; a5 += db * db;
        }
        icov[0] = Vector128.Sum(a0); icov[1] = Vector128.Sum(a1); icov[2] = Vector128.Sum(a2);
        icov[3] = Vector128.Sum(a3); icov[4] = Vector128.Sum(a4); icov[5] = Vector128.Sum(a5);
    }

    private static void CovarianceRgba(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
#if NET8_0_OR_GREATER
        if (Avx512BW.IsSupported && Vector512.IsHardwareAccelerated) CovarianceRgbaV512(px, meanR, meanG, meanB, meanA, icov);
        else
#endif
        if (Avx2.IsSupported) CovarianceRgbaAvx2(px, meanR, meanG, meanB, meanA, icov);
        else if (Vector256.IsHardwareAccelerated) CovarianceRgbaV256(px, meanR, meanG, meanB, meanA, icov);
        else if (Vector128.IsHardwareAccelerated) CovarianceRgbaV128(px, meanR, meanG, meanB, meanA, icov);
        else CovarianceRgbaScalar(px, meanR, meanG, meanB, meanA, icov);
    }
    internal static void CovarianceRgbaScalar(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
        int c0=0,c1=0,c2=0,c3=0,c4=0,c5=0,c6=0,c7=0,c8=0,c9=0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i].R - meanR, g = px[i].G - meanG, b = px[i].B - meanB, a = px[i].A - meanA;
            c0 += r*r; c1 += r*g; c2 += r*b; c3 += r*a; c4 += g*g; c5 += g*b; c6 += g*a; c7 += b*b; c8 += b*a; c9 += a*a;
        }
        icov[0]=c0; icov[1]=c1; icov[2]=c2; icov[3]=c3; icov[4]=c4; icov[5]=c5; icov[6]=c6; icov[7]=c7; icov[8]=c8; icov[9]=c9;
    }
    internal static void CovarianceRgbaAvx2(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB), ma = Vector256.Create(meanA);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0), p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<short> dr = Avx2.PackSignedSaturate((p0 & mask).AsInt32() - mr, (p1 & mask).AsInt32() - mr);
        Vector256<short> dg = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() - mg, (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() - mg);
        Vector256<short> db = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() - mb, (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() - mb);
        Vector256<short> da = Avx2.PackSignedSaturate(Vector256.ShiftRightLogical(p0, 24).AsInt32() - ma, Vector256.ShiftRightLogical(p1, 24).AsInt32() - ma);
        icov[0] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, dr));
        icov[1] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, dg));
        icov[2] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, db));
        icov[3] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dr, da));
        icov[4] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dg, dg));
        icov[5] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dg, db));
        icov[6] = Vector256.Sum(Avx2.MultiplyAddAdjacent(dg, da));
        icov[7] = Vector256.Sum(Avx2.MultiplyAddAdjacent(db, db));
        icov[8] = Vector256.Sum(Avx2.MultiplyAddAdjacent(db, da));
        icov[9] = Vector256.Sum(Avx2.MultiplyAddAdjacent(da, da));
    }
#if NET8_0_OR_GREATER
    // AVX-512: 10 cross-products paired into 5 VPMADDWD-512. Integer-exact -> byte-identical to
    // CovarianceRgbaAvx2.
    internal static void CovarianceRgbaV512(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB), ma = Vector256.Create(meanA);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> p0 = Vector256.LoadUnsafe(ref pr, 0), p1 = Vector256.LoadUnsafe(ref pr, 8);
        Vector256<short> dr = Avx2.PackSignedSaturate((p0 & mask).AsInt32() - mr, (p1 & mask).AsInt32() - mr);
        Vector256<short> dg = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 8) & mask).AsInt32() - mg, (Vector256.ShiftRightLogical(p1, 8) & mask).AsInt32() - mg);
        Vector256<short> db = Avx2.PackSignedSaturate((Vector256.ShiftRightLogical(p0, 16) & mask).AsInt32() - mb, (Vector256.ShiftRightLogical(p1, 16) & mask).AsInt32() - mb);
        Vector256<short> da = Avx2.PackSignedSaturate(Vector256.ShiftRightLogical(p0, 24).AsInt32() - ma, Vector256.ShiftRightLogical(p1, 24).AsInt32() - ma);
        Vector512<int> m1 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dr, dr), Vector512.Create(dr, dg)); // rr | rg
        Vector512<int> m2 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dr, dr), Vector512.Create(db, da)); // rb | ra
        Vector512<int> m3 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dg, dg), Vector512.Create(dg, db)); // gg | gb
        Vector512<int> m4 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(dg, db), Vector512.Create(da, db)); // ga | bb
        Vector512<int> m5 = Avx512BW.MultiplyAddAdjacent(Vector512.Create(db, da), Vector512.Create(da, da)); // ba | aa
        icov[0] = Vector256.Sum(m1.GetLower()); icov[1] = Vector256.Sum(m1.GetUpper());
        icov[2] = Vector256.Sum(m2.GetLower()); icov[3] = Vector256.Sum(m2.GetUpper());
        icov[4] = Vector256.Sum(m3.GetLower()); icov[5] = Vector256.Sum(m3.GetUpper());
        icov[6] = Vector256.Sum(m4.GetLower()); icov[7] = Vector256.Sum(m4.GetUpper());
        icov[8] = Vector256.Sum(m5.GetLower()); icov[9] = Vector256.Sum(m5.GetUpper());
    }
#endif
    internal static void CovarianceRgbaV256(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<int> mr = Vector256.Create(meanR), mg = Vector256.Create(meanG), mb = Vector256.Create(meanB), ma = Vector256.Create(meanA);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> a0=default,a1=default,a2=default,a3=default,a4=default,a5=default,a6=default,a7=default,a8=default,a9=default;
        for (uint g = 0; g < 16; g += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pr, g);
            Vector256<int> dr = (p & mask).AsInt32() - mr;
            Vector256<int> dg = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32() - mg;
            Vector256<int> db = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32() - mb;
            Vector256<int> da = Vector256.ShiftRightLogical(p, 24).AsInt32() - ma;
            a0 += dr*dr; a1 += dr*dg; a2 += dr*db; a3 += dr*da; a4 += dg*dg; a5 += dg*db; a6 += dg*da; a7 += db*db; a8 += db*da; a9 += da*da;
        }
        icov[0]=Vector256.Sum(a0); icov[1]=Vector256.Sum(a1); icov[2]=Vector256.Sum(a2); icov[3]=Vector256.Sum(a3); icov[4]=Vector256.Sum(a4);
        icov[5]=Vector256.Sum(a5); icov[6]=Vector256.Sum(a6); icov[7]=Vector256.Sum(a7); icov[8]=Vector256.Sum(a8); icov[9]=Vector256.Sum(a9);
    }
    internal static void CovarianceRgbaV128(scoped ReadOnlySpan<ColorRgba> px, int meanR, int meanG, int meanB, int meanA, scoped Span<int> icov)
    {
        ref uint pr = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<int> mr = Vector128.Create(meanR), mg = Vector128.Create(meanG), mb = Vector128.Create(meanB), ma = Vector128.Create(meanA);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> a0=default,a1=default,a2=default,a3=default,a4=default,a5=default,a6=default,a7=default,a8=default,a9=default;
        for (uint g = 0; g < 16; g += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pr, g);
            Vector128<int> dr = (p & mask).AsInt32() - mr;
            Vector128<int> dg = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32() - mg;
            Vector128<int> db = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32() - mb;
            Vector128<int> da = Vector128.ShiftRightLogical(p, 24).AsInt32() - ma;
            a0 += dr*dr; a1 += dr*dg; a2 += dr*db; a3 += dr*da; a4 += dg*dg; a5 += dg*db; a6 += dg*da; a7 += db*db; a8 += db*da; a9 += da*da;
        }
        icov[0]=Vector128.Sum(a0); icov[1]=Vector128.Sum(a1); icov[2]=Vector128.Sum(a2); icov[3]=Vector128.Sum(a3); icov[4]=Vector128.Sum(a4);
        icov[5]=Vector128.Sum(a5); icov[6]=Vector128.Sum(a6); icov[7]=Vector128.Sum(a7); icov[8]=Vector128.Sum(a8); icov[9]=Vector128.Sum(a9);
    }

    // ---- sum of squared RGBA differences (decoder error metric, hot in HighQuality) ---------------
    // Returns Sum over 16 pixels of (dR^2+dG^2+dB^2+dA^2). Max value = 16*4*255^2 = 4,161,600, well within
    // int32, so the integer accumulation is exact and bit-identical to the scalar long-based reference.
    // Same packed-uint deinterleave approach as the covariance kernel. Dispatcher + tiers (internal so all
    // tiers can be force-tested; only the JIT-selected one runs in production).
    // ===========================================================================================
    // Vectorized selector eval: w[i] = clamp( trunc( fma(dot_i, f, 0.5) ), 0, mx ),  dot_i = R*dr+G*dg+B*db(+A*da) + aofs.
    // FMA matches the scalar two-rounding bit-for-bit here, so this stays byte-exact.
    // MANUAL per-ISA paths (NOT portable Vector256.FusedMultiplyAdd, which is scalar-gated-off on ARM):
    //   x86  -> 256-bit, 8 px/iter, Fma.MultiplyAdd + truncating vcvttps2dq
    //   ARM  -> 128-bit, 4 px/iter, AdvSimd.FusedMultiplyAdd + FCVTZS (NEON is 128-bit; no wider vector)
    //   else -> scalar two-rounding (identical to reference C++).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelRgb(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int aofs, float f, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelRgbV512(px, w, dr, dg, db, aofs, f, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelRgbX86(px, w, dr, dg, db, aofs, f, mx);
        else if (AdvSimd.IsSupported) EvalSelRgbArm(px, w, dr, dg, db, aofs, f, mx);
        else EvalSelRgbScalar(px, w, dr, dg, db, aofs, f, mx);
    }
    internal static void EvalSelRgbScalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int aofs, float f, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w);
        for (nuint i = 0; i < 16; i++)
        {
            ref readonly ColorRgba q = ref Unsafe.Add(ref p, i);
            int sel = (int)((q.R * dr + q.G * dg + q.B * db + aofs) * f + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelRgbX86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> drv = Vector256.Create(dr), dgv = Vector256.Create(dg), dbv = Vector256.Create(db), ofsv = Vector256.Create(aofs);
        Vector256<float> fv = Vector256.Create(f), halfv = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector256<int> dot = R * drv + G * dgv + B * dbv + ofsv;
            Vector256<float> selF = Fma.MultiplyAdd(Vector256.ConvertToSingle(dot), fv, halfv);
            Vector256<int> sel = Avx.ConvertToVector256Int32WithTruncation(selF);
            sel = Vector256.Min(Vector256.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelRgbArm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> drv = Vector128.Create(dr), dgv = Vector128.Create(dg), dbv = Vector128.Create(db), ofsv = Vector128.Create(aofs);
        Vector128<float> fv = Vector128.Create(f), halfv = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector128<int> dot = R * drv + G * dgv + B * dbv + ofsv;
            Vector128<float> selF = AdvSimd.FusedMultiplyAdd(halfv, Vector128.ConvertToSingle(dot), fv);
            Vector128<int> sel = AdvSimd.ConvertToInt32RoundToZero(selF);
            sel = Vector128.Min(Vector128.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelRgba(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int da, int aofs, float f, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelRgbaV512(px, w, dr, dg, db, da, aofs, f, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelRgbaX86(px, w, dr, dg, db, da, aofs, f, mx);
        else if (AdvSimd.IsSupported) EvalSelRgbaArm(px, w, dr, dg, db, da, aofs, f, mx);
        else EvalSelRgbaScalar(px, w, dr, dg, db, da, aofs, f, mx);
    }
    internal static void EvalSelRgbaScalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int da, int aofs, float f, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w);
        for (nuint i = 0; i < 16; i++)
        {
            ref readonly ColorRgba q = ref Unsafe.Add(ref p, i);
            int sel = (int)((q.R * dr + q.G * dg + q.B * db + q.A * da + aofs) * f + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelRgbaX86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int da, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> drv = Vector256.Create(dr), dgv = Vector256.Create(dg), dbv = Vector256.Create(db), dav = Vector256.Create(da), ofsv = Vector256.Create(aofs);
        Vector256<float> fv = Vector256.Create(f), halfv = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector256.ShiftRightLogical(p, 24).AsInt32();
            Vector256<int> dot = R * drv + G * dgv + B * dbv + A * dav + ofsv;
            Vector256<float> selF = Fma.MultiplyAdd(Vector256.ConvertToSingle(dot), fv, halfv);
            Vector256<int> sel = Avx.ConvertToVector256Int32WithTruncation(selF);
            sel = Vector256.Min(Vector256.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelRgbaArm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int da, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> drv = Vector128.Create(dr), dgv = Vector128.Create(dg), dbv = Vector128.Create(db), dav = Vector128.Create(da), ofsv = Vector128.Create(aofs);
        Vector128<float> fv = Vector128.Create(f), halfv = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector128.ShiftRightLogical(p, 24).AsInt32();
            Vector128<int> dot = R * drv + G * dgv + B * dbv + A * dav + ofsv;
            Vector128<float> selF = AdvSimd.FusedMultiplyAdd(halfv, Vector128.ConvertToSingle(dot), fv);
            Vector128<int> sel = AdvSimd.ConvertToInt32RoundToZero(selF);
            sel = Vector128.Min(Vector128.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    // Single channel (alpha plane): w[i] = clamp( trunc( fma(A-la, f, 0.5) ), 0, mx ).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelA(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, float f, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelAV512(px, w, la, f, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelAX86(px, w, la, f, mx);
        else if (AdvSimd.IsSupported) EvalSelAArm(px, w, la, f, mx);
        else EvalSelAScalar(px, w, la, f, mx);
    }
    internal static void EvalSelAScalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, float f, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w);
        for (nuint i = 0; i < 16; i++)
        {
            int sel = (int)((Unsafe.Add(ref p, i).A - la) * f + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelAX86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector256<int> lav = Vector256.Create(la);
        Vector256<float> fv = Vector256.Create(f), halfv = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> dot = Vector256.ShiftRightLogical(p, 24).AsInt32() - lav;
            Vector256<float> selF = Fma.MultiplyAdd(Vector256.ConvertToSingle(dot), fv, halfv);
            Vector256<int> sel = Avx.ConvertToVector256Int32WithTruncation(selF);
            sel = Vector256.Min(Vector256.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelAArm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        ref int wr = ref MemoryMarshal.GetReference(w);
        Vector128<int> lav = Vector128.Create(la);
        Vector128<float> fv = Vector128.Create(f), halfv = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> dot = Vector128.ShiftRightLogical(p, 24).AsInt32() - lav;
            Vector128<float> selF = AdvSimd.FusedMultiplyAdd(halfv, Vector128.ConvertToSingle(dot), fv);
            Vector128<int> sel = AdvSimd.ConvertToInt32RoundToZero(selF);
            sel = Vector128.Min(Vector128.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    // ===== Multi-subset selector eval (the blend): compute selectors for ALL subsets across the vector, then pick per-pixel. =====
    // Subset chosen per pixel from a 2-way bitmask (modes 1/3/7) or a 3-way Partition3 table (modes 0/2). FMA byte-exact as above.
    // ConditionalSelect(mask,a,b) = mask?a:b ; clamp applied after the pick (monotone per lane, so identical to per-pixel clamp).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelRgb2(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelRgb2V512(px, w, dr, dg, db, sofs, f, subsetMask, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelRgb2X86(px, w, dr, dg, db, sofs, f, subsetMask, mx);
        else if (AdvSimd.IsSupported) EvalSelRgb2Arm(px, w, dr, dg, db, sofs, f, subsetMask, mx);
        else EvalSelRgb2Scalar(px, w, dr, dg, db, sofs, f, subsetMask, mx);
    }
    internal static void EvalSelRgb2Scalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w);
        for (nuint i = 0; i < 16; i++)
        {
            int s = (subsetMask >> (int)i) & 1; ref readonly ColorRgba q = ref Unsafe.Add(ref p, i);
            int sel = (int)((q.R * dr[s] + q.G * dg[s] + q.B * db[s] - sofs[s]) * f[s] + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelRgb2X86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> dr0 = Vector256.Create(dr[0]), dr1 = Vector256.Create(dr[1]), dg0 = Vector256.Create(dg[0]), dg1 = Vector256.Create(dg[1]), db0 = Vector256.Create(db[0]), db1 = Vector256.Create(db[1]);
        Vector256<int> so0 = Vector256.Create(sofs[0]), so1 = Vector256.Create(sofs[1]);
        Vector256<float> f0 = Vector256.Create(f[0]), f1 = Vector256.Create(f[1]), half = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx), bitsel = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector256<int> s0 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0, half));
            Vector256<int> s1 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1, half));
            Vector256<int> m = Vector256.Equals(Vector256.Create((subsetMask >> (int)o) & 0xFF) & bitsel, zero);
            Vector256<int> sel = Vector256.Min(Vector256.Max(Vector256.ConditionalSelect(m, s0, s1), zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelRgb2Arm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> dr0 = Vector128.Create(dr[0]), dr1 = Vector128.Create(dr[1]), dg0 = Vector128.Create(dg[0]), dg1 = Vector128.Create(dg[1]), db0 = Vector128.Create(db[0]), db1 = Vector128.Create(db[1]);
        Vector128<int> so0 = Vector128.Create(sofs[0]), so1 = Vector128.Create(sofs[1]);
        Vector128<float> f0 = Vector128.Create(f[0]), f1 = Vector128.Create(f[1]), half = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx), bitsel = Vector128.Create(1, 2, 4, 8);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector128<int> s0 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0));
            Vector128<int> s1 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1));
            Vector128<int> m = Vector128.Equals(Vector128.Create((subsetMask >> (int)o) & 0xF) & bitsel, zero);
            Vector128<int> sel = Vector128.Min(Vector128.Max(Vector128.ConditionalSelect(m, s0, s1), zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelRgba2(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> da, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelRgba2V512(px, w, dr, dg, db, da, sofs, f, subsetMask, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelRgba2X86(px, w, dr, dg, db, da, sofs, f, subsetMask, mx);
        else if (AdvSimd.IsSupported) EvalSelRgba2Arm(px, w, dr, dg, db, da, sofs, f, subsetMask, mx);
        else EvalSelRgba2Scalar(px, w, dr, dg, db, da, sofs, f, subsetMask, mx);
    }
    internal static void EvalSelRgba2Scalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> da, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w);
        for (nuint i = 0; i < 16; i++)
        {
            int s = (subsetMask >> (int)i) & 1; ref readonly ColorRgba q = ref Unsafe.Add(ref p, i);
            int sel = (int)((q.R * dr[s] + q.G * dg[s] + q.B * db[s] + q.A * da[s] - sofs[s]) * f[s] + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelRgba2X86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> da, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> dr0 = Vector256.Create(dr[0]), dr1 = Vector256.Create(dr[1]), dg0 = Vector256.Create(dg[0]), dg1 = Vector256.Create(dg[1]), db0 = Vector256.Create(db[0]), db1 = Vector256.Create(db[1]), da0 = Vector256.Create(da[0]), da1 = Vector256.Create(da[1]);
        Vector256<int> so0 = Vector256.Create(sofs[0]), so1 = Vector256.Create(sofs[1]);
        Vector256<float> f0 = Vector256.Create(f[0]), f1 = Vector256.Create(f[1]), half = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx), bitsel = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector256.ShiftRightLogical(p, 24).AsInt32();
            Vector256<int> s0 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr0 + G * dg0 + B * db0 + A * da0 - so0), f0, half));
            Vector256<int> s1 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr1 + G * dg1 + B * db1 + A * da1 - so1), f1, half));
            Vector256<int> m = Vector256.Equals(Vector256.Create((subsetMask >> (int)o) & 0xFF) & bitsel, zero);
            Vector256<int> sel = Vector256.Min(Vector256.Max(Vector256.ConditionalSelect(m, s0, s1), zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelRgba2Arm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> da, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> dr0 = Vector128.Create(dr[0]), dr1 = Vector128.Create(dr[1]), dg0 = Vector128.Create(dg[0]), dg1 = Vector128.Create(dg[1]), db0 = Vector128.Create(db[0]), db1 = Vector128.Create(db[1]), da0 = Vector128.Create(da[0]), da1 = Vector128.Create(da[1]);
        Vector128<int> so0 = Vector128.Create(sofs[0]), so1 = Vector128.Create(sofs[1]);
        Vector128<float> f0 = Vector128.Create(f[0]), f1 = Vector128.Create(f[1]), half = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx), bitsel = Vector128.Create(1, 2, 4, 8);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector128.ShiftRightLogical(p, 24).AsInt32();
            Vector128<int> s0 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr0 + G * dg0 + B * db0 + A * da0 - so0), f0));
            Vector128<int> s1 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr1 + G * dg1 + B * db1 + A * da1 - so1), f1));
            Vector128<int> m = Vector128.Equals(Vector128.Create((subsetMask >> (int)o) & 0xF) & bitsel, zero);
            Vector128<int> sel = Vector128.Min(Vector128.Max(Vector128.ConditionalSelect(m, s0, s1), zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EvalSelRgb3(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, scoped ReadOnlySpan<byte> part, int mx)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) EvalSelRgb3V512(px, w, dr, dg, db, sofs, f, part, mx);
        else
#endif
        if (Fma.IsSupported) EvalSelRgb3X86(px, w, dr, dg, db, sofs, f, part, mx);
        else if (AdvSimd.IsSupported) EvalSelRgb3Arm(px, w, dr, dg, db, sofs, f, part, mx);
        else EvalSelRgb3Scalar(px, w, dr, dg, db, sofs, f, part, mx);
    }
    internal static void EvalSelRgb3Scalar(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, scoped ReadOnlySpan<byte> part, int mx)
    {
        ref ColorRgba p = ref MemoryMarshal.GetReference(px); ref int wr = ref MemoryMarshal.GetReference(w); ref byte pt = ref MemoryMarshal.GetReference(part);
        for (nuint i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref pt, i); ref readonly ColorRgba q = ref Unsafe.Add(ref p, i);
            int sel = (int)((q.R * dr[s] + q.G * dg[s] + q.B * db[s] - sofs[s]) * f[s] + 0.5f);
            if ((uint)sel > (uint)mx) sel = (~sel >> 31) & mx;
            Unsafe.Add(ref wr, i) = sel;
        }
    }
    internal static void EvalSelRgb3X86(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, scoped ReadOnlySpan<byte> part, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w); ref byte pt = ref MemoryMarshal.GetReference(part);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<int> dr0 = Vector256.Create(dr[0]), dr1 = Vector256.Create(dr[1]), dr2 = Vector256.Create(dr[2]), dg0 = Vector256.Create(dg[0]), dg1 = Vector256.Create(dg[1]), dg2 = Vector256.Create(dg[2]), db0 = Vector256.Create(db[0]), db1 = Vector256.Create(db[1]), db2 = Vector256.Create(db[2]);
        Vector256<int> so0 = Vector256.Create(sofs[0]), so1 = Vector256.Create(sofs[1]), so2 = Vector256.Create(sofs[2]);
        Vector256<float> f0 = Vector256.Create(f[0]), f1 = Vector256.Create(f[1]), f2 = Vector256.Create(f[2]), half = Vector256.Create(0.5f);
        Vector256<int> zero = Vector256<int>.Zero, mxv = Vector256.Create(mx), one = Vector256.Create(1), two = Vector256.Create(2);
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<int> R = (p & mask).AsInt32(), G = (Vector256.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector256.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector256<int> s0 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0, half));
            Vector256<int> s1 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1, half));
            Vector256<int> s2 = Avx.ConvertToVector256Int32WithTruncation(Fma.MultiplyAdd(Vector256.ConvertToSingle(R * dr2 + G * dg2 + B * db2 - so2), f2, half));
            Vector256<int> idx = Vector256.Create(Unsafe.Add(ref pt, o), Unsafe.Add(ref pt, o + 1), Unsafe.Add(ref pt, o + 2), Unsafe.Add(ref pt, o + 3), Unsafe.Add(ref pt, o + 4), Unsafe.Add(ref pt, o + 5), Unsafe.Add(ref pt, o + 6), Unsafe.Add(ref pt, o + 7));
            Vector256<int> sel = Vector256.ConditionalSelect(Vector256.Equals(idx, two), s2, Vector256.ConditionalSelect(Vector256.Equals(idx, one), s1, s0));
            sel = Vector256.Min(Vector256.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }
    internal static void EvalSelRgb3Arm(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, scoped ReadOnlySpan<byte> part, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w); ref byte pt = ref MemoryMarshal.GetReference(part);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<int> dr0 = Vector128.Create(dr[0]), dr1 = Vector128.Create(dr[1]), dr2 = Vector128.Create(dr[2]), dg0 = Vector128.Create(dg[0]), dg1 = Vector128.Create(dg[1]), dg2 = Vector128.Create(dg[2]), db0 = Vector128.Create(db[0]), db1 = Vector128.Create(db[1]), db2 = Vector128.Create(db[2]);
        Vector128<int> so0 = Vector128.Create(sofs[0]), so1 = Vector128.Create(sofs[1]), so2 = Vector128.Create(sofs[2]);
        Vector128<float> f0 = Vector128.Create(f[0]), f1 = Vector128.Create(f[1]), f2 = Vector128.Create(f[2]), half = Vector128.Create(0.5f);
        Vector128<int> zero = Vector128<int>.Zero, mxv = Vector128.Create(mx), one = Vector128.Create(1), two = Vector128.Create(2);
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<int> R = (p & mask).AsInt32(), G = (Vector128.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector128.ShiftRightLogical(p, 16) & mask).AsInt32();
            Vector128<int> s0 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0));
            Vector128<int> s1 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1));
            Vector128<int> s2 = AdvSimd.ConvertToInt32RoundToZero(AdvSimd.FusedMultiplyAdd(half, Vector128.ConvertToSingle(R * dr2 + G * dg2 + B * db2 - so2), f2));
            Vector128<int> idx = Vector128.Create(Unsafe.Add(ref pt, o), Unsafe.Add(ref pt, o + 1), Unsafe.Add(ref pt, o + 2), Unsafe.Add(ref pt, o + 3));
            Vector128<int> sel = Vector128.ConditionalSelect(Vector128.Equals(idx, two), s2, Vector128.ConditionalSelect(Vector128.Equals(idx, one), s1, s0));
            sel = Vector128.Min(Vector128.Max(sel, zero), mxv);
            sel.StoreUnsafe(ref wr, o);
        }
    }

    // ===== LS accumulation (approximately-exact): vectorized emulated-gather + FMA. Reorders the FP reduction =====
    // (lanes summed horizontally vs sequential) -> sub-quantization drift (~5e-5 on 0..255). Scalar tier stays exact.
    // RGB sums:  s[0..2]=z00,z10,z11  s[3..5]=q0r,q0g,q0b.   RGBA adds s[6]=q0a.
    internal static void LsAccum3D(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        // VPERMPS gathers all 16 table rows by index in a few register permutes instead of 64 scalar loads;
        // byte-identical to the scalar gather. AVX2 8-wide (PermuteVar8x32) is the primary path; AVX-512 is the net8+ tier.
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) LsAccum3DV512Permute(w, px, Bc7Tables.LsColsFor(tab.Length), s);
        else
#endif
        if (Avx2.IsSupported && Fma.IsSupported) LsAccum3DAvx2Permute(w, px, Bc7Tables.LsColsFor(tab.Length), s);
        else if (Fma.IsSupported) LsAccum3DX86(w, px, tab, s);   // Fma without Avx2 (old AMD) — emulated gather
        else if (AdvSimd.IsSupported) LsAccum3DArm(w, px, tab, s);   // ARM (NEON) emulated gather
        else LsAccum3DScalar(w, px, tab, s);
    }
#if NET8_0_OR_GREATER
    // AVX-512: one PermuteVar16x32 (VPERMPS zmm) gathers all 16 rows by index (no blend needed, even for 16-row
    // tables). Byte-identical to LsAccum3DX86: the result is split to 256-bit halves and reduced in the shipped
    // order (pair lane j with j+8; q-terms via FMA(ww_hi, R_hi, ww_lo*R_lo)).
    internal static void LsAccum3DV512Permute(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, in Bc7Tables.LsCols lc, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector512<int> wv = Vector512.LoadUnsafe(ref wr, 0);
        Vector512<float> pz0 = Avx512F.PermuteVar16x32(lc.V0, wv), pz1 = Avx512F.PermuteVar16x32(lc.V1, wv), pz2 = Avx512F.PermuteVar16x32(lc.V2, wv), pww = Avx512F.PermuteVar16x32(lc.Vw, wv);
        s[0] = Vector256.Sum(pz0.GetLower() + pz0.GetUpper());
        s[1] = Vector256.Sum(pz1.GetLower() + pz1.GetUpper());
        s[2] = Vector256.Sum(pz2.GetLower() + pz2.GetUpper());
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<float> Rf = Vector512.ConvertToSingle((p & mask).AsInt32()), Gf = Vector512.ConvertToSingle((Vector512.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector512.ConvertToSingle((Vector512.ShiftRightLogical(p, 16) & mask).AsInt32());
        Vector256<float> wwl = pww.GetLower(), wwh = pww.GetUpper();
        s[3] = Vector256.Sum(Fma.MultiplyAdd(wwh, Rf.GetUpper(), wwl * Rf.GetLower()));
        s[4] = Vector256.Sum(Fma.MultiplyAdd(wwh, Gf.GetUpper(), wwl * Gf.GetLower()));
        s[5] = Vector256.Sum(Fma.MultiplyAdd(wwh, Bf.GetUpper(), wwl * Bf.GetLower()));
    }
    // Byte-identical to LsAccum3DX86: only the gather changes (Vector256.Create(64 scalar loads) -> PermuteVar8x32).
    // The 256-bit accumulate/reduce order is unchanged, so the float result matches bit-for-bit.
#endif
    internal static void LsAccum3DAvx2Permute(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, in Bc7Tables.LsCols lc, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu); Vector256<int> seven = Vector256.Create(7);
        Vector256<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default;
        bool wide = lc.Wide;
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<int> wv = Vector256.LoadUnsafe(ref wr, o);
            Vector256<float> Z0, Z1, Z2, WW;
            if (wide)
            {
                Vector256<float> hi = Avx2.CompareGreaterThan(wv, seven).AsSingle(); // MSB set where w >= 8
                Z0 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C0, wv), Avx2.PermuteVar8x32(lc.C0h, wv), hi);
                Z1 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C1, wv), Avx2.PermuteVar8x32(lc.C1h, wv), hi);
                Z2 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C2, wv), Avx2.PermuteVar8x32(lc.C2h, wv), hi);
                WW = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.Cw, wv), Avx2.PermuteVar8x32(lc.Cwh, wv), hi);
            }
            else { Z0 = Avx2.PermuteVar8x32(lc.C0, wv); Z1 = Avx2.PermuteVar8x32(lc.C1, wv); Z2 = Avx2.PermuteVar8x32(lc.C2, wv); WW = Avx2.PermuteVar8x32(lc.Cw, wv); }
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<float> Rf = Vector256.ConvertToSingle((p & mask).AsInt32()), Gf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 16) & mask).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = Fma.MultiplyAdd(WW, Rf, qr); qg = Fma.MultiplyAdd(WW, Gf, qg); qb = Fma.MultiplyAdd(WW, Bf, qb);
        }
        s[0] = Vector256.Sum(z0); s[1] = Vector256.Sum(z1); s[2] = Vector256.Sum(z2); s[3] = Vector256.Sum(qr); s[4] = Vector256.Sum(qg); s[5] = Vector256.Sum(qb);
    }
    internal static void LsAccum3DScalar(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0r = 0, q0g = 0, q0b = 0;
        ref int wr = ref MemoryMarshal.GetReference(w); ref ColorRgba pr = ref MemoryMarshal.GetReference(px); ref float tabr = ref MemoryMarshal.GetReference(tab);
        for (int i = 0; i < 16; i++)
        {
            int o = Unsafe.Add(ref wr, i) * 4; z00 += Unsafe.Add(ref tabr, o); z10 += Unsafe.Add(ref tabr, o + 1); z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly ColorRgba q = ref Unsafe.Add(ref pr, i); float ww = Unsafe.Add(ref tabr, o + 3); q0r += ww * q.R; q0g += ww * q.G; q0b += ww * q.B;
        }
        s[0] = z00; s[1] = z10; s[2] = z11; s[3] = q0r; s[4] = q0g; s[5] = q0b;
    }
    internal static void LsAccum3DX86(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w); ref float t = ref MemoryMarshal.GetReference(tab);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default;
        for (nuint o = 0; o < 16; o += 8)
        {
            int o0 = Unsafe.Add(ref wr, o) * 4, o1 = Unsafe.Add(ref wr, o + 1) * 4, o2 = Unsafe.Add(ref wr, o + 2) * 4, o3 = Unsafe.Add(ref wr, o + 3) * 4, o4 = Unsafe.Add(ref wr, o + 4) * 4, o5 = Unsafe.Add(ref wr, o + 5) * 4, o6 = Unsafe.Add(ref wr, o + 6) * 4, o7 = Unsafe.Add(ref wr, o + 7) * 4;
            Vector256<float> Z0 = Vector256.Create(Unsafe.Add(ref t, o0), Unsafe.Add(ref t, o1), Unsafe.Add(ref t, o2), Unsafe.Add(ref t, o3), Unsafe.Add(ref t, o4), Unsafe.Add(ref t, o5), Unsafe.Add(ref t, o6), Unsafe.Add(ref t, o7));
            Vector256<float> Z1 = Vector256.Create(Unsafe.Add(ref t, o0 + 1), Unsafe.Add(ref t, o1 + 1), Unsafe.Add(ref t, o2 + 1), Unsafe.Add(ref t, o3 + 1), Unsafe.Add(ref t, o4 + 1), Unsafe.Add(ref t, o5 + 1), Unsafe.Add(ref t, o6 + 1), Unsafe.Add(ref t, o7 + 1));
            Vector256<float> Z2 = Vector256.Create(Unsafe.Add(ref t, o0 + 2), Unsafe.Add(ref t, o1 + 2), Unsafe.Add(ref t, o2 + 2), Unsafe.Add(ref t, o3 + 2), Unsafe.Add(ref t, o4 + 2), Unsafe.Add(ref t, o5 + 2), Unsafe.Add(ref t, o6 + 2), Unsafe.Add(ref t, o7 + 2));
            Vector256<float> WW = Vector256.Create(Unsafe.Add(ref t, o0 + 3), Unsafe.Add(ref t, o1 + 3), Unsafe.Add(ref t, o2 + 3), Unsafe.Add(ref t, o3 + 3), Unsafe.Add(ref t, o4 + 3), Unsafe.Add(ref t, o5 + 3), Unsafe.Add(ref t, o6 + 3), Unsafe.Add(ref t, o7 + 3));
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<float> Rf = Vector256.ConvertToSingle((p & mask).AsInt32()), Gf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 16) & mask).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = Fma.MultiplyAdd(WW, Rf, qr); qg = Fma.MultiplyAdd(WW, Gf, qg); qb = Fma.MultiplyAdd(WW, Bf, qb);
        }
        s[0] = Vector256.Sum(z0); s[1] = Vector256.Sum(z1); s[2] = Vector256.Sum(z2); s[3] = Vector256.Sum(qr); s[4] = Vector256.Sum(qg); s[5] = Vector256.Sum(qb);
    }
    internal static void LsAccum3DArm(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w); ref float t = ref MemoryMarshal.GetReference(tab);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default;
        for (nuint o = 0; o < 16; o += 4)
        {
            int o0 = Unsafe.Add(ref wr, o) * 4, o1 = Unsafe.Add(ref wr, o + 1) * 4, o2 = Unsafe.Add(ref wr, o + 2) * 4, o3 = Unsafe.Add(ref wr, o + 3) * 4;
            Vector128<float> Z0 = Vector128.Create(Unsafe.Add(ref t, o0), Unsafe.Add(ref t, o1), Unsafe.Add(ref t, o2), Unsafe.Add(ref t, o3));
            Vector128<float> Z1 = Vector128.Create(Unsafe.Add(ref t, o0 + 1), Unsafe.Add(ref t, o1 + 1), Unsafe.Add(ref t, o2 + 1), Unsafe.Add(ref t, o3 + 1));
            Vector128<float> Z2 = Vector128.Create(Unsafe.Add(ref t, o0 + 2), Unsafe.Add(ref t, o1 + 2), Unsafe.Add(ref t, o2 + 2), Unsafe.Add(ref t, o3 + 2));
            Vector128<float> WW = Vector128.Create(Unsafe.Add(ref t, o0 + 3), Unsafe.Add(ref t, o1 + 3), Unsafe.Add(ref t, o2 + 3), Unsafe.Add(ref t, o3 + 3));
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<float> Rf = Vector128.ConvertToSingle((p & mask).AsInt32()), Gf = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(p, 16) & mask).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = AdvSimd.FusedMultiplyAdd(qr, WW, Rf); qg = AdvSimd.FusedMultiplyAdd(qg, WW, Gf); qb = AdvSimd.FusedMultiplyAdd(qb, WW, Bf);
        }
        s[0] = Vector128.Sum(z0); s[1] = Vector128.Sum(z1); s[2] = Vector128.Sum(z2); s[3] = Vector128.Sum(qr); s[4] = Vector128.Sum(qg); s[5] = Vector128.Sum(qb);
    }

    internal static void LsAccum4D(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
#if NET8_0_OR_GREATER
        if (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) LsAccum4DV512Permute(w, px, Bc7Tables.LsColsFor(tab.Length), s);
        else
#endif
        if (Avx2.IsSupported && Fma.IsSupported) LsAccum4DAvx2Permute(w, px, Bc7Tables.LsColsFor(tab.Length), s);
        else if (Fma.IsSupported) LsAccum4DX86(w, px, tab, s);
        else if (AdvSimd.IsSupported) LsAccum4DArm(w, px, tab, s);   // ARM (NEON) emulated gather
        else LsAccum4DScalar(w, px, tab, s);
    }
#if NET8_0_OR_GREATER
    // AVX-512 16-wide permute gather; byte-identical to LsAccum4DX86 (adds the alpha term).
    internal static void LsAccum4DV512Permute(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, in Bc7Tables.LsCols lc, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector512<int> wv = Vector512.LoadUnsafe(ref wr, 0);
        Vector512<float> pz0 = Avx512F.PermuteVar16x32(lc.V0, wv), pz1 = Avx512F.PermuteVar16x32(lc.V1, wv), pz2 = Avx512F.PermuteVar16x32(lc.V2, wv), pww = Avx512F.PermuteVar16x32(lc.Vw, wv);
        s[0] = Vector256.Sum(pz0.GetLower() + pz0.GetUpper());
        s[1] = Vector256.Sum(pz1.GetLower() + pz1.GetUpper());
        s[2] = Vector256.Sum(pz2.GetLower() + pz2.GetUpper());
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<float> Rf = Vector512.ConvertToSingle((p & mask).AsInt32()), Gf = Vector512.ConvertToSingle((Vector512.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector512.ConvertToSingle((Vector512.ShiftRightLogical(p, 16) & mask).AsInt32()), Af = Vector512.ConvertToSingle(Vector512.ShiftRightLogical(p, 24).AsInt32());
        Vector256<float> wwl = pww.GetLower(), wwh = pww.GetUpper();
        s[3] = Vector256.Sum(Fma.MultiplyAdd(wwh, Rf.GetUpper(), wwl * Rf.GetLower()));
        s[4] = Vector256.Sum(Fma.MultiplyAdd(wwh, Gf.GetUpper(), wwl * Gf.GetLower()));
        s[5] = Vector256.Sum(Fma.MultiplyAdd(wwh, Bf.GetUpper(), wwl * Bf.GetLower()));
        s[6] = Vector256.Sum(Fma.MultiplyAdd(wwh, Af.GetUpper(), wwl * Af.GetLower()));
    }
    // Byte-identical to LsAccum4DX86 (same accumulate/reduce; only the gather is VPERMPS). Adds the alpha term.
#endif
    internal static void LsAccum4DAvx2Permute(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, in Bc7Tables.LsCols lc, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu); Vector256<int> seven = Vector256.Create(7);
        Vector256<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default, qa = default;
        bool wide = lc.Wide;
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<int> wv = Vector256.LoadUnsafe(ref wr, o);
            Vector256<float> Z0, Z1, Z2, WW;
            if (wide)
            {
                Vector256<float> hi = Avx2.CompareGreaterThan(wv, seven).AsSingle();
                Z0 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C0, wv), Avx2.PermuteVar8x32(lc.C0h, wv), hi);
                Z1 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C1, wv), Avx2.PermuteVar8x32(lc.C1h, wv), hi);
                Z2 = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.C2, wv), Avx2.PermuteVar8x32(lc.C2h, wv), hi);
                WW = Avx2.BlendVariable(Avx2.PermuteVar8x32(lc.Cw, wv), Avx2.PermuteVar8x32(lc.Cwh, wv), hi);
            }
            else { Z0 = Avx2.PermuteVar8x32(lc.C0, wv); Z1 = Avx2.PermuteVar8x32(lc.C1, wv); Z2 = Avx2.PermuteVar8x32(lc.C2, wv); WW = Avx2.PermuteVar8x32(lc.Cw, wv); }
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<float> Rf = Vector256.ConvertToSingle((p & mask).AsInt32()), Gf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 16) & mask).AsInt32()), Af = Vector256.ConvertToSingle(Vector256.ShiftRightLogical(p, 24).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = Fma.MultiplyAdd(WW, Rf, qr); qg = Fma.MultiplyAdd(WW, Gf, qg); qb = Fma.MultiplyAdd(WW, Bf, qb); qa = Fma.MultiplyAdd(WW, Af, qa);
        }
        s[0] = Vector256.Sum(z0); s[1] = Vector256.Sum(z1); s[2] = Vector256.Sum(z2); s[3] = Vector256.Sum(qr); s[4] = Vector256.Sum(qg); s[5] = Vector256.Sum(qb); s[6] = Vector256.Sum(qa);
    }
    internal static void LsAccum4DScalar(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        float z00 = 0, z10 = 0, z11 = 0, q0r = 0, q0g = 0, q0b = 0, q0a = 0;
        ref int wr = ref MemoryMarshal.GetReference(w); ref ColorRgba pr = ref MemoryMarshal.GetReference(px); ref float tabr = ref MemoryMarshal.GetReference(tab);
        for (int i = 0; i < 16; i++)
        {
            int o = Unsafe.Add(ref wr, i) * 4; z00 += Unsafe.Add(ref tabr, o); z10 += Unsafe.Add(ref tabr, o + 1); z11 += Unsafe.Add(ref tabr, o + 2);
            ref readonly ColorRgba q = ref Unsafe.Add(ref pr, i); float ww = Unsafe.Add(ref tabr, o + 3); q0r += ww * q.R; q0g += ww * q.G; q0b += ww * q.B; q0a += ww * q.A;
        }
        s[0] = z00; s[1] = z10; s[2] = z11; s[3] = q0r; s[4] = q0g; s[5] = q0b; s[6] = q0a;
    }
    internal static void LsAccum4DX86(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w); ref float t = ref MemoryMarshal.GetReference(tab);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default, qa = default;
        for (nuint o = 0; o < 16; o += 8)
        {
            int o0 = Unsafe.Add(ref wr, o) * 4, o1 = Unsafe.Add(ref wr, o + 1) * 4, o2 = Unsafe.Add(ref wr, o + 2) * 4, o3 = Unsafe.Add(ref wr, o + 3) * 4, o4 = Unsafe.Add(ref wr, o + 4) * 4, o5 = Unsafe.Add(ref wr, o + 5) * 4, o6 = Unsafe.Add(ref wr, o + 6) * 4, o7 = Unsafe.Add(ref wr, o + 7) * 4;
            Vector256<float> Z0 = Vector256.Create(Unsafe.Add(ref t, o0), Unsafe.Add(ref t, o1), Unsafe.Add(ref t, o2), Unsafe.Add(ref t, o3), Unsafe.Add(ref t, o4), Unsafe.Add(ref t, o5), Unsafe.Add(ref t, o6), Unsafe.Add(ref t, o7));
            Vector256<float> Z1 = Vector256.Create(Unsafe.Add(ref t, o0 + 1), Unsafe.Add(ref t, o1 + 1), Unsafe.Add(ref t, o2 + 1), Unsafe.Add(ref t, o3 + 1), Unsafe.Add(ref t, o4 + 1), Unsafe.Add(ref t, o5 + 1), Unsafe.Add(ref t, o6 + 1), Unsafe.Add(ref t, o7 + 1));
            Vector256<float> Z2 = Vector256.Create(Unsafe.Add(ref t, o0 + 2), Unsafe.Add(ref t, o1 + 2), Unsafe.Add(ref t, o2 + 2), Unsafe.Add(ref t, o3 + 2), Unsafe.Add(ref t, o4 + 2), Unsafe.Add(ref t, o5 + 2), Unsafe.Add(ref t, o6 + 2), Unsafe.Add(ref t, o7 + 2));
            Vector256<float> WW = Vector256.Create(Unsafe.Add(ref t, o0 + 3), Unsafe.Add(ref t, o1 + 3), Unsafe.Add(ref t, o2 + 3), Unsafe.Add(ref t, o3 + 3), Unsafe.Add(ref t, o4 + 3), Unsafe.Add(ref t, o5 + 3), Unsafe.Add(ref t, o6 + 3), Unsafe.Add(ref t, o7 + 3));
            Vector256<uint> p = Vector256.LoadUnsafe(ref pru, o);
            Vector256<float> Rf = Vector256.ConvertToSingle((p & mask).AsInt32()), Gf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(p, 16) & mask).AsInt32()), Af = Vector256.ConvertToSingle(Vector256.ShiftRightLogical(p, 24).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = Fma.MultiplyAdd(WW, Rf, qr); qg = Fma.MultiplyAdd(WW, Gf, qg); qb = Fma.MultiplyAdd(WW, Bf, qb); qa = Fma.MultiplyAdd(WW, Af, qa);
        }
        s[0] = Vector256.Sum(z0); s[1] = Vector256.Sum(z1); s[2] = Vector256.Sum(z2); s[3] = Vector256.Sum(qr); s[4] = Vector256.Sum(qg); s[5] = Vector256.Sum(qb); s[6] = Vector256.Sum(qa);
    }
    internal static void LsAccum4DArm(scoped ReadOnlySpan<int> w, scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<float> tab, scoped Span<float> s)
    {
        ref int wr = ref MemoryMarshal.GetReference(w); ref float t = ref MemoryMarshal.GetReference(tab);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<float> z0 = default, z1 = default, z2 = default, qr = default, qg = default, qb = default, qa = default;
        for (nuint o = 0; o < 16; o += 4)
        {
            int o0 = Unsafe.Add(ref wr, o) * 4, o1 = Unsafe.Add(ref wr, o + 1) * 4, o2 = Unsafe.Add(ref wr, o + 2) * 4, o3 = Unsafe.Add(ref wr, o + 3) * 4;
            Vector128<float> Z0 = Vector128.Create(Unsafe.Add(ref t, o0), Unsafe.Add(ref t, o1), Unsafe.Add(ref t, o2), Unsafe.Add(ref t, o3));
            Vector128<float> Z1 = Vector128.Create(Unsafe.Add(ref t, o0 + 1), Unsafe.Add(ref t, o1 + 1), Unsafe.Add(ref t, o2 + 1), Unsafe.Add(ref t, o3 + 1));
            Vector128<float> Z2 = Vector128.Create(Unsafe.Add(ref t, o0 + 2), Unsafe.Add(ref t, o1 + 2), Unsafe.Add(ref t, o2 + 2), Unsafe.Add(ref t, o3 + 2));
            Vector128<float> WW = Vector128.Create(Unsafe.Add(ref t, o0 + 3), Unsafe.Add(ref t, o1 + 3), Unsafe.Add(ref t, o2 + 3), Unsafe.Add(ref t, o3 + 3));
            Vector128<uint> p = Vector128.LoadUnsafe(ref pru, o);
            Vector128<float> Rf = Vector128.ConvertToSingle((p & mask).AsInt32()), Gf = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(p, 8) & mask).AsInt32()), Bf = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(p, 16) & mask).AsInt32()), Af = Vector128.ConvertToSingle(Vector128.ShiftRightLogical(p, 24).AsInt32());
            z0 += Z0; z1 += Z1; z2 += Z2; qr = AdvSimd.FusedMultiplyAdd(qr, WW, Rf); qg = AdvSimd.FusedMultiplyAdd(qg, WW, Gf); qb = AdvSimd.FusedMultiplyAdd(qb, WW, Bf); qa = AdvSimd.FusedMultiplyAdd(qa, WW, Af);
        }
        s[0] = Vector128.Sum(z0); s[1] = Vector128.Sum(z1); s[2] = Vector128.Sum(z2); s[3] = Vector128.Sum(qr); s[4] = Vector128.Sum(qg); s[5] = Vector128.Sum(qb); s[6] = Vector128.Sum(qa);
    }


#if NET8_0_OR_GREATER
    // ===================== AVX-512 selector-eval paths (16 px = one 512-bit register) =====================
    // Each mirrors its 256-bit sibling, collapsing two 8-wide passes into one 16-wide pass. Gate:
    // (Avx512F.IsSupported && Vector512.IsHardwareAccelerated) -- the second clause matters because .NET can report
    // Avx512F support while leaving Vector512<T> un-accelerated, in which case these ops emulate as 2x256.
    internal static void EvalSelRgbV512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<int> drv = Vector512.Create(dr), dgv = Vector512.Create(dg), dbv = Vector512.Create(db), ofsv = Vector512.Create(aofs);
        Vector512<float> fv = Vector512.Create(f), halfv = Vector512.Create(0.5f);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> R = (p & mask).AsInt32(), G = (Vector512.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector512.ShiftRightLogical(p, 16) & mask).AsInt32();
        Vector512<int> dot = R * drv + G * dgv + B * dbv + ofsv;
        Vector512<int> sel = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(dot), fv, halfv));
        sel = Vector512.Min(Vector512.Max(sel, Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }
    internal static void EvalSelRgbaV512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int dr, int dg, int db, int da, int aofs, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<int> drv = Vector512.Create(dr), dgv = Vector512.Create(dg), dbv = Vector512.Create(db), dav = Vector512.Create(da), ofsv = Vector512.Create(aofs);
        Vector512<float> fv = Vector512.Create(f), halfv = Vector512.Create(0.5f);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> R = (p & mask).AsInt32(), G = (Vector512.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector512.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector512.ShiftRightLogical(p, 24).AsInt32();
        Vector512<int> dot = R * drv + G * dgv + B * dbv + A * dav + ofsv;
        Vector512<int> sel = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(dot), fv, halfv));
        sel = Vector512.Min(Vector512.Max(sel, Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }
    internal static void EvalSelAV512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, float f, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector512<int> lav = Vector512.Create(la);
        Vector512<float> fv = Vector512.Create(f), halfv = Vector512.Create(0.5f);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> dot = Vector512.ShiftRightLogical(p, 24).AsInt32() - lav;
        Vector512<int> sel = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(dot), fv, halfv));
        sel = Vector512.Min(Vector512.Max(sel, Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }
    internal static void EvalSelRgb2V512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<int> dr0 = Vector512.Create(dr[0]), dr1 = Vector512.Create(dr[1]), dg0 = Vector512.Create(dg[0]), dg1 = Vector512.Create(dg[1]), db0 = Vector512.Create(db[0]), db1 = Vector512.Create(db[1]);
        Vector512<int> so0 = Vector512.Create(sofs[0]), so1 = Vector512.Create(sofs[1]);
        Vector512<float> f0 = Vector512.Create(f[0]), f1 = Vector512.Create(f[1]), half = Vector512.Create(0.5f);
        Vector512<int> bitsel = Vector512.Create(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> R = (p & mask).AsInt32(), G = (Vector512.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector512.ShiftRightLogical(p, 16) & mask).AsInt32();
        Vector512<int> s0 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0, half));
        Vector512<int> s1 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1, half));
        Vector512<int> m = Vector512.Equals(Vector512.Create(subsetMask & 0xFFFF) & bitsel, Vector512<int>.Zero);
        Vector512<int> sel = Vector512.Min(Vector512.Max(Vector512.ConditionalSelect(m, s0, s1), Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }
    internal static void EvalSelRgba2V512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> da, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, int subsetMask, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w);
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<int> dr0 = Vector512.Create(dr[0]), dr1 = Vector512.Create(dr[1]), dg0 = Vector512.Create(dg[0]), dg1 = Vector512.Create(dg[1]), db0 = Vector512.Create(db[0]), db1 = Vector512.Create(db[1]), da0 = Vector512.Create(da[0]), da1 = Vector512.Create(da[1]);
        Vector512<int> so0 = Vector512.Create(sofs[0]), so1 = Vector512.Create(sofs[1]);
        Vector512<float> f0 = Vector512.Create(f[0]), f1 = Vector512.Create(f[1]), half = Vector512.Create(0.5f);
        Vector512<int> bitsel = Vector512.Create(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> R = (p & mask).AsInt32(), G = (Vector512.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector512.ShiftRightLogical(p, 16) & mask).AsInt32(), A = Vector512.ShiftRightLogical(p, 24).AsInt32();
        Vector512<int> s0 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr0 + G * dg0 + B * db0 + A * da0 - so0), f0, half));
        Vector512<int> s1 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr1 + G * dg1 + B * db1 + A * da1 - so1), f1, half));
        Vector512<int> m = Vector512.Equals(Vector512.Create(subsetMask & 0xFFFF) & bitsel, Vector512<int>.Zero);
        Vector512<int> sel = Vector512.Min(Vector512.Max(Vector512.ConditionalSelect(m, s0, s1), Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }
    internal static void EvalSelRgb3V512(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, scoped ReadOnlySpan<int> dr, scoped ReadOnlySpan<int> dg, scoped ReadOnlySpan<int> db, scoped ReadOnlySpan<int> sofs, scoped ReadOnlySpan<float> f, scoped ReadOnlySpan<byte> part, int mx)
    {
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px)); ref int wr = ref MemoryMarshal.GetReference(w); ref byte pt = ref MemoryMarshal.GetReference(part);
        Vector512<uint> mask = Vector512.Create(0xFFu);
        Vector512<int> dr0 = Vector512.Create(dr[0]), dr1 = Vector512.Create(dr[1]), dr2 = Vector512.Create(dr[2]), dg0 = Vector512.Create(dg[0]), dg1 = Vector512.Create(dg[1]), dg2 = Vector512.Create(dg[2]), db0 = Vector512.Create(db[0]), db1 = Vector512.Create(db[1]), db2 = Vector512.Create(db[2]);
        Vector512<int> so0 = Vector512.Create(sofs[0]), so1 = Vector512.Create(sofs[1]), so2 = Vector512.Create(sofs[2]);
        Vector512<float> f0 = Vector512.Create(f[0]), f1 = Vector512.Create(f[1]), f2 = Vector512.Create(f[2]), half = Vector512.Create(0.5f);
        Vector512<int> one = Vector512.Create(1), two = Vector512.Create(2);
        Vector512<uint> p = Vector512.LoadUnsafe(ref pru, 0);
        Vector512<int> R = (p & mask).AsInt32(), G = (Vector512.ShiftRightLogical(p, 8) & mask).AsInt32(), B = (Vector512.ShiftRightLogical(p, 16) & mask).AsInt32();
        Vector512<int> s0 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr0 + G * dg0 + B * db0 - so0), f0, half));
        Vector512<int> s1 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr1 + G * dg1 + B * db1 - so1), f1, half));
        Vector512<int> s2 = Avx512F.ConvertToVector512Int32WithTruncation(Avx512F.FusedMultiplyAdd(Vector512.ConvertToSingle(R * dr2 + G * dg2 + B * db2 - so2), f2, half));
        Vector512<int> idx = Vector512.Create(Unsafe.Add(ref pt, 0), Unsafe.Add(ref pt, 1), Unsafe.Add(ref pt, 2), Unsafe.Add(ref pt, 3), Unsafe.Add(ref pt, 4), Unsafe.Add(ref pt, 5), Unsafe.Add(ref pt, 6), Unsafe.Add(ref pt, 7), Unsafe.Add(ref pt, 8), Unsafe.Add(ref pt, 9), Unsafe.Add(ref pt, 10), Unsafe.Add(ref pt, 11), Unsafe.Add(ref pt, 12), Unsafe.Add(ref pt, 13), Unsafe.Add(ref pt, 14), Unsafe.Add(ref pt, 15));
        Vector512<int> sel = Vector512.ConditionalSelect(Vector512.Equals(idx, two), s2, Vector512.ConditionalSelect(Vector512.Equals(idx, one), s1, s0));
        sel = Vector512.Min(Vector512.Max(sel, Vector512<int>.Zero), Vector512.Create(mx));
        sel.StoreUnsafe(ref wr, 0);
    }

#endif
    internal static long SsdRgba(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
#if NET8_0_OR_GREATER
        if (Avx512BW.IsSupported && Vector512.IsHardwareAccelerated) return SsdRgbaV512Bw(a, b);  // fastest x86-512 (vpmaddwd-512)
        if (Vector512.IsHardwareAccelerated) return SsdRgbaV512(a, b);                             // portable generic 512 fallback
#endif
#if NET9_0_OR_GREATER
        if (AvxVnni.IsSupported) return SsdRgbaVnni(a, b);   // VPDPWSSD: byte-identical (net9+)
#endif
        if (Avx2.IsSupported) return SsdRgbaAvx2(a, b);
        if (Dp.IsSupported) return SsdRgbaArm(a, b);   // ARM64 UDOT (ARMv8.2-DotProd) — byte-identical
        if (Vector256.IsHardwareAccelerated) return SsdRgbaV256(a, b);
        if (Vector128.IsHardwareAccelerated) return SsdRgbaV128(a, b);
        return SsdRgbaScalar(a, b);
    }

    // AVX2 fast path: sum-of-squared-diffs via vpmaddwd (Avx2.MultiplyAddAdjacent). Integer-exact => byte-identical.
    internal static long SsdRgbaAvx2(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref byte ra = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(a));
        ref byte rb = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(b));
        Vector256<int> acc = Vector256<int>.Zero;
        for (nuint o = 0; o < 64; o += 32)
        {
            Vector256<byte> av = Vector256.LoadUnsafe(ref ra, o), bv = Vector256.LoadUnsafe(ref rb, o);
            Vector256<short> dLo = Avx2.ConvertToVector256Int16(av.GetLower()) - Avx2.ConvertToVector256Int16(bv.GetLower());
            Vector256<short> dHi = Avx2.ConvertToVector256Int16(av.GetUpper()) - Avx2.ConvertToVector256Int16(bv.GetUpper());
            acc += Avx2.MultiplyAddAdjacent(dLo, dLo);
            acc += Avx2.MultiplyAddAdjacent(dHi, dHi);
        }
        return Vector256.Sum(acc);
    }
#if NET9_0_OR_GREATER
    // AVX-VNNI SSD: four VPDPWSSD into TWO accumulators (breaks the latency chain). VPDPWSSD(acc,x,x)
    // == acc + MultiplyAddAdjacent(x,x), integer-exact -> bit-identical to SsdRgbaAvx2.
    internal static long SsdRgbaVnni(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref byte ra = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(a));
        ref byte rb = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(b));
        Vector256<int> acc0 = Vector256<int>.Zero, acc1 = Vector256<int>.Zero;
        for (nuint o = 0; o < 64; o += 32)
        {
            Vector256<byte> av = Vector256.LoadUnsafe(ref ra, o), bv = Vector256.LoadUnsafe(ref rb, o);
            Vector256<short> dLo = Vector256.WidenLower(av).AsInt16() - Vector256.WidenLower(bv).AsInt16();
            Vector256<short> dHi = Vector256.WidenUpper(av).AsInt16() - Vector256.WidenUpper(bv).AsInt16();
            acc0 = AvxVnni.MultiplyWideningAndAdd(acc0, dLo, dLo);
            acc1 = AvxVnni.MultiplyWideningAndAdd(acc1, dHi, dHi);
        }
        return Vector256.Sum(acc0 + acc1);
    }
#endif

    internal static long SsdRgbaScalar(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        long sse = 0;
        for (int i = 0; i < 16; i++)
        {
            int dr = a[i].R - b[i].R, dg = a[i].G - b[i].G, db = a[i].B - b[i].B, da = a[i].A - b[i].A;
            sse += (long)dr * dr + (long)dg * dg + (long)db * db + (long)da * da;
        }
        return sse;
    }

#if NET8_0_OR_GREATER
    internal static long SsdRgbaV512(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref uint ar = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(a));
        ref uint br = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(b));
        Vector512<uint> m = Vector512.Create(0xFFu);
        Vector512<uint> p = Vector512.LoadUnsafe(ref ar), q = Vector512.LoadUnsafe(ref br);
        Vector512<int> dR = (p & m).AsInt32() - (q & m).AsInt32();
        Vector512<int> dG = (Vector512.ShiftRightLogical(p, 8) & m).AsInt32() - (Vector512.ShiftRightLogical(q, 8) & m).AsInt32();
        Vector512<int> dB = (Vector512.ShiftRightLogical(p, 16) & m).AsInt32() - (Vector512.ShiftRightLogical(q, 16) & m).AsInt32();
        Vector512<int> dA = Vector512.ShiftRightLogical(p, 24).AsInt32() - Vector512.ShiftRightLogical(q, 24).AsInt32();
        return Vector512.Sum(dR * dR + dG * dG + dB * dB + dA * dA);
    }
    // AVX-512BW SSD: one 64-byte load, widen to int16, VPMADDWD-512 (cheaper than the generic int32
    // vpmulld path). Integer-exact -> byte-identical to SsdRgbaV512.
    internal static long SsdRgbaV512Bw(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref byte ra = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(a));
        ref byte rb = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(b));
        Vector512<byte> av = Vector512.LoadUnsafe(ref ra), bv = Vector512.LoadUnsafe(ref rb);
        Vector512<short> dLo = Vector512.WidenLower(av).AsInt16() - Vector512.WidenLower(bv).AsInt16();
        Vector512<short> dHi = Vector512.WidenUpper(av).AsInt16() - Vector512.WidenUpper(bv).AsInt16();
        return Vector512.Sum(Avx512BW.MultiplyAddAdjacent(dLo, dLo) + Avx512BW.MultiplyAddAdjacent(dHi, dHi));
    }

#endif
    internal static long SsdRgbaV256(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref uint ar = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(a));
        ref uint br = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(b));
        Vector256<uint> m = Vector256.Create(0xFFu);
        Vector256<int> acc = Vector256<int>.Zero;
        for (nuint o = 0; o < 16; o += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref ar, o), q = Vector256.LoadUnsafe(ref br, o);
            Vector256<int> dR = (p & m).AsInt32() - (q & m).AsInt32();
            Vector256<int> dG = (Vector256.ShiftRightLogical(p, 8) & m).AsInt32() - (Vector256.ShiftRightLogical(q, 8) & m).AsInt32();
            Vector256<int> dB = (Vector256.ShiftRightLogical(p, 16) & m).AsInt32() - (Vector256.ShiftRightLogical(q, 16) & m).AsInt32();
            Vector256<int> dA = Vector256.ShiftRightLogical(p, 24).AsInt32() - Vector256.ShiftRightLogical(q, 24).AsInt32();
            acc += dR * dR + dG * dG + dB * dB + dA * dA;
        }
        return Vector256.Sum(acc);
    }

    internal static long SsdRgbaV128(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref uint ar = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(a));
        ref uint br = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(b));
        Vector128<uint> m = Vector128.Create(0xFFu);
        Vector128<int> acc = Vector128<int>.Zero;
        for (nuint o = 0; o < 16; o += 4)
        {
            Vector128<uint> p = Vector128.LoadUnsafe(ref ar, o), q = Vector128.LoadUnsafe(ref br, o);
            Vector128<int> dR = (p & m).AsInt32() - (q & m).AsInt32();
            Vector128<int> dG = (Vector128.ShiftRightLogical(p, 8) & m).AsInt32() - (Vector128.ShiftRightLogical(q, 8) & m).AsInt32();
            Vector128<int> dB = (Vector128.ShiftRightLogical(p, 16) & m).AsInt32() - (Vector128.ShiftRightLogical(q, 16) & m).AsInt32();
            Vector128<int> dA = Vector128.ShiftRightLogical(p, 24).AsInt32() - Vector128.ShiftRightLogical(q, 24).AsInt32();
            acc += dR * dR + dG * dG + dB * dB + dA * dA;
        }
        return Vector128.Sum(acc);
    }

    // --- ARM64 NEON optimizations ---

    // ARM64 NEON SSD via UDOT: AbsoluteDifference (UABD) -> |a-b| in [0,255]; DotProduct(acc,d,d) (UDOT) accumulates
    // sum |a-b|^2 into int32 lanes. Integer-exact -> byte-identical to the other SSD paths. Dp = ARMv8.2-DotProd
    // (Apple M*, modern Snapdragon, Graviton2+). Max SSD = 64*255^2 ~ 4.16M < uint32 max, so no overflow.
    internal static long SsdRgbaArm(scoped ReadOnlySpan<ColorRgba> a, scoped ReadOnlySpan<ColorRgba> b)
    {
        ref byte ra = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(a));
        ref byte rb = ref Unsafe.As<ColorRgba, byte>(ref MemoryMarshal.GetReference(b));
        Vector128<uint> acc = Vector128<uint>.Zero;
        for (nuint o = 0; o < 64; o += 16)
        {
            Vector128<byte> av = Vector128.LoadUnsafe(ref ra, o), bv = Vector128.LoadUnsafe(ref rb, o);
            Vector128<byte> d = AdvSimd.AbsoluteDifference(av, bv);
            acc = Dp.DotProduct(acc, d, d);
        }
        return Vector128.Sum(acc);
    }
}
