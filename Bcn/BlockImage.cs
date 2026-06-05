// BlockImage.cs — Tiles an RGBA8 image into 4x4 blocks and runs any IBlockEncoder over them in 
// row-major order, producing packed block bytes.
//
// Partial edge blocks (when width/height aren't multiples of 4) are filled by clamping texel
// coordinates to the image edge — i.e. replicating the last row/column — which is the standard,
// artifact-minimizing convention for block-compressing non-aligned images.

namespace Bcn;

using System;
using Ldr;

public static class BlockImage
{
    /// <summary>Number of 4x4 blocks needed to cover a width x height image.</summary>
    public static int BlockCount(int width, int height) => (width + 3) / 4 * ((height + 3) / 4);

    /// <summary>Required output byte count for the given encoder and image dimensions.</summary>
    public static int ByteCount(IBlockEncoder encoder, int width, int height)
        => BlockCount(width, height) * encoder.BytesPerBlock;

    /// <summary>
    ///     Encode an RGBA8 image (row-major, <paramref name="rowStride" /> bytes per row) to packed blocks.
    ///     output.Length must be at least ByteCount(encoder, width, height).
    /// </summary>
    public static void Encode(IBlockEncoder encoder,
        scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, bpb = encoder.BytesPerBlock;
        var total = bw * bh;

        // Batch fast path: the encoder packs BatchBlocks blocks per call (lane=block SIMD). Opted-in
        // encoders only. N consecutive raster-order blocks are independent and land in N*bpb contiguous
        // output bytes regardless of row crossings; each encoder owns its block layout.
        if (encoder is IBlockEncoderBatch batch && batch.SupportsBatch)
        {
            var n = batch.BatchBlocks;
            Span<ColorRgba> pxN = stackalloc ColorRgba[n * 16];
            int groups = total / n, gsz = n * bpb;
            for (var g = 0; g < groups; g++)
            {
                var baseBlk = g * n;
                for (var lb = 0; lb < n; lb++)
                {
                    var blk = baseBlk + lb;
                    ExtractBlock(rgba, width, height, rowStride, blk % bw, blk / bw, pxN.Slice(lb * 16, 16));
                }

                batch.EncodeBatch(pxN, output.Slice(baseBlk * bpb, gsz));
            }

            Span<ColorRgba> rem = stackalloc ColorRgba[16];
            for (var blk = groups * n; blk < total; blk++)
            {
                ExtractBlock(rgba, width, height, rowStride, blk % bw, blk / bw, rem);
                encoder.EncodeBlock(rem, output.Slice(blk * bpb, bpb));
            }

            return;
        }

        Span<ColorRgba> tile = stackalloc ColorRgba[16];
        for (var blk = 0; blk < total; blk++)
        {
            ExtractBlock(rgba, width, height, rowStride, blk % bw, blk / bw, tile);
            encoder.EncodeBlock(tile, output.Slice(blk * bpb, bpb));
        }
    }

    // Gather one 4x4 block from the image into row-major texel order, clamping to the edge (replicating the
    // last row/column) for partial edge blocks.
    static void ExtractBlock(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        int bx,
        int by,
        scoped Span<ColorRgba> tile)
    {
        for (var ty = 0; ty < 4; ty++)
        {
            var sy = by * 4 + ty;
            if (sy >= height) sy = height - 1; // clamp-to-edge (replicate)
            var rowBase = sy * rowStride;
            for (var tx = 0; tx < 4; tx++)
            {
                var sx = bx * 4 + tx;
                if (sx >= width) sx = width - 1;
                var o = rowBase + sx * 4;
                tile[ty * 4 + tx] = new(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
            }
        }
    }

    // ---- per-format convenience: drop-in for BasisBlockEncoder.BlockEncoder.EncodeBcN ----

    public static void EncodeBc1(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        Bc1Quality quality = Bc1Quality.Default)
        => Encode(new Bc1Encoder(quality), rgba, width, height, rowStride, output);

    public static void EncodeBc2(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        Bc1Quality colorQuality = Bc1Quality.Default)
        => Encode(new Bc2Encoder(colorQuality), rgba, width, height, rowStride, output);

    public static void EncodeBc3(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        Bc1Quality colorQuality = Bc1Quality.Default,
        int alphaChannel = 3)
        => Encode(new Bc3Encoder(colorQuality, alphaChannel), rgba, width, height, rowStride, output);

    public static void EncodeBc4(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        int channel = 0)
        => Encode(new Bc4Encoder(channel), rgba, width, height, rowStride, output);

    public static void EncodeBc5(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        int channel0 = 0,
        int channel1 = 1)
        => Encode(new Bc5Encoder(channel0, channel1), rgba, width, height, rowStride, output);

    public static void EncodeBc7(scoped ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int rowStride,
        scoped Span<byte> output,
        Bc7Flags flags = Bc7Flags.DefaultFast)
        => Encode(new Bc7Encoder(flags), rgba, width, height, rowStride, output);
}