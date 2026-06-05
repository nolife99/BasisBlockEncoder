// Bc1SimdTest.cs — verifies the BC1 selector-search SIMD tiers are bit-identical to the scalar tier:
// the V128 and V256 tiers must produce the same selectors and the same returned error with the early-out
// disabled (full 16-pixel sum). With a tight cap the scalar and V128 paths group pixels by 4 and bail at
// the same point, so they stay identical; V256 groups by 8, so its early-out can land later and return a
// larger partial error on a *rejected* trial. That difference is discarded by the caller (a rejected trial
// never updates the output), so V256 is checked only on the committed (no-early-out) path here, and the
// end-to-end golden snapshot confirms the encoded output is unchanged.
using System;
using System.Runtime.Intrinsics;
using Bcn;

namespace Bcn.Ldr;

internal static class Bc1SimdTest
{
    static uint s_rng = 0x9E3779B9u;
    static uint Rnd() { s_rng ^= s_rng << 13; s_rng ^= s_rng >> 17; s_rng ^= s_rng << 5; return s_rng; }
    static byte RB() => (byte)(Rnd() & 0xFF);
    static int R5() => (int)(Rnd() % 32);
    static int R6() => (int)(Rnd() % 64);

    public static bool Run()
    {
        Console.WriteLine();
        Console.WriteLine("BC1 FindSels4 SIMD tier identity (scalar == V128 == V256):");
        Span<ColorRgba> px = stackalloc ColorRgba[16];
        Span<byte> s1 = stackalloc byte[16], s2 = stackalloc byte[16];
        Span<float> xl1 = stackalloc float[3], xh1 = stackalloc float[3], xl2 = stackalloc float[3], xh2 = stackalloc float[3];
        long noErrFails = 0, fastSelFails = 0, fastValFails = 0, covFails = 0, projFails = 0, lsFails = 0;

        for (int t = 0; t < 300_000; t++)
        {
            for (int i = 0; i < 16; i++) px[i] = new ColorRgba(RB(), RB(), RB(), RB());
            int lr = R5(), lg = R6(), lb = R5(), hr = R5(), hg = R6(), hb = R5();

            // no-error variant: selectors must match exactly across all three tiers
            s1.Clear(); s2.Clear();
            Bc1Block.FindSels4NoErrScalar(px, lr, lg, lb, hr, hg, hb, s1);
            Bc1Block.FindSels4NoErrV128(px, lr, lg, lb, hr, hg, hb, s2);
            if (!s1.SequenceEqual(s2)) noErrFails++;
            s2.Clear();
            Bc1Block.FindSels4NoErrV256(px, lr, lg, lb, hr, hg, hb, s2);
            if (!s1.SequenceEqual(s2)) noErrFails++;

            // fast-error, no early-out (full 16-pixel sum): selectors + returned error must match across tiers
            s1.Clear(); s2.Clear();
            uint e1 = Bc1Block.FindSels4FastErrScalar(px, lr, lg, lb, hr, hg, hb, s1, uint.MaxValue);
            uint e2 = Bc1Block.FindSels4FastErrV128(px, lr, lg, lb, hr, hg, hb, s2, uint.MaxValue);
            if (!s1.SequenceEqual(s2)) fastSelFails++;
            if (e1 != e2) fastValFails++;
            s2.Clear();
            uint e3 = Bc1Block.FindSels4FastErrV256(px, lr, lg, lb, hr, hg, hb, s2, uint.MaxValue);
            if (!s1.SequenceEqual(s2)) fastSelFails++;
            if (e1 != e3) fastValFails++;

            // fast-error with a tight cap: scalar and V128 both group by 4, so they bail at the same point ->
            // identical. V256 groups by 8 and may bail later (returning a larger partial error on a rejected
            // trial); that is discarded by the caller, so it is intentionally not compared here.
            s1.Clear(); s2.Clear();
            uint cap = e1 == 0 ? 1u : e1;
            uint c1 = Bc1Block.FindSels4FastErrScalar(px, lr, lg, lb, hr, hg, hb, s1, cap);
            uint c2 = Bc1Block.FindSels4FastErrV128(px, lr, lg, lb, hr, hg, hb, s2, cap);
            if (!s1.SequenceEqual(s2)) fastSelFails++;
            if (c1 != c2) fastValFails++;

            // PCA covariance accumulation: 6 sums must match
            int avgR = RB(), avgG = RB(), avgB = RB();
            Bc1Block.AccumCovarianceScalar(px, avgR, avgG, avgB, out int ca0, out int ca1, out int ca2, out int ca3, out int ca4, out int ca5);
            Bc1Block.AccumCovarianceV128(px, avgR, avgG, avgB, out int cb0, out int cb1, out int cb2, out int cb3, out int cb4, out int cb5);
            if (ca0 != cb0 || ca1 != cb1 || ca2 != cb2 || ca3 != cb3 || ca4 != cb4 || ca5 != cb5) covFails++;

            // PCA projection min/max (axis spans the post-<<4 range, both signs)
            int sr = (int)(Rnd() % 80001) - 40000, sg = (int)(Rnd() % 80001) - 40000, sb = (int)(Rnd() % 80001) - 40000;
            Bc1Block.ProjectMinMaxScalar(px, sr, sg, sb, out int slo, out int shi);
            Bc1Block.ProjectMinMaxV128(px, sr, sg, sb, out int vlo, out int vhi);
            if (slo != vlo || shi != vhi) projFails++;

            // least-squares accumulation: scalar vs V128 endpoints must be bit-identical (integer accum)
            for (int i = 0; i < 16; i++) s1[i] = (byte)(Rnd() & 3);
            int tR = 0, tG = 0, tB = 0;
            for (int i = 0; i < 16; i++) { tR += px[i].R; tG += px[i].G; tB += px[i].B; }
            bool b1 = Bc1Block.ComputeLeastSquaresScalar(px, s1, tR, tG, tB, xl1, xh1);
            bool b2 = Bc1Block.ComputeLeastSquaresV128(px, s1, tR, tG, tB, xl2, xh2);
            if (b1 != b2 || (b1 && (xl1[0] != xl2[0] || xl1[1] != xl2[1] || xl1[2] != xl2[2]
                || xh1[0] != xh2[0] || xh1[1] != xh2[1] || xh1[2] != xh2[2]))) lsFails++;
        }

        Console.WriteLine($"  NoErr selector mismatches      : {noErrFails}");
        Console.WriteLine($"  FastErr selector mismatches    : {fastSelFails}");
        Console.WriteLine($"  FastErr returned-error diffs   : {fastValFails}");
        Console.WriteLine($"  PCA covariance sum mismatches  : {covFails}");
        Console.WriteLine($"  PCA projection min/max diffs   : {projFails}");
        Console.WriteLine($"  LS endpoint mismatches         : {lsFails}");

        long encFails = ValidateEncode8();
        bool ok = noErrFails == 0 && fastSelFails == 0 && fastValFails == 0 && covFails == 0
            && projFails == 0 && lsFails == 0 && encFails == 0;
        Console.WriteLine(ok ? "  RESULT: PASS" : "  RESULT: FAIL");
        return ok;
    }

    // Stage 3: the full EncodeBlocks8 output must be byte-for-byte identical to 8 independent EncodeBlock
    // calls, across all three qualities and a mix of solid, grayscale, and random blocks.
    static long ValidateEncode8()
    {
#if NET8_0_OR_GREATER
        if (!Vector256.IsHardwareAccelerated)
        {
            Console.WriteLine("  multi-block encode             : skipped (no V256)");
            return 0;
        }
        Span<ColorRgba> px8 = stackalloc ColorRgba[128];
        Span<byte> out8 = stackalloc byte[64];
        Span<byte> ref8 = stackalloc byte[8];
        Bc1Quality[] quals = { Bc1Quality.Fast, Bc1Quality.Default, Bc1Quality.HighQuality };
        long fails = 0;
        for (int t = 0; t < 40_000; t++)
        {
            for (int blk = 0; blk < 8; blk++)
            {
                int kind = (int)(Rnd() % 10);          // ~1/10 solid, ~1/10 grayscale, rest random
                if (kind == 0)
                {
                    byte r = RB(), g = RB(), b = RB();
                    for (int i = 0; i < 16; i++) px8[blk * 16 + i] = new ColorRgba(r, g, b, RB());
                }
                else if (kind == 1)
                {
                    for (int i = 0; i < 16; i++) { byte v = RB(); px8[blk * 16 + i] = new ColorRgba(v, v, v, RB()); }
                }
                else
                {
                    for (int i = 0; i < 16; i++) px8[blk * 16 + i] = new ColorRgba(RB(), RB(), RB(), RB());
                }
            }
            Bc1Quality q = quals[t % 3];
            Bc1Block.EncodeBlocks8(px8, out8, q);
            for (int blk = 0; blk < 8; blk++)
            {
                Bc1Block.EncodeBlock(px8.Slice(blk * 16, 16), ref8, q);
                for (int k = 0; k < 8; k++) if (out8[blk * 8 + k] != ref8[k]) { fails++; break; }
            }
        }
        Console.WriteLine($"  multi-block encode mismatches   : {fails}  (of {40_000 * 8} blocks, all qualities)");
        return fails;
#else
        return 0;
#endif
    }
}
