using System;

namespace Bcn.Ldr;

internal static class Bc13Test
{
    static uint s_rng = 0x9E3779B9u;
    static uint Rnd() { s_rng ^= s_rng << 13; s_rng ^= s_rng >> 17; s_rng ^= s_rng << 5; return s_rng; }
    static byte RB() => (byte)(Rnd() & 0xFF);
    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // color-oriented blocks: solid / RGB gradient / noise / two-tone / ramp / near-flat / gray ramp / banded
    static void Gen(int kind, Span<ColorRgba> px)
    {
        switch (kind % 8)
        {
            case 0: { var c = new ColorRgba { R = RB(), G = RB(), B = RB(), A = RB() }; for (int i = 0; i < 16; i++) px[i] = c; } break;
            case 1: for (int i = 0; i < 16; i++) px[i] = new ColorRgba { R = (byte)(i * 17), G = (byte)(255 - i * 17), B = (byte)((i * 9) & 0xFF), A = 255 }; break;
            case 2: for (int i = 0; i < 16; i++) px[i] = new ColorRgba { R = RB(), G = RB(), B = RB(), A = RB() }; break;
            case 3: { var a = new ColorRgba { R = RB(), G = RB(), B = RB(), A = 255 }; var b = new ColorRgba { R = RB(), G = RB(), B = RB(), A = 255 }; for (int i = 0; i < 16; i++) px[i] = (i & 3) < 2 ? a : b; } break;
            case 4: for (int i = 0; i < 16; i++) { int t = i * 16; px[i] = new ColorRgba { R = (byte)t, G = (byte)(t / 2), B = Clamp(255 - t), A = 255 }; } break;
            case 5: { int br = RB(), bg = RB(), bb = RB(); for (int i = 0; i < 16; i++) px[i] = new ColorRgba { R = Clamp(br + (int)(Rnd() % 16) - 8), G = Clamp(bg + (int)(Rnd() % 16) - 8), B = Clamp(bb + (int)(Rnd() % 16) - 8), A = 255 }; } break;
            case 6: for (int i = 0; i < 16; i++) { byte v = (byte)(i * 17); px[i] = new ColorRgba { R = v, G = v, B = v, A = 255 }; } break;
            default: for (int i = 0; i < 16; i++) px[i] = new ColorRgba { R = (byte)(Rnd() % 64), G = (byte)(64 + Rnd() % 64), B = (byte)(128 + Rnd() % 64), A = 255 }; break;
        }
    }

    public static bool Run()
    {
        const int N = 100_000;
        var quals = new[] { Bc1Quality.Fast, Bc1Quality.Default, Bc1Quality.HighQuality };
        double[] psnr = new double[3];
        long fourColorFails = 0;

        Span<ColorRgba> px = stackalloc ColorRgba[16];
        Span<byte> blk = stackalloc byte[8];
        Span<ColorRgba> rec = stackalloc ColorRgba[16];

        for (int qi = 0; qi < 3; qi++)
        {
            double sse = 0; long n = 0;
            for (int t = 0; t < N; t++)
            {
                Gen(t, px);
                Bc1Block.EncodeBlock(px, blk, quals[qi]);
                int c0 = blk[0] | (blk[1] << 8), c1 = blk[2] | (blk[3] << 8);
                if (c0 <= c1) fourColorFails++;                 // strictly color0>color1 => 4-color only
                LdrDecode.DecodeBc1(blk, rec);
                for (int i = 0; i < 16; i++)
                {
                    int dr = rec[i].R - px[i].R, dg = rec[i].G - px[i].G, db = rec[i].B - px[i].B;
                    sse += (double)dr * dr + dg * dg + db * db; n += 3;
                }
            }
            psnr[qi] = sse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (sse / n));
        }
        bool mono = psnr[1] >= psnr[0] - 1e-9 && psnr[2] >= psnr[1] - 1e-9;

        // BC3 == BC4-alpha + BC1-color (composition)
        long bc3Fails = 0;
        Span<byte> bc3 = stackalloc byte[16], a8 = stackalloc byte[8], c8 = stackalloc byte[8];
        var enc3 = new Bc3Encoder(Bc1Quality.Default, 3);
        for (int t = 0; t < 20_000; t++)
        {
            Gen(t * 2 + 1, px);
            enc3.EncodeBlock(px, bc3);
            Bc4Block.Encode(px, a8, 3);
            Bc1Block.EncodeBlock(px, c8, Bc1Quality.Default);
            if (!bc3.Slice(0, 8).SequenceEqual(a8) || !bc3.Slice(8, 8).SequenceEqual(c8)) bc3Fails++;
        }

        // BC3 round-trip RGBA PSNR (with varying alpha)
        double sseA = 0; long nA = 0;
        Span<ColorRgba> rec3 = stackalloc ColorRgba[16];
        for (int t = 0; t < 20_000; t++)
        {
            Gen(t * 2, px);
            for (int i = 0; i < 16; i++) px[i] = new ColorRgba { R = px[i].R, G = px[i].G, B = px[i].B, A = (byte)((i * 15 + t) & 0xFF) };
            enc3.EncodeBlock(px, bc3);
            LdrDecode.DecodeBc3(bc3, rec3);
            for (int i = 0; i < 16; i++)
            {
                int dr = rec3[i].R - px[i].R, dg = rec3[i].G - px[i].G, db = rec3[i].B - px[i].B, da = rec3[i].A - px[i].A;
                sseA += (double)dr * dr + dg * dg + db * db + da * da; nA += 4;
            }
        }
        double psnr3 = sseA <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (sseA / nA));

        Console.WriteLine();
        Console.WriteLine($"BC1/BC3 verification over {N} color blocks per level:");
        Console.WriteLine($"  4-color-always (color0>color1) failures   : {fourColorFails}");
        Console.WriteLine($"  BC1 round-trip PSNR  Fast/Default/HQ      : {psnr[0]:F2} / {psnr[1]:F2} / {psnr[2]:F2} dB");
        Console.WriteLine($"  monotonic (HQ>=Default>=Fast)             : {(mono ? "yes" : "NO")}");
        Console.WriteLine($"  BC3 == BC4-alpha + BC1-color mismatches   : {bc3Fails}");
        Console.WriteLine($"  BC3 round-trip RGBA PSNR (Default)        : {psnr3:F2} dB");
        bool ok = fourColorFails == 0 && mono && bc3Fails == 0;
        Console.WriteLine(ok ? "  RESULT: PASS" : "  RESULT: FAIL");
        return ok;
    }
}
