// Bc3Encoder.cs — public BC3 / DXT5 block encoder (namespace Bcn.Ldr).
//
// BC3 = optimal BC4 alpha block (bytes 0..7, channel A) + BC1 4-color color block (bytes 8..15).
// The alpha half is byte-identical to native and provably optimal; the color half is the BC1 4-color
// encoder (3-color forbidden, so always GPU-safe). Color quality is selectable.
using System;

namespace Bcn.Ldr;

/// <summary>BC3 / DXT5: 16 RGBA texels -> a 16-byte block (BC4 alpha + BC1 color).</summary>
public sealed class Bc3Encoder : IBlockEncoder, IBlockEncoder8
{
    /// <summary>BC3 descriptor: 4x4 texels, 16 bytes/block.</summary>
    public BlockFormat Format => BlockFormat.Bc3;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 16;

    private readonly Bc1Quality _colorQuality;
    private readonly int _alphaChannel;

    /// <param name="colorQuality">Quality level for the BC1 color half.</param>
    /// <param name="alphaChannel">Channel stored in the BC4 alpha half (default A).</param>
    public Bc3Encoder(Bc1Quality colorQuality = Bc1Quality.Default, int alphaChannel = 3)
    {
        _colorQuality = colorQuality;
        _alphaChannel = alphaChannel;
    }

    /// <summary>Encode 16 texels (row-major) to a 16-byte BC3 block.</summary>
    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
    {
        Bc4Block.Encode(pixels, output.Slice(0, 8), _alphaChannel);   // alpha -> bytes 0..7
        Bc1Block.EncodeBlock(pixels, output.Slice(8, 8), _colorQuality); // color -> bytes 8..15
    }

    // The BC1 color half batches 8-wide (lane=block); the BC4 alpha half stays per-block (already
    // byte-identical to native and cheap). Output is bit-identical to 8x EncodeBlock.
    bool IBlockEncoder8.SupportsBatch8 =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
#else
        false;
#endif

    void IBlockEncoder8.EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8)
    {
#if NET8_0_OR_GREATER
        Bc4Block.EncodeAlpha8(px8, out8, _alphaChannel, 16, 0);        // alpha halves, 8-wide
        Bc1Block.EncodeColorStrided8(px8, out8, 16, 8, _colorQuality); // color halves, 8-wide
#else
        throw new NotSupportedException();
#endif
    }
}
