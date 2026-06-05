// For each synthetic HDR block, encode with both, decode with the managed decoder,
// and report 1/luma^2-weighted (relative) PSNR + peak PSNR per quality tier.
using System;
using System.Runtime.InteropServices;
using Bcn.Hdr;

internal static class Bc6hOracleTest
{
    const string Lib = "basis_block_encoder";
    const string SoPath = "/home/claude/bbe_pkg/runtimes/linux-x64/native/libbasis_block_encoder.so";

    [DllImport(Lib)] static extern int bbe_init();
    [DllImport(Lib)] static extern int bbe_encode_bc6h_block(ushort[] blockRgbHalf48, byte[] dst, uint quality);

    static Bc6hOracleTest()
    {
        NativeLibrary.SetDllImportResolver(typeof(Bc6hOracleTest).Assembly,
            (name, asm, path) => name == Lib ? NativeLibrary.Load(SoPath) : IntPtr.Zero);
    }

    static float HF(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);
    static ushort HB(float f) => BitConverter.HalfToUInt16Bits((Half)f);

    // Mixed HDR content: kinds 0-2 are smooth (1-subset); kinds 3-4 are two-region blocks with
    // independent endpoint pairs (2-subset territory). Moderate per-block dynamic range.
    static void Gen(ushort[] dst, Random rng)
    {
        float Lerp(float a, float b) => a + (b - a) * (float)rng.NextDouble();
        float[] MakeLo() { float M = MathF.Exp(Lerp(MathF.Log(0.1f), MathF.Log(64f))); return new[] { M * Lerp(0.4f, 1.3f), M * Lerp(0.4f, 1.3f), M * Lerp(0.4f, 1.3f) }; }
        float[] MakeHi(float[] lo) { float con = Lerp(0.35f, 2.8f); return new[] { lo[0] * con * Lerp(0.7f, 1.4f), lo[1] * con * Lerp(0.7f, 1.4f), lo[2] * con * Lerp(0.7f, 1.4f) }; }
        float[] e0 = MakeLo(), e1 = MakeHi(e0), e2 = MakeLo(), e3 = MakeHi(e2);
        int kind = rng.Next(5);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = y * 4 + x;
                bool regB = (kind == 3 && (x + y) >= 3) || (kind == 4 && x >= 2);
                float t = kind switch { 0 => 0f, 1 => x / 3f, 2 => (x + y) / 6f, 3 => (x + y) / 6f, _ => x / 3f };
                float[] lo = regB ? e2 : e0, hi = regB ? e3 : e1;
                for (int cc = 0; cc < 3; cc++)
                {
                    float v = lo[cc] + (hi[cc] - lo[cc]) * t;
                    if (v < 0) v = 0; else if (v > 65000f) v = 65000f;
                    dst[i * 3 + cc] = HB(v);
                }
            }
    }

    struct Acc { public double peakMse, relSe; public int exact, maxBd; }

    static void Score(ushort[] orig, ushort[] dec, ref Acc a, ref double maxV)
    {
        for (int i = 0; i < 16; i++)
        {
            float or = HF(orig[i * 3]), og = HF(orig[i * 3 + 1]), ob = HF(orig[i * 3 + 2]);
            float dr = HF(dec[i * 3]), dg = HF(dec[i * 3 + 1]), db = HF(dec[i * 3 + 2]);
            double er = dr - or, eg = dg - og, eb = db - ob, se = er * er + eg * eg + eb * eb;
            a.peakMse += se;
            double luma = 0.2126 * or + 0.7152 * og + 0.0722 * ob;
            a.relSe += se / (luma * luma + 1e-6);
            if (or > maxV) maxV = or; if (og > maxV) maxV = og; if (ob > maxV) maxV = ob;
            for (int c = 0; c < 3; c++) { int d = Math.Abs(orig[i * 3 + c] - dec[i * 3 + c]); if (d > a.maxBd) a.maxBd = d; if (d == 0) a.exact++; }
        }
    }

    static (double rel, double peak, double exactPct) Finish(Acc a, double maxV)
    {
        const int n = 16 * 4096 * 3;
        double peak = a.peakMse <= 0 ? 99 : 10 * Math.Log10(maxV * maxV / (a.peakMse / (16.0 * 4096)));
        double rel = a.relSe <= 0 ? 99 : 10 * Math.Log10(1.0 / (a.relSe / (16.0 * 4096)));
        return (rel, peak, 100.0 * a.exact / n);
    }

    internal static void Run()
    {
        int rc = bbe_init();
        Console.WriteLine($"BC6H encoder vs oracle  (bbe_init -> {rc})  [N=4096 mixed blocks/tier]");

        const int N = 4096;
        ushort[] block = new ushort[48], dec = new ushort[48], mydec = new ushort[48];
        byte[] enc = new byte[16], myenc = new byte[16];

        foreach (uint q in new uint[] { 0, 1, 2 })
        {
            var rng = new Random(12345);
            Acc oa = default, ma = default; double oMaxV = 0, mMaxV = 0;
            int[] myModeCount = new int[14];

            for (int b = 0; b < N; b++)
            {
                Gen(block, rng);
                Array.Clear(enc, 0, 16);
                bbe_encode_bc6h_block(block, enc, q);
                Bc6hDecoder.DecodeBlock(enc, dec);
                Bc6hEncoder.EncodeBlock(block, myenc, (Bc6hQuality)(int)q);
                Bc6hDecoder.DecodeBlock(myenc, mydec);

                int myMode = Bc6hTables.ModeLookup[myenc[0] & 0x1F];
                if (myMode >= 0) myModeCount[myMode]++;

                Score(block, dec, ref oa, ref oMaxV);
                Score(block, mydec, ref ma, ref mMaxV);
            }

            var o = Finish(oa, oMaxV); var m = Finish(ma, mMaxV);
            string tier = q == 0 ? "Fast" : q == 1 ? "Default" : "HighQuality";
            Console.WriteLine($"  q={q} ({tier,-11})");
            Console.WriteLine($"    oracle : relPSNR {o.rel,6:F2} dB | peakPSNR {o.peak,6:F2} dB | exact {o.exactPct,5:F1}% | maxBd {oa.maxBd}");
            Console.WriteLine($"    mine   : relPSNR {m.rel,6:F2} dB | peakPSNR {m.peak,6:F2} dB | exact {m.exactPct,5:F1}% | maxBd {ma.maxBd}  (Δrel {m.rel - o.rel:+0.00;-0.00} dB)");
            Console.Write("    mine modes: "); for (int k = 0; k < 14; k++) if (myModeCount[k] > 0) Console.Write($"[{k}:{myModeCount[k]}] "); Console.WriteLine();
        }
        Console.WriteLine("  (Δrel >= ~0 => managed encoder matches/beats oracle; 2-subset now active on two-region blocks)");
    }

    // Image-level driver: tiling, partial edge blocks (37x21 is non-aligned in both dims), banded == whole,
    // and a decode round-trip with a padded row stride.
    internal static void ImageCheck()
    {
        int width = 37, height = 21, rowStride = width * 3 + 5;   // padded stride exercises stride handling
        var src = new ushort[rowStride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = y * rowStride + x * 3;
                src[o + 0] = HB(50f * (0.5f + 0.4f * MathF.Sin(x * 0.10f)));
                src[o + 1] = HB(50f * (0.5f + 0.4f * MathF.Sin(y * 0.09f + 1f)));
                src[o + 2] = HB(50f * (0.5f + 0.4f * MathF.Cos((x + y) * 0.07f)));
            }

        int bw = (width + 3) / 4, bh = (height + 3) / 4, bytes = Bc6hImage.ByteCount(width, height);
        Console.WriteLine($"BC6H image  {width}x{height} (stride {rowStride}) -> {bw}x{bh} blocks, {bytes} bytes");

        var whole = new byte[bytes];
        Bc6hImage.Encode(src, width, height, rowStride, whole, Bc6hQuality.HighQuality);

        // banded encode (2 block-rows per band) must reproduce the whole-image output byte-for-byte
        var banded = new byte[bytes];
        for (int by = 0; by < bh; by += 2)
        {
            int n = Math.Min(2, bh - by);
            Bc6hImage.EncodeBand(src, width, height, rowStride, by, n, banded.AsSpan(by * bw * 16, n * bw * 16), Bc6hQuality.HighQuality);
        }
        int mism = 0;
        for (int i = 0; i < bytes; i++) if (whole[i] != banded[i]) mism++;
        Console.WriteLine($"  banded (2 rows/band) vs whole: {mism} mismatched bytes");

        // decode round-trip; peak PSNR over the valid width x height pixels
        var recon = new ushort[rowStride * height];
        Bc6hImage.Decode(whole, width, height, rowStride, recon);
        double se = 0; float maxv = 0; long n3 = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = y * rowStride + x * 3;
                for (int c = 0; c < 3; c++)
                {
                    float a = HF(src[o + c]), b = HF(recon[o + c]);
                    double d = a - b; se += d * d; if (a > maxv) maxv = a; n3++;
                }
            }
        double rmse = Math.Sqrt(se / n3);
        double psnr = 20.0 * Math.Log10(maxv / rmse);
        Console.WriteLine($"  decode round-trip: peak PSNR {psnr:F1} dB over {n3 / 3} pixels (maxVal {maxv:F1})");
    }
}