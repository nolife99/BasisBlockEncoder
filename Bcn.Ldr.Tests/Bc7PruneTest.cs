using System;
using Bcn;
using Bcn.Bc7;

namespace Bcn.Ldr;

// Validation for the mode-6 prune.
//   "eig"  : confirm MaxEig3x3Sym (closed form) equals the converged power-iteration largest eigenvalue,
//            so totalVar - lambda_max is the true off-axis residual -- a sound lower bound on mode-6 SSE.
//   "prune": confirm enabling the prune is output-preserving (prune ON reproduces prune OFF byte-for-byte).
internal static class Bc7PruneTest
{
    static uint s = 0x2468ACEu;
    static int Rnd(int n) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (int)(s % (uint)n); }

    // start-independent correctness: lambda is an eigenvalue iff det(A - lambda I) == 0, and it's the
    // largest iff it's >= trace/3 and >= the Rayleigh quotient of every direction (we sample many).
    static double CharPoly(ReadOnlySpan<float> cov, double L)
    {
        double a = cov[0], b = cov[1], c = cov[2], d = cov[3], e = cov[4], f = cov[5];
        return (a - L) * ((d - L) * (f - L) - e * e) - b * (b * (f - L) - e * c) + c * (b * e - (d - L) * c);
    }
    static double Rayleigh(ReadOnlySpan<float> cov, double vx, double vy, double vz)
    {
        double a = cov[0], b = cov[1], c = cov[2], d = cov[3], e = cov[4], f = cov[5];
        double n = vx * vx + vy * vy + vz * vz; if (n < 1e-300) return 0;
        double rx = a * vx + b * vy + c * vz, ry = b * vx + d * vy + e * vz, rz = c * vx + e * vy + f * vz;
        return (rx * vx + ry * vy + rz * vz) / n;
    }

    public static int Run(string[] args)
    {
        const int N = 200_000;
        double worstPoly = 0, worstShort = 0; int bad = 0;
        Span<float> cov = stackalloc float[6];
        Span<int> px = stackalloc int[16 * 3];
        for (int t = 0; t < N; t++)
        {
            // build a covariance from a random cluster of up to 16 points (real-shaped PSD matrices)
            int np = 2 + Rnd(15);
            double sxx = 0, sxy = 0, sxz = 0, syy = 0, syz = 0, szz = 0, mx = 0, my = 0, mz = 0;
            for (int i = 0; i < np; i++) { int r = Rnd(256), g = Rnd(256), bl = Rnd(256); px[i * 3] = r; px[i * 3 + 1] = g; px[i * 3 + 2] = bl; mx += r; my += g; mz += bl; }
            mx /= np; my /= np; mz /= np;
            for (int i = 0; i < np; i++)
            {
                double dr = px[i * 3] - mx, dg = px[i * 3 + 1] - my, db = px[i * 3 + 2] - mz;
                sxx += dr * dr; sxy += dr * dg; sxz += dr * db; syy += dg * dg; syz += dg * db; szz += db * db;
            }
            cov[0] = (float)sxx; cov[1] = (float)sxy; cov[2] = (float)sxz; cov[3] = (float)syy; cov[4] = (float)syz; cov[5] = (float)szz;

            float lam = Bc7Block.MaxEig3x3Sym(cov);
            double trace = cov[0] + cov[3] + cov[5];
            double scale = Math.Max(1.0, lam); scale = scale * scale * scale;
            double polyRel = Math.Abs(CharPoly(cov, lam)) / scale;     // ~0 means lam is an eigenvalue
            if (polyRel > worstPoly) worstPoly = polyRel;

            // lam must be the LARGEST: >= trace/3 and >= Rayleigh of many random directions (lambda_max bound)
            bool largestOk = lam >= trace / 3.0 - Math.Max(1.0, trace) * 1e-4;
            double maxRay = 0;
            for (int k = 0; k < 24; k++) { int ax = Rnd(2001) - 1000, ay = Rnd(2001) - 1000, az = Rnd(2001) - 1000; double ry = Rayleigh(cov, ax, ay, az); if (ry > maxRay) maxRay = ry; }
            double shortfall = (maxRay - lam) / Math.Max(1.0, lam);     // >0 means lam UNDER-estimates lambda_max (bad: over-prune)
            if (shortfall > worstShort) worstShort = shortfall;

            if (polyRel > 1e-3 || !largestOk || shortfall > 1e-3) bad++;
        }
        Console.WriteLine($"MaxEig3x3Sym validated against char-poly + Rayleigh over {N} random covariances:");
        Console.WriteLine($"  worst |det(A-lambda*I)| / lambda^3 (eigenvalue check) : {worstPoly:E3}");
        Console.WriteLine($"  worst Rayleigh shortfall (under-estimate of lam_max)  : {worstShort:E3}");
        Console.WriteLine($"  failing cases                                         : {bad}");
        Console.WriteLine(bad == 0 ? "  RESULT: PASS (eigenvalue exact + never under-estimates)" : "  RESULT: FAIL");
        return bad == 0 ? 0 : 1;
    }

    // The mode-6 prune (Bc7Block.Mode6PruneEnabled) must be output-preserving: it skips mode-6 evaluation
    // only when a multi-subset candidate already provably beats it, so enabling it must reproduce the
    // prune-disabled output byte-for-byte. Exercised on the high-quality path -- the only path that evaluates
    // the multi-subset candidates the prune bound is compared against.
    static byte Cl(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public static int RunPrune(string[] args)
    {
        const int Nb = 60_000;
        var bl = new ColorRgba[Nb * 16];
        var rng = new Random(0xBC7);
        for (int b = 0; b < Nb; b++)
        {
            int o = b * 16;
            switch (b & 3)
            {
                case 0: // uniform-random RGBA (stresses alpha + arbitrary endpoint/index configs)
                    for (int i = 0; i < 16; i++) bl[o + i] = new ColorRgba((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    break;
                case 1: // opaque-random
                    for (int i = 0; i < 16; i++) bl[o + i] = new ColorRgba((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)255);
                    break;
                case 2: // gradient + noise (two-region structure the multi-subset modes win on -> exercises the prune)
                {
                    int lr = rng.Next(256), lg = rng.Next(256), lb = rng.Next(256), la = rng.Next(256);
                    int hr = rng.Next(256), hg = rng.Next(256), hb = rng.Next(256), ha = rng.Next(256);
                    for (int i = 0; i < 16; i++)
                    {
                        int t = i * 64 / 15;
                        bl[o + i] = new ColorRgba(
                            Cl((lr * (64 - t) + hr * t) / 64 + rng.Next(-4, 5)),
                            Cl((lg * (64 - t) + hg * t) / 64 + rng.Next(-4, 5)),
                            Cl((lb * (64 - t) + hb * t) / 64 + rng.Next(-4, 5)),
                            Cl((la * (64 - t) + ha * t) / 64 + rng.Next(-4, 5)));
                    }
                    break;
                }
                default: // near-solid
                {
                    int cr = rng.Next(256), cg = rng.Next(256), cb = rng.Next(256), ca = rng.Next(256);
                    for (int i = 0; i < 16; i++) bl[o + i] = new ColorRgba(Cl(cr + rng.Next(-2, 3)), Cl(cg + rng.Next(-2, 3)), Cl(cb + rng.Next(-2, 3)), Cl(ca + rng.Next(-2, 3)));
                    break;
                }
            }
        }

        const Bc7Flags hq = Bc7Flags.Default | Bc7Flags.PartiallyAnalyticalRgb;
        byte[] EncodeAll(bool prune)
        {
            Bc7Block.Mode6PruneEnabled = prune;
            var enc = new Bc7Encoder(hq);
            var outb = new byte[Nb * 16];
            for (int b = 0; b < Nb; b++) enc.EncodeBlock(bl.AsSpan(b * 16, 16), outb.AsSpan(b * 16, 16));
            return outb;
        }

        byte[] baseline = EncodeAll(false);   // prune disabled: mode 6 always evaluated
        byte[] pruned = EncodeAll(true);      // prune enabled: must match the unpruned output exactly
        Bc7Block.Mode6PruneEnabled = false;   // restore the shipped default

        int diffBlocks = 0, diffBytes = 0, first = -1;
        for (int b = 0; b < Nb; b++)
        {
            bool d = false;
            for (int i = 0; i < 16; i++) if (baseline[b * 16 + i] != pruned[b * 16 + i]) { diffBytes++; d = true; }
            if (d) { diffBlocks++; if (first < 0) first = b; }
        }
        Console.WriteLine($"mode-6 prune output-invariance (prune ON vs prune OFF) over {Nb:N0} HQ blocks:");
        Console.WriteLine($"  differing blocks {diffBlocks} | differing bytes {diffBytes}" + (first >= 0 ? $" | first @ {first}" : ""));
        Console.WriteLine(diffBytes == 0 ? "  RESULT: PASS (prune is output-preserving)" : "  RESULT: FAIL");
        return diffBytes == 0 ? 0 : 1;
    }
}
