namespace Bcn.Ldr;

using System;
using System.Numerics;

/// <summary>BC3 / DXT5: 16 RGBA texels -> a 16-byte block (BC4 alpha + BC1 color).</summary>
public sealed class Bc3Encoder : IBlockEncoder, IBlockEncoderBatch
{
    readonly int _alphaChannel;

    readonly Bc1Quality _colorQuality;

    /// <param name="colorQuality">Quality level for the BC1 color half.</param>
    /// <param name="alphaChannel">Channel stored in the BC4 alpha half (default A).</param>
    public Bc3Encoder(Bc1Quality colorQuality = Bc1Quality.Default, int alphaChannel = 3)
    {
        _colorQuality = colorQuality;
        _alphaChannel = alphaChannel;
    }

    /// <summary>BC3 descriptor: 4x4 texels, 16 bytes/block.</summary>
    public BlockFormat Format => BlockFormat.Bc3;

    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 16;

    /// <summary>Encode 16 texels (row-major) to a 16-byte BC3 block.</summary>
    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
    {
        Bc4Block.Encode(pixels, output.Slice(0, 8), _alphaChannel); // alpha -> bytes 0..7
        Bc1Block.EncodeBlock(pixels, output.Slice(8, 8), _colorQuality); // color -> bytes 8..15
    }

    // Both halves are width-generic (lane=block): the BC4 alpha kernel and the BC1 color kernel each batch
    // Vector<int>.Count blocks at the native vector width
    bool IBlockEncoderBatch.SupportsBatch => Vector.IsHardwareAccelerated;
    int IBlockEncoderBatch.BatchBlocks => Vector<int>.Count;

    void IBlockEncoderBatch.EncodeBatch(scoped ReadOnlySpan<ColorRgba> px, scoped Span<byte> output)
    {
        Bc4Block.EncodeAlphaBatch(px, output, _alphaChannel, 16, 0); // alpha halves
        Bc1Block.EncodeColorStrided8(px, output, 16, 8, _colorQuality); // color halves
    }
}