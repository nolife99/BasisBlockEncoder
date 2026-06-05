// Bc1Block.cs — BC1 4-color color-block encoder (namespace Bcn.Ldr).
//
// Faithful port of the rgbcx 4-color BC1 kernel (which is a superset of, and on its fast path
// identical to, native basisu's encode_bc1). 3-color/punch-through blocks are never emitted, so the
// output is always GPU-safe and valid as the color half of a BC3 block. The endpoint math is plain
// scalar float (no FMA contraction), so a managed port is bit-identical across architectures by
// construction; every other reduction is integer. Quality is a real speed/quality knob:
//   Fast        : PCA(4 power-iters) + 1 LS pass, commit
//   Default     : PCA(4 power-iters) + 2 LS passes + MSE-keep-best  (keep-best rather than commit-last)
//   HighQuality : PCA(6 power-iters) + 2 LS passes + MSE-keep-best
//
// The selector search (FindSels4*) is the hot, repeatedly-called integer kernel; it has a 128-bit
// SIMD tier (groups of 4 lanes, matching the scalar early-out granularity) that is bit-identical to
// the scalar path and runs on SSE and NEON alike. The float endpoint math stays scalar.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bcn.Ldr;

/// <summary>Speed/quality level for the BC1 color encoder (and the color half of BC3).</summary>
public enum Bc1Quality { Fast, Default, HighQuality }

internal static partial class Bc1Block
{
    // ---- 5/6-bit expand + 8-bit->5/6-bit quantize ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Scale5To8(int v) => (v << 3) | (v >> 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Scale6To8(int v) => (v << 2) | (v >> 4);
    private static int To5(int v) { v = v * 31 + 128; return (v + (v >> 8)) >> 8; }
    private static int To6(int v) { v = v * 63 + 128; return (v + (v >> 8)) >> 8; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sq(int a) => a * a;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack565(int r, int g, int b) => (uint)((r << 11) | (g << 5) | b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp5(int v) => ((uint)v > 31u) ? ((~v >> 31) & 31) : v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp6(int v) => ((uint)v > 63u) ? ((~v >> 31) & 63) : v;

    // 9*(w*w), 9*((1-w)*w), 9*((1-w)*(1-w)) for w in {0,1/3,2/3,1}, packed (z00<<16)|(z10<<8)|z11.
    private static readonly uint[] WeightVals4 = { 0x000009u, 0x010204u, 0x040201u, 0x090000u };

    private static readonly float[] Midpoint5 =
    {
        .015686f,.047059f,.078431f,.111765f,.145098f,.176471f,.207843f,.241176f,.274510f,.305882f,
        .337255f,.370588f,.403922f,.435294f,.466667f,.5f,.533333f,.564706f,.596078f,.629412f,.662745f,
        .694118f,.725490f,.758824f,.792157f,.823529f,.854902f,.888235f,.921569f,.952941f,.984314f,1e+37f
    };
    private static readonly float[] Midpoint6 =
    {
        .007843f,.023529f,.039216f,.054902f,.070588f,.086275f,.101961f,.117647f,.133333f,.149020f,
        .164706f,.180392f,.196078f,.211765f,.227451f,.245098f,.262745f,.278431f,.294118f,.309804f,
        .325490f,.341176f,.356863f,.372549f,.388235f,.403922f,.419608f,.435294f,.450980f,.466667f,
        .482353f,.500000f,.517647f,.533333f,.549020f,.564706f,.580392f,.596078f,.611765f,.627451f,
        .643137f,.658824f,.674510f,.690196f,.705882f,.721569f,.737255f,.754902f,.772549f,.788235f,
        .803922f,.819608f,.835294f,.850980f,.866667f,.882353f,.898039f,.913725f,.929412f,.945098f,
        .960784f,.976471f,.992157f,1e+37f
    };

    // single-color match tables: for each target 0..255, the (hi,lo) 565 pair whose selector-2 color
    // (2*hi+lo)/3 best matches it, in ideal-interpolation mode. Generated once.
    private static readonly byte[] Match5Hi = new byte[256], Match5Lo = new byte[256];
    private static readonly byte[] Match6Hi = new byte[256], Match6Lo = new byte[256];

    static Bc1Block()
    {
        PrepareSingleColorTable(Match5Hi, Match5Lo, 32);
        PrepareSingleColorTable(Match6Hi, Match6Lo, 64);
    }

    private static void PrepareSingleColorTable(byte[] hiT, byte[] loT, int size)
    {
        for (int i = 0; i < 256; i++)
        {
            int lowestE = 256;
            for (int lo = 0; lo < size; lo++)
            {
                int loE = size == 32 ? Scale5To8(lo) : Scale6To8(lo);
                for (int hi = 0; hi < size; hi++)
                {
                    int hiE = size == 32 ? Scale5To8(hi) : Scale6To8(hi);
                    int v = (hiE * 2 + loE) / 3;                      // ideal interp_5_6 of selector 2
                    int e = Math.Abs(v - i) + Math.Abs(hiE - loE) * 3 / 100; // 3% GPU-approx term (ideal mode)
                    if (e < lowestE || (e == lowestE && lo == hi))    // favor equal endpoints
                    {
                        hiT[i] = (byte)hi; loT[i] = (byte)lo; lowestE = e;
                    }
                }
            }
        }
    }

    // ---- 4-color palette expand (ideal mode): [low, (2lo+hi)/3, (2hi+lo)/3, high] ----
    private static void GetBlockColors4(Span<int> br, Span<int> bg, Span<int> bb,
        int lr, int lg, int lb, int hr, int hg, int hb)
    {
        br[0] = Scale5To8(lr); bg[0] = Scale6To8(lg); bb[0] = Scale5To8(lb);
        br[3] = Scale5To8(hr); bg[3] = Scale6To8(hg); bb[3] = Scale5To8(hb);
        br[1] = (br[0] * 2 + br[3]) / 3; bg[1] = (bg[0] * 2 + bg[3]) / 3; bb[1] = (bb[0] * 2 + bb[3]) / 3;
        br[2] = (br[3] * 2 + br[0]) / 3; bg[2] = (bg[3] * 2 + bg[0]) / 3; bb[2] = (bb[3] * 2 + bb[0]) / 3;
    }

    private static ReadOnlySpan<byte> SSels => new byte[] { 3, 2, 1, 0 };

    // ---- selector search: dispatch to the 128-bit tier when available, else scalar ----
    // Both tiers are bit-identical: the kernel is pure integer and the V128 path works in groups of
    // 4, the same granularity the scalar early-out uses.

    private static void FindSels4NoErr(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels)
    {
        if (Vector256.IsHardwareAccelerated) FindSels4NoErrV256(px, lr, lg, lb, hr, hg, hb, sels);
        else if (Vector128.IsHardwareAccelerated) FindSels4NoErrV128(px, lr, lg, lb, hr, hg, hb, sels);
        else FindSels4NoErrScalar(px, lr, lg, lb, hr, hg, hb, sels);
    }

    private static uint FindSels4FastErr(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels, uint curErr)
        => Vector256.IsHardwareAccelerated
            ? FindSels4FastErrV256(px, lr, lg, lb, hr, hg, hb, sels, curErr)
            : Vector128.IsHardwareAccelerated
                ? FindSels4FastErrV128(px, lr, lg, lb, hr, hg, hb, sels, curErr)
                : FindSels4FastErrScalar(px, lr, lg, lb, hr, hg, hb, sels, curErr);

    // selector assignment by projection thresholds (no error). sels[i] indexes the monotonic palette.
    internal static void FindSels4NoErrScalar(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int d0p = br[0] * ar + bg[0] * ag + bb[0] * ab, d1p = br[1] * ar + bg[1] * ag + bb[1] * ab;
        int d2p = br[2] * ar + bg[2] * ag + bb[2] * ab, d3p = br[3] * ar + bg[3] * ag + bb[3] * ab;
        int t0 = d0p + d1p, t1 = d1p + d2p, t2 = d2p + d3p;
        ar *= 2; ag *= 2; ab *= 2;
        ReadOnlySpan<byte> s = SSels;
        for (int i = 0; i < 16; i++)
        {
            int d = px[i].R * ar + px[i].G * ag + px[i].B * ab;
            sels[i] = s[(d <= t0 ? 1 : 0) + (d < t1 ? 1 : 0) + (d < t2 ? 1 : 0)];
        }
    }

    // selector assignment with reconstruction error + early-out (used for MSE-keep-best).
    internal static uint FindSels4FastErrScalar(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels, uint curErr)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int d0p = br[0] * ar + bg[0] * ag + bb[0] * ab, d1p = br[1] * ar + bg[1] * ag + bb[1] * ab;
        int d2p = br[2] * ar + bg[2] * ag + bb[2] * ab, d3p = br[3] * ar + bg[3] * ag + bb[3] * ab;
        int t0 = d0p + d1p, t1 = d1p + d2p, t2 = d2p + d3p;
        ar *= 2; ag *= 2; ab *= 2;
        ReadOnlySpan<byte> s = SSels;
        uint total = 0;
        for (int i = 0; i < 16; i += 4)
        {
            for (int j = 0; j < 4; j++)
            {
                int d = px[i + j].R * ar + px[i + j].G * ag + px[i + j].B * ab;
                int sel = s[(d <= t0 ? 1 : 0) + (d < t1 ? 1 : 0) + (d < t2 ? 1 : 0)];
                sels[i + j] = (byte)sel;
                total += (uint)(Sq(px[i + j].R - br[sel]) + Sq(px[i + j].G - bg[sel]) + Sq(px[i + j].B - bb[sel]));
            }
            if (total >= curErr) break; // rejected by caller; remaining sels left stale, never used
        }
        return total;
    }

    // Load 4 RGBA texels (at uint offset g) and split into R/G/B as Vector128<int> (0..255 lanes).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load4(ref uint pru, int g, Vector128<uint> mask8,
        out Vector128<int> r, out Vector128<int> gch, out Vector128<int> b)
    {
        Vector128<uint> u = Vector128.LoadUnsafe(ref Unsafe.Add(ref pru, (nuint)g));
        r = (u & mask8).AsInt32();
        gch = (Vector128.ShiftRightLogical(u, 8) & mask8).AsInt32();
        b = (Vector128.ShiftRightLogical(u, 16) & mask8).AsInt32();
    }

    // sel = SSels[(d<=t0)+(d<t1)+(d<t2)] computed lane-wise. Compares yield 0/-1, so the crossing
    // count is -(sum) and SSels[c] == 3-c, giving sel = 3 + (m0+m1+m2), with each m in {0,-1}.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SelOf(Vector128<int> d, Vector128<int> t0, Vector128<int> t1,
        Vector128<int> t2, Vector128<int> three)
        => three + Vector128.LessThanOrEqual(d, t0) + Vector128.LessThan(d, t1) + Vector128.LessThan(d, t2);

    // 8-wide analogues for the AVX2 (V256) tier.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load8(ref uint pru, int g, Vector256<uint> mask8,
        out Vector256<int> r, out Vector256<int> gch, out Vector256<int> b)
    {
        Vector256<uint> u = Vector256.LoadUnsafe(ref Unsafe.Add(ref pru, (nuint)g));
        r = (u & mask8).AsInt32();
        gch = (Vector256.ShiftRightLogical(u, 8) & mask8).AsInt32();
        b = (Vector256.ShiftRightLogical(u, 16) & mask8).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SelOf(Vector256<int> d, Vector256<int> t0, Vector256<int> t1,
        Vector256<int> t2, Vector256<int> three)
        => three + Vector256.LessThanOrEqual(d, t0) + Vector256.LessThan(d, t1) + Vector256.LessThan(d, t2);

    internal static void FindSels4NoErrV128(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int t0 = (br[0] + br[1]) * ar + (bg[0] + bg[1]) * ag + (bb[0] + bb[1]) * ab;
        int t1 = (br[1] + br[2]) * ar + (bg[1] + bg[2]) * ag + (bb[1] + bb[2]) * ab;
        int t2 = (br[2] + br[3]) * ar + (bg[2] + bg[3]) * ag + (bb[2] + bb[3]) * ab;
        ar *= 2; ag *= 2; ab *= 2;

        Vector128<int> vAr = Vector128.Create(ar), vAg = Vector128.Create(ag), vAb = Vector128.Create(ab);
        Vector128<int> vT0 = Vector128.Create(t0), vT1 = Vector128.Create(t1), vT2 = Vector128.Create(t2);
        Vector128<int> three = Vector128.Create(3);
        Vector128<uint> mask8 = Vector128.Create(0xFFu);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));

        for (int g = 0; g < 16; g += 4)
        {
            Load4(ref pru, g, mask8, out var R, out var G, out var B);
            Vector128<int> sel = SelOf(R * vAr + G * vAg + B * vAb, vT0, vT1, vT2, three);
            sels[g] = (byte)sel.GetElement(0); sels[g + 1] = (byte)sel.GetElement(1);
            sels[g + 2] = (byte)sel.GetElement(2); sels[g + 3] = (byte)sel.GetElement(3);
        }
    }

    // AVX2 (8-wide) variant of the no-error selector assignment. Each pixel's selector is independent,
    // so this is bit-identical to the V128/scalar tiers.
    internal static void FindSels4NoErrV256(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int t0 = (br[0] + br[1]) * ar + (bg[0] + bg[1]) * ag + (bb[0] + bb[1]) * ab;
        int t1 = (br[1] + br[2]) * ar + (bg[1] + bg[2]) * ag + (bb[1] + bb[2]) * ab;
        int t2 = (br[2] + br[3]) * ar + (bg[2] + bg[3]) * ag + (bb[2] + bb[3]) * ab;
        ar *= 2; ag *= 2; ab *= 2;
        Vector256<int> vAr = Vector256.Create(ar), vAg = Vector256.Create(ag), vAb = Vector256.Create(ab);
        Vector256<int> vT0 = Vector256.Create(t0), vT1 = Vector256.Create(t1), vT2 = Vector256.Create(t2);
        Vector256<int> three = Vector256.Create(3);
        Vector256<uint> mask8 = Vector256.Create(0xFFu);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        for (int g = 0; g < 16; g += 8)
        {
            Load8(ref pru, g, mask8, out var R, out var G, out var B);
            Vector256<int> sel = SelOf(R * vAr + G * vAg + B * vAb, vT0, vT1, vT2, three);
            for (int k = 0; k < 8; k++) sels[g + k] = (byte)sel.GetElement(k);
        }
    }

    internal static uint FindSels4FastErrV128(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels, uint curErr)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int t0 = (br[0] + br[1]) * ar + (bg[0] + bg[1]) * ag + (bb[0] + bb[1]) * ab;
        int t1 = (br[1] + br[2]) * ar + (bg[1] + bg[2]) * ag + (bb[1] + bb[2]) * ab;
        int t2 = (br[2] + br[3]) * ar + (bg[2] + bg[3]) * ag + (bb[2] + bb[3]) * ab;
        ar *= 2; ag *= 2; ab *= 2;

        Vector128<int> vAr = Vector128.Create(ar), vAg = Vector128.Create(ag), vAb = Vector128.Create(ab);
        Vector128<int> vT0 = Vector128.Create(t0), vT1 = Vector128.Create(t1), vT2 = Vector128.Create(t2);
        Vector128<int> three = Vector128.Create(3);
        Vector128<int> palR = Vector128.Create(br[0], br[1], br[2], br[3]);
        Vector128<int> palG = Vector128.Create(bg[0], bg[1], bg[2], bg[3]);
        Vector128<int> palB = Vector128.Create(bb[0], bb[1], bb[2], bb[3]);
        Vector128<uint> mask8 = Vector128.Create(0xFFu);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));

        uint total = 0;
        for (int g = 0; g < 16; g += 4)
        {
            Load4(ref pru, g, mask8, out var R, out var G, out var B);
            Vector128<int> sel = SelOf(R * vAr + G * vAg + B * vAb, vT0, vT1, vT2, three);

            // palette[sel] per lane: sel in {0,1,2,3} indexes the 4-entry palette vector directly
            Vector128<int> dr = R - Vector128.Shuffle(palR, sel);
            Vector128<int> dg = G - Vector128.Shuffle(palG, sel);
            Vector128<int> db = B - Vector128.Shuffle(palB, sel);
            total += (uint)Vector128.Sum(dr * dr + dg * dg + db * db);
            sels[g] = (byte)sel.GetElement(0); sels[g + 1] = (byte)sel.GetElement(1);
            sels[g + 2] = (byte)sel.GetElement(2); sels[g + 3] = (byte)sel.GetElement(3);
            if (total >= curErr) break; // rejected by caller; remaining sels left stale, never used
        }
        return total;
    }

    // AVX2 (8-wide) variant: two groups of 8 instead of four of 4. Output is identical to the V128/scalar
    // tiers -- a committed (improving) trial never bails so its 16-pixel error sum is width-independent,
    // and a rejected trial's selectors are discarded regardless of where the early-out lands.
    internal static uint FindSels4FastErrV256(scoped ReadOnlySpan<ColorRgba> px,
        int lr, int lg, int lb, int hr, int hg, int hb, scoped Span<byte> sels, uint curErr)
    {
        Span<int> br = stackalloc int[4], bg = stackalloc int[4], bb = stackalloc int[4];
        GetBlockColors4(br, bg, bb, lr, lg, lb, hr, hg, hb);
        int ar = br[3] - br[0], ag = bg[3] - bg[0], ab = bb[3] - bb[0];
        int t0 = (br[0] + br[1]) * ar + (bg[0] + bg[1]) * ag + (bb[0] + bb[1]) * ab;
        int t1 = (br[1] + br[2]) * ar + (bg[1] + bg[2]) * ag + (bb[1] + bb[2]) * ab;
        int t2 = (br[2] + br[3]) * ar + (bg[2] + bg[3]) * ag + (bb[2] + bb[3]) * ab;
        ar *= 2; ag *= 2; ab *= 2;

        Vector256<int> vAr = Vector256.Create(ar), vAg = Vector256.Create(ag), vAb = Vector256.Create(ab);
        Vector256<int> vT0 = Vector256.Create(t0), vT1 = Vector256.Create(t1), vT2 = Vector256.Create(t2);
        Vector256<int> three = Vector256.Create(3);
        // palette in lanes 0..3 (high lanes unused); sel in {0,1,2,3} gathers palette[sel] via vpermd
        Vector256<int> palR = Vector256.Create(br[0], br[1], br[2], br[3], 0, 0, 0, 0);
        Vector256<int> palG = Vector256.Create(bg[0], bg[1], bg[2], bg[3], 0, 0, 0, 0);
        Vector256<int> palB = Vector256.Create(bb[0], bb[1], bb[2], bb[3], 0, 0, 0, 0);
        Vector256<uint> mask8 = Vector256.Create(0xFFu);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));

        uint total = 0;
        for (int g = 0; g < 16; g += 8)
        {
            Load8(ref pru, g, mask8, out var R, out var G, out var B);
            Vector256<int> sel = SelOf(R * vAr + G * vAg + B * vAb, vT0, vT1, vT2, three);
            Vector256<int> dr = R - Vector256.Shuffle(palR, sel);
            Vector256<int> dg = G - Vector256.Shuffle(palG, sel);
            Vector256<int> db = B - Vector256.Shuffle(palB, sel);
            total += (uint)Vector256.Sum(dr * dr + dg * dg + db * db);
            for (int k = 0; k < 8; k++) sels[g + k] = (byte)sel.GetElement(k);
            if (total >= curErr) break; // rejected by caller; remaining sels left stale, never used
        }
        return total;
    }

    // ---- PCA integer loops: covariance accumulation + projection min/max (scalar + V128) ----

    private static void AccumCovariance(scoped ReadOnlySpan<ColorRgba> px, int avgR, int avgG, int avgB,
        out int c0, out int c1, out int c2, out int c3, out int c4, out int c5)
    {
        if (Vector128.IsHardwareAccelerated) AccumCovarianceV128(px, avgR, avgG, avgB, out c0, out c1, out c2, out c3, out c4, out c5);
        else AccumCovarianceScalar(px, avgR, avgG, avgB, out c0, out c1, out c2, out c3, out c4, out c5);
    }

    internal static void AccumCovarianceScalar(scoped ReadOnlySpan<ColorRgba> px, int avgR, int avgG, int avgB,
        out int c0, out int c1, out int c2, out int c3, out int c4, out int c5)
    {
        c0 = c1 = c2 = c3 = c4 = c5 = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i].R - avgR, g = px[i].G - avgG, b = px[i].B - avgB;
            c0 += r * r; c1 += r * g; c2 += r * b; c3 += g * g; c4 += g * b; c5 += b * b;
        }
    }

    internal static void AccumCovarianceV128(scoped ReadOnlySpan<ColorRgba> px, int avgR, int avgG, int avgB,
        out int c0, out int c1, out int c2, out int c3, out int c4, out int c5)
    {
        Vector128<int> vAvgR = Vector128.Create(avgR), vAvgG = Vector128.Create(avgG), vAvgB = Vector128.Create(avgB);
        Vector128<uint> mask8 = Vector128.Create(0xFFu);
        Vector128<int> a0 = Vector128<int>.Zero, a1 = a0, a2 = a0, a3 = a0, a4 = a0, a5 = a0;
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        for (int g = 0; g < 16; g += 4)
        {
            Load4(ref pru, g, mask8, out var R, out var G, out var B);
            Vector128<int> r = R - vAvgR, gg = G - vAvgG, b = B - vAvgB;
            a0 += r * r; a1 += r * gg; a2 += r * b; a3 += gg * gg; a4 += gg * b; a5 += b * b;
        }
        c0 = Vector128.Sum(a0); c1 = Vector128.Sum(a1); c2 = Vector128.Sum(a2);
        c3 = Vector128.Sum(a3); c4 = Vector128.Sum(a4); c5 = Vector128.Sum(a5);
    }

    private static int HMin128(Vector128<int> m)
    { m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1))); m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))); return m.ToScalar(); }
    private static int HMax128(Vector128<int> m)
    { m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1))); m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))); return m.ToScalar(); }

    private static void ProjectMinMax(scoped ReadOnlySpan<ColorRgba> px, int sr, int sg, int sb, out int lowDot, out int highDot)
    {
        if (Vector128.IsHardwareAccelerated) ProjectMinMaxV128(px, sr, sg, sb, out lowDot, out highDot);
        else ProjectMinMaxScalar(px, sr, sg, sb, out lowDot, out highDot);
    }

    internal static void ProjectMinMaxScalar(scoped ReadOnlySpan<ColorRgba> px, int sr, int sg, int sb, out int lowDot, out int highDot)
    {
        lowDot = int.MaxValue; highDot = int.MinValue;
        for (int i = 0; i < 16; i++)
        {
            int dot = ((px[i].R * sr + px[i].G * sg + px[i].B * sb) & ~0xF) + i;
            if (dot < lowDot) lowDot = dot;
            if (dot > highDot) highDot = dot;
        }
    }

    internal static void ProjectMinMaxV128(scoped ReadOnlySpan<ColorRgba> px, int sr, int sg, int sb, out int lowDot, out int highDot)
    {
        Vector128<int> vSr = Vector128.Create(sr), vSg = Vector128.Create(sg), vSb = Vector128.Create(sb);
        Vector128<int> vNotF = Vector128.Create(~0xF), iota = Vector128.Create(0, 1, 2, 3);
        Vector128<uint> mask8 = Vector128.Create(0xFFu);
        Vector128<int> vLow = Vector128.Create(int.MaxValue), vHigh = Vector128.Create(int.MinValue);
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        for (int g = 0; g < 16; g += 4)
        {
            Load4(ref pru, g, mask8, out var R, out var G, out var B);
            Vector128<int> dot = ((R * vSr + G * vSg + B * vSb) & vNotF) + (iota + Vector128.Create(g));
            vLow = Vector128.Min(vLow, dot);
            vHigh = Vector128.Max(vHigh, dot);
        }
        lowDot = HMin128(vLow); highDot = HMax128(vHigh);
    }

    // 2x2 normal-equations least-squares endpoint solve from current selectors. The accumulation is
    // entirely integer (and the sums never overflow their fields over 16 pixels), so the V128 4-wide
    // reduction is bit-identical to the scalar sum; only the post-loop solve is float, shared by both.
    private static bool ComputeLeastSquares(scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<byte> sels,
        int totalR, int totalG, int totalB, scoped Span<float> xl, scoped Span<float> xh)
        => Vector128.IsHardwareAccelerated
            ? ComputeLeastSquaresV128(px, sels, totalR, totalG, totalB, xl, xh)
            : ComputeLeastSquaresScalar(px, sels, totalR, totalG, totalB, xl, xh);

    internal static bool ComputeLeastSquaresScalar(scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<byte> sels,
        int totalR, int totalG, int totalB, scoped Span<float> xl, scoped Span<float> xh)
    {
        uint uq00R = 0, uq00G = 0, uq00B = 0, weightAccum = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i].R, g = px[i].G, b = px[i].B; int sel = sels[i];
            weightAccum += WeightVals4[sel];
            uq00R += (uint)(sel * r); uq00G += (uint)(sel * g); uq00B += (uint)(sel * b);
        }
        return SolveLeastSquares(uq00R, uq00G, uq00B, weightAccum, totalR, totalG, totalB, xl, xh);
    }

    internal static bool ComputeLeastSquaresV128(scoped ReadOnlySpan<ColorRgba> px, scoped ReadOnlySpan<byte> sels,
        int totalR, int totalG, int totalB, scoped Span<float> xl, scoped Span<float> xh)
    {
        Vector128<uint> mask8 = Vector128.Create(0xFFu);
        Vector128<int> wtab = Vector128.Create((int)WeightVals4[0], (int)WeightVals4[1], (int)WeightVals4[2], (int)WeightVals4[3]);
        Vector128<int> accR = Vector128<int>.Zero, accG = Vector128<int>.Zero, accB = Vector128<int>.Zero, accW = Vector128<int>.Zero;
        ref uint pru = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<ColorRgba, uint>(px));
        for (int g = 0; g < 16; g += 4)
        {
            Load4(ref pru, g, mask8, out var R, out var G, out var B);
            Vector128<int> sel = Vector128.Create(sels[g], sels[g + 1], sels[g + 2], sels[g + 3]);
            accR += sel * R; accG += sel * G; accB += sel * B;
            accW += Vector128.Shuffle(wtab, sel); // WeightVals4[sel] per lane; packed fields don't overflow over 16 px
        }
        uint uq00R = (uint)Vector128.Sum(accR), uq00G = (uint)Vector128.Sum(accG), uq00B = (uint)Vector128.Sum(accB);
        uint weightAccum = (uint)Vector128.Sum(accW);
        return SolveLeastSquares(uq00R, uq00G, uq00B, weightAccum, totalR, totalG, totalB, xl, xh);
    }

    // shared 2x2 solve from the integer accumulators (identical float arithmetic on both paths).
    private static bool SolveLeastSquares(uint uq00R, uint uq00G, uint uq00B, uint weightAccum,
        int totalR, int totalG, int totalB, scoped Span<float> xl, scoped Span<float> xh)
    {
        int q10R = totalR * 3 - (int)uq00R, q10G = totalG * 3 - (int)uq00G, q10B = totalB * 3 - (int)uq00B;
        float z00 = (weightAccum >> 16) & 0xFF, z10 = (weightAccum >> 8) & 0xFF, z11 = weightAccum & 0xFF, z01 = z10;
        float det = z00 * z11 - z01 * z10;
        if (MathF.Abs(det) < 1e-8f) return false;
        det = 3.0f / 255.0f / det;
        float iz00 = z11 * det, iz01 = -z01 * det, iz10 = -z10 * det, iz11 = z00 * det;
        xl[0] = MulAdd(iz00, uq00R, iz01 * q10R); xh[0] = MulAdd(iz10, uq00R, iz11 * q10R);
        xl[1] = MulAdd(iz00, uq00G, iz01 * q10G); xh[1] = MulAdd(iz10, uq00G, iz11 * q10G);
        xl[2] = MulAdd(iz00, uq00B, iz01 * q10B); xh[2] = MulAdd(iz10, uq00B, iz11 * q10B);
        return true;
    }

    // float endpoints -> 565 with clamp + midpoint rounding. First triple from a, second from b.
    private static void PreciseRound565(scoped ReadOnlySpan<float> a, scoped ReadOnlySpan<float> b,
        out int ar, out int ag, out int ab, out int br, out int bg, out int bb)
    {
        ar = Clamp5((int)(a[0] * 31.0f)); ag = Clamp6((int)(a[1] * 63.0f)); ab = Clamp5((int)(a[2] * 31.0f));
        br = Clamp5((int)(b[0] * 31.0f)); bg = Clamp6((int)(b[1] * 63.0f)); bb = Clamp5((int)(b[2] * 31.0f));
        ar = (ar + (a[0] > Midpoint5[ar] ? 1 : 0)) & 31; ag = (ag + (a[1] > Midpoint6[ag] ? 1 : 0)) & 63; ab = (ab + (a[2] > Midpoint5[ab] ? 1 : 0)) & 31;
        br = (br + (b[0] > Midpoint5[br] ? 1 : 0)) & 31; bg = (bg + (b[1] > Midpoint6[bg] ? 1 : 0)) & 63; bb = (bb + (b[2] > Midpoint5[bb] ? 1 : 0)) & 31;
    }

    // grayscale endpoints (independent of the projection axis / power-iteration count).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GrayscaleEndpoints(scoped ReadOnlySpan<ColorRgba> px, int minR, int maxR,
        out int lr, out int lg, out int lb, out int hr, out int hg, out int hb)
    {
        int fr = px[0].R;
        if ((maxR - minR) < 2) { lr = lb = hr = hb = To5(fr); lg = hg = To6(fr); }
        else { lr = lb = To5(minR); lg = To6(minR); hr = hb = To5(maxR); hg = To6(maxR); }
    }

    // power iteration on the precomputed integer covariance -> integer projection axis (already <<4).
    // Fused multiply-add: FMA on FMA-capable hardware (net9+ MultiplyAddEstimate), else two-rounding.
    // Scalar + vector overloads share the same hardware-driven choice, so scalar==V128==V256 holds per machine.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float MulAdd(float a, float b, float c)
#if NET9_0_OR_GREATER
        => float.MultiplyAddEstimate(a, b, c);
#else
        => a * b + c;
#endif

    private static void AxisFromCov(int icov0, int icov1, int icov2, int icov3, int icov4, int icov5,
        int minR, int minG, int minB, int maxR, int maxG, int maxB, int powerIters,
        out int saxisR, out int saxisG, out int saxisB)
    {
        saxisR = 306; saxisG = 601; saxisB = 117;
        float xr = maxR - minR, xg = maxG - minG, xb = maxB - minB;
        if (icov2 < 0) xr = -xr;
        if (icov4 < 0) xg = -xg;
        float c0 = icov0 * (1.0f / 255.0f), c1 = icov1 * (1.0f / 255.0f), c2 = icov2 * (1.0f / 255.0f);
        float c3 = icov3 * (1.0f / 255.0f), c4 = icov4 * (1.0f / 255.0f), c5 = icov5 * (1.0f / 255.0f);
        for (int p = 0; p < powerIters; p++)
        {
            float r = MulAdd(xb, c2, MulAdd(xg, c1, xr * c0));
            float g = MulAdd(xb, c4, MulAdd(xg, c3, xr * c1));
            float b = MulAdd(xb, c5, MulAdd(xg, c4, xr * c2));
            xr = r; xg = g; xb = b;
        }
        float k = MathF.Max(MathF.Max(MathF.Abs(xr), MathF.Abs(xg)), MathF.Abs(xb));
        if (k >= 2) { float m = 2048.0f / k; saxisR = (int)(xr * m); saxisG = (int)(xg * m); saxisB = (int)(xb * m); }
        saxisR <<= 4; saxisG <<= 4; saxisB <<= 4;
    }

    // project pixels onto the axis; the two extreme pixels become the 565 endpoints.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EndpointsFromAxis(scoped ReadOnlySpan<ColorRgba> px, int saxisR, int saxisG, int saxisB,
        out int lr, out int lg, out int lb, out int hr, out int hg, out int hb)
    {
        ProjectMinMax(px, saxisR, saxisG, saxisB, out int lowDot, out int highDot);
        int lowC = lowDot & 15, highC = highDot & 15;
        lr = To5(px[lowC].R); lg = To6(px[lowC].G); lb = To5(px[lowC].B);
        hr = To5(px[highC].R); hg = To6(px[highC].G); hb = To5(px[highC].B);
    }

    // principal-axis init: integer covariance -> power iteration -> extreme projected pixels.
    private static void PickInitialPca(scoped ReadOnlySpan<ColorRgba> px, bool grayscale,
        int minR, int minG, int minB, int maxR, int maxG, int maxB, int avgR, int avgG, int avgB,
        int powerIters, out int lr, out int lg, out int lb, out int hr, out int hg, out int hb)
    {
        if (grayscale) { GrayscaleEndpoints(px, minR, maxR, out lr, out lg, out lb, out hr, out hg, out hb); return; }
        AccumCovariance(px, avgR, avgG, avgB, out int c0, out int c1, out int c2, out int c3, out int c4, out int c5);
        AxisFromCov(c0, c1, c2, c3, c4, c5, minR, minG, minB, maxR, maxG, maxB, powerIters, out int sr, out int sg, out int sb);
        EndpointsFromAxis(px, sr, sg, sb, out lr, out lg, out lb, out hr, out hg, out hb);
    }

    private static void Encode4(scoped Span<byte> output, int lr, int lg, int lb, int hr, int hg, int hb,
        scoped ReadOnlySpan<byte> sels)
    {
        uint lc16 = Pack565(lr, lg, lb), hc16 = Pack565(hr, hg, hb);
        if (lc16 == hc16)
        {
            byte mask = 0;
            if (hc16 > 0) hc16--; else { hc16 = 0; lc16 = 1; mask = 0x55; }
            WriteEndpoints(output, lc16, hc16);
            output[4] = output[5] = output[6] = output[7] = mask;
        }
        else
        {
            byte invert = 0;
            if (lc16 < hc16) { (lc16, hc16) = (hc16, lc16); invert = 0x55; }
            WriteEndpoints(output, lc16, hc16);
            ReadOnlySpan<byte> trans = stackalloc byte[] { 0, 2, 3, 1 };
            uint packed = 0;
            for (int i = 0; i < 16; i++) packed |= (uint)trans[sels[i]] << (i * 2);
            output[4] = (byte)((byte)packed ^ invert);
            output[5] = (byte)((byte)(packed >> 8) ^ invert);
            output[6] = (byte)((byte)(packed >> 16) ^ invert);
            output[7] = (byte)((byte)(packed >> 24) ^ invert);
        }
    }

    private static void WriteEndpoints(scoped Span<byte> output, uint color0, uint color1)
    {
        output[0] = (byte)color0; output[1] = (byte)(color0 >> 8);
        output[2] = (byte)color1; output[3] = (byte)(color1 >> 8);
    }

    private static void EncodeSolid(scoped Span<byte> output, int fr, int fg, int fb)
    {
        uint mask = 0xAA;
        int max16 = (Match5Hi[fr] << 11) | (Match6Hi[fg] << 5) | Match5Hi[fb];
        int min16 = (Match5Lo[fr] << 11) | (Match6Lo[fg] << 5) | Match5Lo[fb];
        if (min16 == max16)
        {
            mask = 0; // forbid 3-color: make color0 > color1
            if (min16 > 0) min16--; else { max16 = 1; min16 = 0; mask = 0x55; }
        }
        if (max16 < min16) { (max16, min16) = (min16, max16); mask ^= 0x55; }
        WriteEndpoints(output, (uint)max16, (uint)min16);
        output[4] = output[5] = output[6] = output[7] = (byte)mask;
    }

    /// <summary>Encode 16 texels' RGB to an 8-byte BC1 (4-color) block.</summary>
    internal static void EncodeBlock(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> output, Bc1Quality quality)
    {
        int fr = px[0].R, fg = px[0].G, fb = px[0].B;
        int j;
        for (j = 15; j >= 1; --j)
            if (px[j].R != fr || px[j].G != fg || px[j].B != fb) break;
        if (j == 0) { EncodeSolid(output, fr, fg, fb); return; }

        int totalR = fr, totalG = fg, totalB = fb, maxR = fr, maxG = fg, maxB = fb, minR = fr, minG = fg, minB = fb;
        bool grayscale = (fr == fg) && (fr == fb);
        for (int i = 1; i < 16; i++)
        {
            int r = px[i].R, g = px[i].G, b = px[i].B;
            grayscale &= (r == g) && (r == b);
            if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (b > maxB) maxB = b;
            if (r < minR) minR = r; if (g < minG) minG = g; if (b < minB) minB = b;
            totalR += r; totalG += g; totalB += b;
        }
        int avgR = (totalR + 8) >> 4, avgG = (totalG + 8) >> 4, avgB = (totalB + 8) >> 4;

        Span<byte> sels = stackalloc byte[16];
        int lr, lg, lb, hr, hg, hb;

        if (quality == Bc1Quality.Fast)
        {
            // PCA(4 iters) init + a single LS pass, committed without error tracking.
            Span<float> xl = stackalloc float[3], xh = stackalloc float[3];
            PickInitialPca(px, grayscale, minR, minG, minB, maxR, maxG, maxB, avgR, avgG, avgB, 4,
                out lr, out lg, out lb, out hr, out hg, out hb);
            FindSels4NoErr(px, lr, lg, lb, hr, hg, hb, sels);
            int tlr, tlg, tlb, thr, thg, thb;
            if (!ComputeLeastSquares(px, sels, totalR, totalG, totalB, xl, xh))
            {
                tlr = Match5Hi[avgR]; tlg = Match6Hi[avgG]; tlb = Match5Hi[avgB];
                thr = Match5Lo[avgR]; thg = Match6Lo[avgG]; thb = Match5Lo[avgB];
            }
            else
            {
                // xl is the low-selector endpoint, xh the high; the high/low naming flips here.
                PreciseRound565(xl, xh, out thr, out thg, out thb, out tlr, out tlg, out tlb);
            }
            if (!(lr == tlr && lg == tlg && lb == tlb && hr == thr && hg == thg && hb == thb))
            {
                FindSels4NoErr(px, tlr, tlg, tlb, thr, thg, thb, sels);
                lr = tlr; lg = tlg; lb = tlb; hr = thr; hg = thg; hb = thb;
            }
        }
        else
        {
            // Default runs a single PCA(4) round; HighQuality a single PCA(6) round (matching the
            // reference encoder's high-quality 4-color path). Both then run find_sels + 2 least-squares
            // passes with MSE-keep-best from the PCA-initialized endpoints.
            int powerIters = quality == Bc1Quality.HighQuality ? 6 : 4;
            PickInitialPca(px, grayscale, minR, minG, minB, maxR, maxG, maxB, avgR, avgG, avgB, powerIters,
                out lr, out lg, out lb, out hr, out hg, out hb);
            RunDescent(px, totalR, totalG, totalB, avgR, avgG, avgB, ref lr, ref lg, ref lb, ref hr, ref hg, ref hb, sels);
        }

        Encode4(output, lr, lg, lb, hr, hg, hb, sels);
    }

    // MSE-keep-best LS descent from the given initial endpoints: returns the best block error and
    // updates the endpoints + selectors. Each kept LS step strictly lowers error and the loop stops at
    // the first non-improving step, so the result error never exceeds the initial endpoints'.
    private static uint RunDescent(scoped ReadOnlySpan<ColorRgba> px, int totalR, int totalG, int totalB,
        int avgR, int avgG, int avgB,
        ref int lr, ref int lg, ref int lb, ref int hr, ref int hg, ref int hb, scoped Span<byte> sels)
    {
        Span<float> xl = stackalloc float[3], xh = stackalloc float[3];
        Span<byte> trialSels = stackalloc byte[16];
        uint err = FindSels4FastErr(px, lr, lg, lb, hr, hg, hb, sels, uint.MaxValue);
        for (int pass = 0; pass < 2; pass++)
        {
            int tlr, tlg, tlb, thr, thg, thb;
            if (!ComputeLeastSquares(px, sels, totalR, totalG, totalB, xl, xh))
            {
                tlr = Match5Hi[avgR]; tlg = Match6Hi[avgG]; tlb = Match5Hi[avgB];
                thr = Match5Lo[avgR]; thg = Match6Lo[avgG]; thb = Match5Lo[avgB];
            }
            else PreciseRound565(xl, xh, out thr, out thg, out thb, out tlr, out tlg, out tlb);
            if (lr == tlr && lg == tlg && lb == tlb && hr == thr && hg == thg && hb == thb) break;
            uint trialErr = FindSels4FastErr(px, tlr, tlg, tlb, thr, thg, thb, trialSels, err);
            if (trialErr < err)
            {
                lr = tlr; lg = tlg; lb = tlb; hr = thr; hg = thg; hb = thb; err = trialErr; trialSels.CopyTo(sels);
            }
            else break;
        }
        return err;
    }
}
