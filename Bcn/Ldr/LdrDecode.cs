// LdrDecode.cs — reference decoders for the BC1/3/4/5 family (namespace Bcn.Ldr).
//
// Used to compute reconstruction PSNR and to check round-trip correctness,
// exactly as Bc7Decoder does for BC7. Decode is the GPU-defined, encoder-independent mapping.
using System;
using System.Runtime.InteropServices;

namespace Bcn.Ldr;

internal static class LdrDecode
{
    // ---- BC4: 8-byte single-channel block -> 16 channel values ----
    public static void DecodeBc4(scoped ReadOnlySpan<byte> block, scoped Span<byte> outVals)
    {
        int e0 = block[0], e1 = block[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = e0; pal[1] = e1;
        if (e0 > e1)
        {
            pal[2] = (6 * e0 + 1 * e1) / 7; pal[3] = (5 * e0 + 2 * e1) / 7; pal[4] = (4 * e0 + 3 * e1) / 7;
            pal[5] = (3 * e0 + 4 * e1) / 7; pal[6] = (2 * e0 + 5 * e1) / 7; pal[7] = (1 * e0 + 6 * e1) / 7;
        }
        else
        {
            pal[2] = (4 * e0 + 1 * e1) / 5; pal[3] = (3 * e0 + 2 * e1) / 5;
            pal[4] = (2 * e0 + 3 * e1) / 5; pal[5] = (1 * e0 + 4 * e1) / 5; pal[6] = 0; pal[7] = 255;
        }
        ulong sel = block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16) |
                    ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);
        for (int i = 0; i < 16; i++) outVals[i] = (byte)pal[(int)((sel >> (3 * i)) & 7)];
    }

    // ---- BC5: 16-byte block -> two channels (16 values each) ----
    public static void DecodeBc5(scoped ReadOnlySpan<byte> block, scoped Span<byte> ch0, scoped Span<byte> ch1)
    {
        DecodeBc4(block.Slice(0, 8), ch0);
        DecodeBc4(block.Slice(8, 8), ch1);
    }

    private static int Scale5To8(int v) => (v << 3) | (v >> 2);
    private static int Scale6To8(int v) => (v << 2) | (v >> 4);

    // ---- BC1: 8-byte color block -> 16 RGBA texels (A=255) ----
    public static void DecodeBc1(scoped ReadOnlySpan<byte> block, scoped Span<ColorRgba> outPx)
    {
        int c0 = block[0] | (block[1] << 8);
        int c1 = block[2] | (block[3] << 8);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], b = stackalloc int[4];
        r[0] = Scale5To8((c0 >> 11) & 31); g[0] = Scale6To8((c0 >> 5) & 63); b[0] = Scale5To8(c0 & 31);
        r[1] = Scale5To8((c1 >> 11) & 31); g[1] = Scale6To8((c1 >> 5) & 63); b[1] = Scale5To8(c1 & 31);
        if (c0 > c1)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
            r[3] = g[3] = b[3] = 0;
        }
        uint sel = block[4] | ((uint)block[5] << 8) | ((uint)block[6] << 16) | ((uint)block[7] << 24);
        for (int i = 0; i < 16; i++)
        {
            int sIdx = (int)((sel >> (2 * i)) & 3);
            outPx[i] = new ColorRgba { R = (byte)r[sIdx], G = (byte)g[sIdx], B = (byte)b[sIdx], A = 255 };
        }
    }

    // ---- BC3: 16-byte block -> 16 RGBA texels (BC4 alpha + BC1 color) ----
    public static void DecodeBc3(scoped ReadOnlySpan<byte> block, scoped Span<ColorRgba> outPx)
    {
        Span<byte> alpha = stackalloc byte[16];
        DecodeBc4(block.Slice(0, 8), alpha);
        DecodeBc1(block.Slice(8, 8), outPx);
        // The color decode already wrote R/G/B (and A=255) for every texel; reinterpret the texels as
        // bytes and overwrite only the A lane (offset 3 of each 4-byte ColorRgba) with the BC4 alpha.
        Span<byte> rgba = MemoryMarshal.AsBytes(outPx);
        for (int i = 0; i < 16; i++) rgba[i * 4 + 3] = alpha[i];
    }

    // ---- BC2: 16-byte block -> 16 RGBA texels (BC1 color + explicit 4-bit alpha) ----
    public static void DecodeBc2(scoped ReadOnlySpan<byte> block, scoped Span<ColorRgba> outPx)
    {
        DecodeBc1(block.Slice(8, 8), outPx);
        Span<byte> rgba = MemoryMarshal.AsBytes(outPx);
        for (int b = 0; b < 8; b++)
        {
            rgba[2 * b * 4 + 3] = (byte)((block[b] & 0x0F) * 17);     // low nibble -> texel 2b
            rgba[(2 * b + 1) * 4 + 3] = (byte)((block[b] >> 4) * 17);   // high nibble -> texel 2b+1
        }
    }
}
