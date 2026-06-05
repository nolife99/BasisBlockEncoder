using Bcn.Ldr;        // LdrDecode, Bc1Quality (internal decoders via InternalsVisibleTo)
using Bcn.Bc7;        // Bc7Decoder
using MHdr = Bcn.Hdr; // Bc6hImage, Bc6hQuality
using NEnc = BasisBlockEncoder.BlockEncoder;
using NBc1Q = BasisBlockEncoder.Bc1Quality;
using NBc6hQ = BasisBlockEncoder.Bc6hQuality;
using NBc7F = BasisBlockEncoder.Bc7Flags;

namespace Bcn.Bench;

internal static class Quality
{
    public readonly record struct Row(double PsnrManaged, double PsnrNative, double BitIdentPct, bool HasNative);

    static readonly Dictionary<string, Row> Cache = new();
    static readonly object Gate = new();
    static bool _nativeReady, _nativeTried;

    static bool NativeReady()
    {
        if (!_nativeTried) { _nativeTried = true; try { NEnc.Initialize(); _nativeReady = true; } catch { _nativeReady = false; } }
        return _nativeReady;
    }

    public static int BytesPerBlock(string fmt) => fmt is "Bc1" or "Bc4" ? 8 : 16;

    public static Row Get(string fmt, string artwork)
    {
        string key = fmt + "|" + artwork;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var c)) return c;
            Row row = fmt == "Bc6h" ? Hdr(artwork) : Ldr(fmt, artwork);
            Cache[key] = row;
            return row;
        }
    }

    static double Db8(long sse, long samples) => sse == 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / ((double)sse / samples));

    static int BitIdent(byte[] a, byte[] b, int bpb)
    {
        int blocks = a.Length / bpb, id = 0;
        for (int i = 0; i < blocks; i++)
            if (a.AsSpan(i * bpb, bpb).SequenceEqual(b.AsSpan(i * bpb, bpb))) id++;
        return blocks == 0 ? 0 : id;
    }

    static Row Ldr(string fmt, string artwork)
    {
        var im = Images.LoadLdr(artwork);
        int bpb = BytesPerBlock(fmt), bw = (im.W + 3) / 4, bh = (im.H + 3) / 4, blocks = bw * bh;
        var dstM = new byte[blocks * bpb];
        EncodeLdrManaged(fmt, im, dstM);
        bool hasNative = fmt != "Bc2" && NativeReady();
        byte[] dstN = hasNative ? new byte[blocks * bpb] : Array.Empty<byte>();
        if (hasNative) EncodeLdrNative(fmt, im, dstN);

        double pM = PsnrLdr(fmt, im, dstM);
        double pN = hasNative ? PsnrLdr(fmt, im, dstN) : double.NaN;
        double bit = hasNative ? 100.0 * BitIdent(dstM, dstN, bpb) / blocks : double.NaN;
        return new Row(pM, pN, bit, hasNative);
    }

    static void EncodeLdrManaged(string fmt, LdrImage im, byte[] dst)
    {
        switch (fmt)
        {
            case "Bc1": BlockImage.EncodeBc1(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc1Quality.HighQuality); break;
            case "Bc2": BlockImage.EncodeBc2(im.Rgba, im.W, im.H, im.StrideBytes, dst); break;
            case "Bc3": BlockImage.EncodeBc3(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc1Quality.HighQuality); break;
            case "Bc4": BlockImage.EncodeBc4(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0); break;
            case "Bc5": BlockImage.EncodeBc5(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0, 1); break;
            case "Bc7": BlockImage.EncodeBc7(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc7Flags.Default); break;
        }
    }

    static void EncodeLdrNative(string fmt, LdrImage im, byte[] dst)
    {
        switch (fmt)
        {
            case "Bc1": NEnc.EncodeBc1(im.Rgba, im.W, im.H, im.StrideBytes, dst, NBc1Q.HighQuality); break;
            case "Bc3": NEnc.EncodeBc3(im.Rgba, im.W, im.H, im.StrideBytes, dst, NBc1Q.HighQuality); break;
            case "Bc4": NEnc.EncodeBc4(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0); break;
            case "Bc5": NEnc.EncodeBc5(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0, 1); break;
            case "Bc7": NEnc.EncodeBc7(im.Rgba, im.W, im.H, im.StrideBytes, dst, NBc7F.Default); break;
        }
    }

    static void Gather(LdrImage im, int bx, int by, Span<ColorRgba> px)
    {
        for (int t = 0; t < 16; t++)
        {
            int o = ((by * 4 + (t >> 2)) * im.W + (bx * 4 + (t & 3))) * 4;
            px[t] = new ColorRgba(im.Rgba[o], im.Rgba[o + 1], im.Rgba[o + 2], im.Rgba[o + 3]);
        }
    }

    static double PsnrLdr(string fmt, LdrImage im, byte[] enc)
    {
        int bpb = BytesPerBlock(fmt), bw = (im.W + 3) / 4, bh = (im.H + 3) / 4;
        long sse = 0, samples = 0;
        Span<ColorRgba> src = stackalloc ColorRgba[16];
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        Span<byte> b0 = stackalloc byte[16];
        Span<byte> b1 = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var blk = enc.AsSpan((by * bw + bx) * bpb, bpb);
                Gather(im, bx, by, src);
                switch (fmt)
                {
                    case "Bc1": LdrDecode.DecodeBc1(blk, dec); AddRgb(src, dec, ref sse, ref samples, alpha: false); break;
                    case "Bc2": LdrDecode.DecodeBc2(blk, dec); AddRgb(src, dec, ref sse, ref samples, alpha: true); break;
                    case "Bc3": LdrDecode.DecodeBc3(blk, dec); AddRgb(src, dec, ref sse, ref samples, alpha: true); break;
                    case "Bc4":
                        LdrDecode.DecodeBc4(blk, b0);
                        for (int t = 0; t < 16; t++) { int d = src[t].R - b0[t]; sse += d * d; }
                        samples += 16; break;
                    case "Bc5":
                        LdrDecode.DecodeBc5(blk, b0, b1);
                        for (int t = 0; t < 16; t++) { int d0 = src[t].R - b0[t], d1 = src[t].G - b1[t]; sse += d0 * d0 + d1 * d1; }
                        samples += 32; break;
                    case "Bc7":
                        sse += Bc7Decoder.ActualSse(blk, src); samples += 64; break;
                }
            }
        return Db8(sse, samples);
    }

    static void AddRgb(ReadOnlySpan<ColorRgba> s, ReadOnlySpan<ColorRgba> d, ref long sse, ref long samples, bool alpha)
    {
        for (int t = 0; t < 16; t++)
        {
            int dr = s[t].R - d[t].R, dg = s[t].G - d[t].G, db = s[t].B - d[t].B;
            sse += dr * dr + dg * dg + db * db;
            if (alpha) { int da = s[t].A - d[t].A; sse += da * da; }
        }
        samples += alpha ? 64 : 48;
    }

    static Row Hdr(string artwork)
    {
        var im = Images.LoadHdr(artwork);
        int bw = (im.W + 3) / 4, bh = (im.H + 3) / 4, blocks = bw * bh;
        var dstM = new byte[blocks * 16];
        MHdr.Bc6hImage.Encode(im.Rgb, im.W, im.H, im.StrideUshorts, dstM, MHdr.Bc6hQuality.Default);
        bool hasNative = NativeReady();
        byte[] dstN = hasNative ? new byte[blocks * 16] : Array.Empty<byte>();
        if (hasNative) NEnc.EncodeBc6h(im.Rgb, im.W, im.H, im.StrideUshorts * sizeof(ushort), dstN, NBc6hQ.Default);

        double pM = SnrHdr(im, dstM);
        double pN = hasNative ? SnrHdr(im, dstN) : double.NaN;
        double bit = hasNative ? 100.0 * BitIdent(dstM, dstN, 16) / blocks : double.NaN;
        return new Row(pM, pN, bit, hasNative);
    }

    static double SnrHdr(HdrImage im, byte[] enc)
    {
        var dec = new ushort[(long)im.W * im.H * 3];
        MHdr.Bc6hImage.Decode(enc, im.W, im.H, im.StrideUshorts, dec);
        double sig = 0, err = 0;
        for (int i = 0; i < dec.Length; i++)
        {
            float r = (float)BitConverter.UInt16BitsToHalf(im.Rgb[i]);
            float e = r - (float)BitConverter.UInt16BitsToHalf(dec[i]);
            sig += r * r; err += e * e;
        }
        return err <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(sig / err);
    }
}