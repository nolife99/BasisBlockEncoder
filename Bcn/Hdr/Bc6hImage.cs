// Bc6hImage.cs — whole-image / banded driver for BC6H (namespace Bcn.Hdr). Tiles an RGB half-float surface
// into 4x4 blocks and runs the per-block codec over them in block-row-major (band) order. The per-block
// encoder is unchanged; this adds the tiling + partial-edge handling a whole-image API needs.
//
// Partial edge blocks (width/height not multiples of 4) replicate the last row/column (clamp-to-edge) on
// encode — the standard artifact-minimizing convention — and are clipped to the image bounds on decode.
//
// A "band" is a contiguous range of block-rows. Bands are fully independent (no block depends on another),
// so a caller may stream output band-by-band (no need to hold the whole output) or run bands on separate
// threads. The library itself stays single-threaded.
//
// Layout: src/dst are RGB half-float *bit patterns*, 3 ushorts per pixel, row-major, `rowStride` ushorts per
// row (>= width*3). Output is 16 bytes per block, block-row-major; ByteCount(width,height) gives the size.
using System;

namespace Bcn.Hdr;

public static class Bc6hImage
{
    /// <summary>Number of 4x4 blocks covering a width x height image.</summary>
    public static int BlockCount(int width, int height) => (width + 3) / 4 * ((height + 3) / 4);

    /// <summary>Required output byte count (16 bytes per block).</summary>
    public static int ByteCount(int width, int height) => BlockCount(width, height) * 16;

    /// <summary>Encode a whole RGB-half-float surface to packed BC6H blocks (block-row-major).</summary>
    public static void Encode(scoped ReadOnlySpan<ushort> src, int width, int height, int rowStride,
        scoped Span<byte> output, Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBand(src, width, height, rowStride, 0, (height + 3) / 4, output, quality);

    /// <summary>
    /// Encode block-rows [blockRowStart, blockRowStart+blockRowCount) into <paramref name="output"/>, which
    /// holds exactly those block-rows (blockRowCount * ceil(width/4) * 16 bytes). src/height describe the full
    /// image so edge clamping is correct regardless of band. Bands are independent — safe to run concurrently.
    /// </summary>
    public static void EncodeBand(scoped ReadOnlySpan<ushort> src, int width, int height, int rowStride,
        int blockRowStart, int blockRowCount, scoped Span<byte> output, Bc6hQuality quality = Bc6hQuality.Default)
    {
        int bw = (width + 3) / 4;
        Span<ushort> tile = stackalloc ushort[48];
        int o = 0;
        for (int by = blockRowStart; by < blockRowStart + blockRowCount; by++)
            for (int bx = 0; bx < bw; bx++, o += 16)
            {
                ExtractBlock(src, width, height, rowStride, bx, by, tile);
                Bc6hEncoder.EncodeBlock(tile, output.Slice(o, 16), quality);
            }
    }

    /// <summary>Decode packed BC6H blocks back to an RGB-half-float surface (block-row-major).</summary>
    public static void Decode(scoped ReadOnlySpan<byte> blocks, int width, int height, int rowStride, scoped Span<ushort> dst)
        => DecodeBand(blocks, width, height, rowStride, 0, (height + 3) / 4, dst);

    /// <summary>Decode block-rows [blockRowStart, blockRowStart+blockRowCount) from a band's blocks into dst.</summary>
    public static void DecodeBand(scoped ReadOnlySpan<byte> blocks, int width, int height, int rowStride,
        int blockRowStart, int blockRowCount, scoped Span<ushort> dst)
    {
        int bw = (width + 3) / 4;
        Span<ushort> tile = stackalloc ushort[48];
        int o = 0;
        for (int by = blockRowStart; by < blockRowStart + blockRowCount; by++)
            for (int bx = 0; bx < bw; bx++, o += 16)
            {
                Bc6hDecoder.DecodeBlock(blocks.Slice(o, 16), tile);
                ScatterBlock(tile, width, height, rowStride, bx, by, dst);
            }
    }

    // Gather one 4x4 block into row-major texel order, clamping to the edge (replicating last row/column).
    private static void ExtractBlock(scoped ReadOnlySpan<ushort> src, int width, int height, int rowStride,
        int bx, int by, scoped Span<ushort> tile)
    {
        for (int ty = 0; ty < 4; ty++)
        {
            int sy = by * 4 + ty; if (sy >= height) sy = height - 1;
            int rb = sy * rowStride;
            for (int tx = 0; tx < 4; tx++)
            {
                int sx = bx * 4 + tx; if (sx >= width) sx = width - 1;
                int s = rb + sx * 3, t = (ty * 4 + tx) * 3;
                tile[t] = src[s]; tile[t + 1] = src[s + 1]; tile[t + 2] = src[s + 2];
            }
        }
    }

    // Write a decoded 4x4 block into dst, skipping texels outside the image (partial edge blocks).
    private static void ScatterBlock(scoped ReadOnlySpan<ushort> tile, int width, int height, int rowStride,
        int bx, int by, scoped Span<ushort> dst)
    {
        for (int ty = 0; ty < 4; ty++)
        {
            int dy = by * 4 + ty; if (dy >= height) break;
            int rb = dy * rowStride;
            for (int tx = 0; tx < 4; tx++)
            {
                int dx = bx * 4 + tx; if (dx >= width) break;
                int d = rb + dx * 3, t = (ty * 4 + tx) * 3;
                dst[d] = tile[t]; dst[d + 1] = tile[t + 1]; dst[d + 2] = tile[t + 2];
            }
        }
    }
}
