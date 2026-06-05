using System;

namespace Bcn.Ldr;

/// <summary>BC4 / DXT5A: compresses a single channel of 16 texels to an 8-byte block.</summary>
public sealed class Bc4Encoder : IBlockEncoder, IBlockEncoder8
{
    /// <summary>BC4 descriptor: 4x4 texels, 8 bytes/block.</summary>
    public BlockFormat Format => BlockFormat.Bc4;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 8;

    private readonly int _channel;

    /// <param name="channel">Source channel to compress: 0=R, 1=G, 2=B, 3=A (default R).</param>
    public Bc4Encoder(int channel = 0) => _channel = channel;

    /// <summary>Encode 16 texels (row-major) to an 8-byte BC4 block.</summary>
    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
        => Bc4Block.Encode(pixels, output, _channel);

    bool IBlockEncoder8.SupportsBatch8 =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
#else
        false;
#endif

    void IBlockEncoder8.EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8)
    {
#if NET8_0_OR_GREATER
        Bc4Block.EncodeAlpha8(px8, out8, _channel, 8, 0);
#else
        throw new NotSupportedException();
#endif
    }
}

/// <summary>BC5 / 3Dc: two channels (default R,G) as two BC4 blocks -> a 16-byte block.</summary>
public sealed class Bc5Encoder : IBlockEncoder, IBlockEncoder8
{
    /// <summary>BC5 descriptor: 4x4 texels, 16 bytes/block.</summary>
    public BlockFormat Format => BlockFormat.Bc5;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 16;

    private readonly int _channel0, _channel1;

    /// <param name="channel0">First channel (bytes 0..7), default R.</param>
    /// <param name="channel1">Second channel (bytes 8..15), default G.</param>
    public Bc5Encoder(int channel0 = 0, int channel1 = 1) { _channel0 = channel0; _channel1 = channel1; }

    /// <summary>Encode 16 texels to a 16-byte BC5 block (channel0 then channel1, each an optimal BC4).</summary>
    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
    {
        Bc4Block.Encode(pixels, output.Slice(0, 8), _channel0);
        Bc4Block.Encode(pixels, output.Slice(8, 8), _channel1);
    }

    bool IBlockEncoder8.SupportsBatch8 =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
#else
        false;
#endif

    void IBlockEncoder8.EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8)
    {
#if NET8_0_OR_GREATER
        Bc4Block.EncodeAlpha8(px8, out8, _channel0, 16, 0);  // channel0 -> bytes 0..7
        Bc4Block.EncodeAlpha8(px8, out8, _channel1, 16, 8);  // channel1 -> bytes 8..15
#else
        throw new NotSupportedException();
#endif
    }
}