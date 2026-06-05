namespace Bcn;

using System;

public enum BlockFormat { Bc1, Bc2, Bc3, Bc4, Bc5, Bc6h, Bc7, Astc }

/// <summary>
///     Common seam for every block codec. BC7 is the first implementation; BC1–5 reuse most of the
///     same endpoint/index machinery, BC6H is HDR (different pixel type + endpoint math), and ASTC has
///     variable block dimensions — hence dimensions and bytes-per-block are descriptors, not constants.
/// </summary>
public interface IBlockEncoder
{
    BlockFormat Format { get; }
    int BlockWidth { get; }
    int BlockHeight { get; }
    int BytesPerBlock { get; }

    /// <summary>pixels.Length == BlockWidth*BlockHeight, output.Length == BytesPerBlock.</summary>
    void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output);
}

// Optional fast path: encode BatchBlocks raster-contiguous blocks in one call (lane = block SIMD).
// BlockImage uses this when SupportsBatch is true, otherwise it falls back to per-block EncodeBlock.
// BatchBlocks is the encoder's natural SIMD width (e.g. Vector<int>.Count for the width-generic paths,
// or a fixed 8 for the AVX2-only color path); the driver groups exactly that many blocks per call.
interface IBlockEncoderBatch
{
    int BatchBlocks { get; }
    bool SupportsBatch { get; }
    void EncodeBatch(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> output);
}