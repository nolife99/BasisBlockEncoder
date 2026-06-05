using System;

namespace Bcn.Ldr;

internal static class Bc45Test
{
    static uint s_rng = 0x12345678u;
    static uint Rnd() { s_rng ^= s_rng << 13; s_rng ^= s_rng >> 17; s_rng ^= s_rng << 5; return s_rng; }
    static byte RB() => (byte)(Rnd() & 0xFF);

    // diverse 16-texel blocks: solid / ramp / noise / bimodal / extremes / dark / near-flat
    static void Gen(int kind, Span<ColorRgba> px)
    {
        for (int i = 0; i < 16; i++)
        {
            byte r, g, b, a;
            switch (kind % 7)
            {
                case 0: r = g = b = a = RB(); break;                                   // solid
                case 1: r = g = b = a = (byte)(i * 17); break;                          // ramp
                case 2: r = RB(); g = RB(); b = RB(); a = RB(); break;                  // noise
                case 3: { bool hi = (i & 3) < 2; byte v = hi ? (byte)(200 + (Rnd() % 56)) : (byte)(Rnd() % 50); r = g = b = a = v; } break; // bimodal
                case 4: r = g = b = a = (byte)((i & 1) == 0 ? 0 : 255); break;          // extremes
                case 5: r = g = b = a = (byte)(Rnd() % 16); break;                      // dark
                default: r = g = b = a = (byte)(120 + (Rnd() % 8)); break;              // near-flat
            }
            px[i] = new ColorRgba { R = r, G = g, B = b, A = a };
        }
    }

    static int Chan(in ColorRgba c, int ch) => ch switch { 0 => c.R, 1 => c.G, 2 => c.B, _ => c.A };

    // build the 8-entry BC4 palette + decode the encoder's selectors; return (encSSE, optSSE) over the
    // chosen endpoints. encSSE == optSSE proves the selector assignment is closest-of-8 for those endpoints.
    static (long enc, long opt) OptCheck(ReadOnlySpan<byte> block, ReadOnlySpan<ColorRgba> px, int ch)
    {
        int e0 = block[0], e1 = block[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = e0; pal[1] = e1;
        if (e0 > e1)
        {
            pal[2] = (6 * e0 + e1) / 7; pal[3] = (5 * e0 + 2 * e1) / 7; pal[4] = (4 * e0 + 3 * e1) / 7;
            pal[5] = (3 * e0 + 4 * e1) / 7; pal[6] = (2 * e0 + 5 * e1) / 7; pal[7] = (e0 + 6 * e1) / 7;
        }
        else { pal[2] = (4*e0+e1)/5; pal[3] = (3*e0+2*e1)/5; pal[4] = (2*e0+3*e1)/5; pal[5] = (e0+4*e1)/5; pal[6] = 0; pal[7] = 255; }
        ulong sel = block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16) |
                    ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);
        long enc = 0, opt = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = Chan(px[i], ch);
            int s = (int)((sel >> (3 * i)) & 7);
            int de = pal[s] - v; enc += (long)de * de;
            int best = int.MaxValue;
            for (int k = 0; k < 8; k++) { int d = pal[k] - v; int e = d * d; if (e < best) best = e; }
            opt += best;
        }
        return (enc, opt);
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "eig") return Bc7PruneTest.Run(args);
        if (args.Length > 0 && args[0] == "prune") return Bc7PruneTest.RunPrune(args);
        if (args.Length > 0 && args[0] == "bc6h") { Bc6hOracleTest.Run(); return 0; }
        if (args.Length > 0 && args[0] == "bc6himg") { Bc6hOracleTest.ImageCheck(); return 0; }

        const int N = 200_000;
        Span<ColorRgba> px = stackalloc ColorRgba[16];
        Span<byte> bScal = stackalloc byte[8], b128 = stackalloc byte[8], b256 = stackalloc byte[8];
        Span<byte> rec = stackalloc byte[16];

        long tierFails = 0, optFails = 0; long worstExcess = 0;
        double sse = 0; long nVals = 0;

        for (int t = 0; t < N; t++)
        {
            Gen(t, px);
            int ch = t & 3;

            // (1) tier byte-identity: scalar == V128 == V256
            Bc4Block.EncodeScalar(px, bScal, ch);
            Bc4Block.EncodeV128(px, b128, ch);
            Bc4Block.EncodeV256(px, b256, ch);
            if (!bScal.SequenceEqual(b128) || !bScal.SequenceEqual(b256)) tierFails++;

            // (2) selector optimality for the chosen endpoints
            var (enc, opt) = OptCheck(bScal, px, ch);
            if (enc != opt) { optFails++; if (enc - opt > worstExcess) worstExcess = enc - opt; }

            // (3) round-trip PSNR
            LdrDecode.DecodeBc4(bScal, rec);
            for (int i = 0; i < 16; i++) { int d = rec[i] - Chan(px[i], ch); sse += (double)d * d; nVals++; }
        }

        // (4) BC5 == two independent BC4 halves
        long bc5Fails = 0;
        Span<byte> bc5 = stackalloc byte[16], h0 = stackalloc byte[8], h1 = stackalloc byte[8];
        var enc5 = new Bc5Encoder(0, 1);
        for (int t = 0; t < 20_000; t++)
        {
            Gen(t * 3 + 1, px);
            enc5.EncodeBlock(px, bc5);
            Bc4Block.Encode(px, h0, 0); Bc4Block.Encode(px, h1, 1);
            if (!bc5.Slice(0, 8).SequenceEqual(h0) || !bc5.Slice(8, 8).SequenceEqual(h1)) bc5Fails++;
        }

        // (5) lane=block: EncodeAlpha8 (8 blocks/register) == 8x per-block Bc4Block.Encode, all channels.
        long mbFails = 0;
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            Span<ColorRgba> px8 = stackalloc ColorRgba[128];
            Span<byte> o8 = stackalloc byte[64], r8 = stackalloc byte[8];
            for (int t = 0; t < 40_000; t++)
            {
                for (int b = 0; b < 8; b++) { Gen(t * 8 + b, px); px.CopyTo(px8.Slice(b * 16, 16)); }
                int ch = t & 3;
                Bc4Block.EncodeAlpha8(px8, o8, ch, 8, 0);
                for (int b = 0; b < 8; b++)
                {
                    Bc4Block.Encode(px8.Slice(b * 16, 16), r8, ch);
                    if (!o8.Slice(b * 8, 8).SequenceEqual(r8)) mbFails++;
                }
            }
        }
#endif

        double mse = sse / nVals;
        double psnr = mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        Console.WriteLine($"BC4/BC5 verification over {N} blocks (+20k BC5):");
        Console.WriteLine($"  tier byte-identity (scalar==V128==V256) failures : {tierFails}");
        Console.WriteLine($"  selector-optimality failures (enc!=opt)          : {optFails}  (worst SSE excess {worstExcess})");
        Console.WriteLine($"  BC5 == two independent BC4 halves, mismatches    : {bc5Fails}");
        Console.WriteLine($"  multi-block (lane=block) == per-block, mismatches : {mbFails}  (of {40_000 * 8} blocks)");
        Console.WriteLine($"  round-trip PSNR (mixed synthetic)                : {psnr:F2} dB");
        bool bc45 = tierFails == 0 && optFails == 0 && bc5Fails == 0 && mbFails == 0;
        Console.WriteLine(bc45 ? "  RESULT: PASS" : "  RESULT: FAIL");

        bool bc13 = Bc13Test.Run();
        bool bc12img = Bc12ImageTest.Run();
        bool bc1simd = Bc1SimdTest.Run();
        return bc45 && bc13 && bc12img && bc1simd ? 0 : 1;
    }
}
