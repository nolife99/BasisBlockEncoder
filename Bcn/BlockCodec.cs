using System;

namespace Bcn;

public enum BlockFormat { Bc1, Bc2, Bc3, Bc4, Bc5, Bc6h, Bc7, Astc }

/// <summary>
/// Common seam for every block codec. BC7 is the first implementation; BC1–5 reuse most of the
/// same endpoint/index machinery, BC6H is HDR (different pixel type + endpoint math), and ASTC has
/// variable block dimensions — hence dimensions and bytes-per-block are descriptors, not constants.
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

// Optional fast path: encode 8 block-contiguous blocks in one call (lane = block SIMD). BlockImage uses
// this when SupportsBatch8 is true and the format is 8 bytes/block, otherwise it falls back
// to per-block EncodeBlock
internal interface IBlockEncoder8
{
    bool SupportsBatch8 { get; }
    void EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8);
}