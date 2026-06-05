// Bc12ImageTest.cs — verifies the standalone BC1 / BC2 encoders and the whole-image driver
// (block counting, clamp-to-edge padding on non-4-aligned images, round-trip).
using System;
using Bcn;

namespace Bcn.Ldr;

internal static class Bc12ImageTest
{
    static uint s_rng = 0x2545F491u;
    static uint Rnd() { s_rng ^= s_rng << 13; s_rng ^= s_rng >> 17; s_rng ^= s_rng << 5; return s_rng; }
    static byte RB() => (byte)(Rnd() & 0xFF);

    public static bool Run()
    {
        Console.WriteLine();
        Console.WriteLine("BC1/BC2 + whole-image driver verification:");
        bool ok = true;

        Span<ColorRgba> px = stackalloc ColorRgba[16];
        Span<byte> blk2 = stackalloc byte[16], c8 = stackalloc byte[8];
        Span<ColorRgba> rec = stackalloc ColorRgba[16];

        // --- BC2: 4-bit alpha exactness + color half == standalone BC1 ---
        var bc2 = new Bc2Encoder(Bc1Quality.Default);
        var bc1 = new Bc1Encoder(Bc1Quality.Default);
        long alphaErr = 0, bc2ColorFails = 0; double sse = 0; long n = 0;
        for (int t = 0; t < 50_000; t++)
        {
            for (int i = 0; i < 16; i++) px[i] = new ColorRgba(RB(), RB(), RB(), RB());
            bc2.EncodeBlock(px, blk2);
            bc1.EncodeBlock(px, c8);
            if (!blk2.Slice(8, 8).SequenceEqual(c8)) bc2ColorFails++;
            LdrDecode.DecodeBc2(blk2, rec);
            for (int i = 0; i < 16; i++)
            {
                int expA = ((px[i].A + 8) / 17) * 17;        // nearest representable 4-bit alpha
                if (rec[i].A != expA) alphaErr++;
                int dr = rec[i].R - px[i].R, dg = rec[i].G - px[i].G, db = rec[i].B - px[i].B;
                sse += (double)dr * dr + dg * dg + db * db; n += 3;
            }
        }
        Console.WriteLine($"  BC2 alpha quant exact (round-to-17) failures : {alphaErr}");
        Console.WriteLine($"  BC2 color == standalone BC1 mismatches       : {bc2ColorFails}");
        Console.WriteLine($"  BC2 round-trip RGB PSNR                      : {(sse <= 0 ? 99 : 10 * Math.Log10(65025.0 / (sse / n))):F2} dB");
        ok &= alphaErr == 0 && bc2ColorFails == 0;

        // --- driver: block counting on a 13x7 image (ceil(13/4)*ceil(7/4) = 4*2 = 8 blocks) ---
        const int W = 13, H = 7, stride = W * 4;
        byte[] img = new byte[stride * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = y * stride + x * 4;
                img[o] = (byte)(x * 19); img[o + 1] = (byte)(y * 36); img[o + 2] = (byte)((x + y) * 11); img[o + 3] = (byte)(x * y * 3);
            }
        var enc = new Bc3Encoder(Bc1Quality.Default, 3);
        int bc = BlockImage.BlockCount(W, H), bytes = BlockImage.ByteCount(enc, W, H);
        bool sizeOk = bc == 8 && bytes == 128;
        Console.WriteLine($"  driver BlockCount/ByteCount (expect 8/128)   : {bc}/{bytes}{(sizeOk ? "" : "  <-- WRONG")}");
        ok &= sizeOk;
        byte[] outp = new byte[bytes];
        BlockImage.Encode(enc, img, W, H, stride, outp);

        // --- driver edge padding: 5x5 image, BC1. Block (bx=1,by=1) covers x4..7,y4..7 where only the
        //     corner texel (4,4) is in-bounds, so all 16 texels clamp to it -> a solid block. ---
        const int W2 = 5, H2 = 5, st2 = W2 * 4;
        byte[] img2 = new byte[st2 * H2];
        for (int y = 0; y < H2; y++)
            for (int x = 0; x < W2; x++)
            {
                int o = y * st2 + x * 4;
                img2[o] = (byte)(30 + x * 40); img2[o + 1] = (byte)(20 + y * 40); img2[o + 2] = (byte)(200 - x * 20); img2[o + 3] = 255;
            }
        var bc1e = new Bc1Encoder(Bc1Quality.Default);
        byte[] out2 = new byte[BlockImage.ByteCount(bc1e, W2, H2)];   // 2x2 blocks * 8 = 32
        BlockImage.Encode(bc1e, img2, W2, H2, st2, out2);
        int co = (H2 - 1) * st2 + (W2 - 1) * 4;
        var corner = new ColorRgba(img2[co], img2[co + 1], img2[co + 2], 255);
        Span<ColorRgba> crec = stackalloc ColorRgba[16];
        LdrDecode.DecodeBc1(out2.AsSpan(3 * 8, 8), crec);            // block index by*2+bx = 1*2+1 = 3
        long cornerErr = 0;
        for (int i = 0; i < 16; i++)
            cornerErr += Math.Abs(crec[i].R - corner.R) + Math.Abs(crec[i].G - corner.G) + Math.Abs(crec[i].B - corner.B);
        bool edgeOk = cornerErr / 48.0 <= 8.0;                        // solid block -> within 565 quant of the replicated texel
        Console.WriteLine($"  driver edge-pad: corner block is solid (avg |e| {cornerErr / 48.0:F2}/ch) : {(edgeOk ? "ok" : "FAIL")}");
        ok &= edgeOk;

        // --- driver whole-image round-trip PSNR (13x7 BC3) vs the clamp-padded source ---
        double ise = 0; long inn = 0; Span<ColorRgba> brec = stackalloc ColorRgba[16];
        int bw = (W + 3) / 4, bh = (H + 3) / 4, bi = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++, bi++)
            {
                LdrDecode.DecodeBc3(outp.AsSpan(bi * 16, 16), brec);
                for (int ty = 0; ty < 4; ty++)
                {
                    int sy = Math.Min(by * 4 + ty, H - 1);
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int sx = Math.Min(bx * 4 + tx, W - 1), o = sy * stride + sx * 4;
                        var q = brec[ty * 4 + tx];
                        int dr = q.R - img[o], dg = q.G - img[o + 1], db = q.B - img[o + 2], da = q.A - img[o + 3];
                        ise += (double)dr * dr + dg * dg + db * db + da * da; inn += 4;
                    }
                }
            }
        Console.WriteLine($"  driver whole-image (13x7 BC3) round-trip PSNR: {(ise <= 0 ? 99 : 10 * Math.Log10(65025.0 / (ise / inn))):F2} dB");

        // --- driver 8-wide batch path == per-block, across sizes (multiple-of-8 and remainder block counts,
        //     partial edges) and across every batching encoder (BC1 x3 qualities, BC3 x2, BC2). For these on
        //     AVX2 the driver routes through the lane=block path; the whole-image output must be byte-for-byte
        //     identical to encoding each block independently. ---
        (int w, int h)[] sizes = { (32, 32), (33, 17), (12, 12), (4, 4), (64, 1), (7, 31), (1, 64), (100, 100) };
        IBlockEncoder[] encs =
        {
            new Bc1Encoder(Bc1Quality.Fast), new Bc1Encoder(Bc1Quality.Default), new Bc1Encoder(Bc1Quality.HighQuality),
            new Bc3Encoder(Bc1Quality.Default, 3), new Bc3Encoder(Bc1Quality.HighQuality, 3), new Bc2Encoder(Bc1Quality.Default),
            new Bc4Encoder(0), new Bc4Encoder(3), new Bc5Encoder(0, 1),
        };
        long batchDiffs = 0;
        foreach (var e in encs)
            foreach (var (w, h) in sizes)
            {
                int st = w * 4, bpb = e.BytesPerBlock, nb = BlockImage.BlockCount(w, h);
                byte[] im = new byte[st * h];
                for (int i = 0; i < im.Length; i++) im[i] = RB();
                byte[] got = new byte[nb * bpb];
                BlockImage.Encode(e, im, w, h, st, got);                  // batch path on AVX2
                byte[] exp = new byte[nb * bpb];                          // per-block reference
                int ww = (w + 3) / 4, hh = (h + 3) / 4, bb = 0;
                Span<ColorRgba> tl = stackalloc ColorRgba[16];
                for (int by = 0; by < hh; by++)
                    for (int bx = 0; bx < ww; bx++, bb++)
                    {
                        for (int ty = 0; ty < 4; ty++)
                        {
                            int sy = Math.Min(by * 4 + ty, h - 1), rb = sy * st;
                            for (int tx = 0; tx < 4; tx++)
                            {
                                int sx = Math.Min(bx * 4 + tx, w - 1), o = rb + sx * 4;
                                tl[ty * 4 + tx] = new ColorRgba(im[o], im[o + 1], im[o + 2], im[o + 3]);
                            }
                        }
                        e.EncodeBlock(tl, exp.AsSpan(bb * bpb, bpb));
                    }
                if (!got.AsSpan().SequenceEqual(exp)) batchDiffs++;
            }
        Console.WriteLine($"  driver 8-wide batch == per-block ({encs.Length * sizes.Length} cases: BC1/BC3/BC2) : {batchDiffs} differing");
        ok &= batchDiffs == 0;

        Console.WriteLine(ok ? "  RESULT: PASS" : "  RESULT: FAIL");
        return ok;
    }
}
