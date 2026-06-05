using System;

namespace Bcn.Ldr;

/// <summary>BC1 / DXT1: 16 RGB texels -> an 8-byte 4-color color block (alpha ignored).</summary>
public sealed class Bc1Encoder : IBlockEncoder, IBlockEncoder8
{
    private readonly Bc1Quality _quality;

    /// <param name="quality">Fast, Default, or HighQuality.</param>
    public Bc1Encoder(Bc1Quality quality = Bc1Quality.Default) => _quality = quality;

    public BlockFormat Format => BlockFormat.Bc1;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 8;

    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
        => Bc1Block.EncodeBlock(pixels, output, _quality);

    // The lane=block path is substantially faster on AVX2 for every quality, including Fast. It opts in
    // whenever V256 hardware is present.
    bool IBlockEncoder8.SupportsBatch8 =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
#else
        false;
#endif

    void IBlockEncoder8.EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8)
    {
#if NET8_0_OR_GREATER
        Bc1Block.EncodeBlocks8(px8, out8, _quality);
#else
        throw new NotSupportedException();
#endif
    }
}

/// <summary>BC2 / DXT3: 16 RGBA texels -> a 16-byte block (16 explicit 4-bit alphas + BC1 color).</summary>
public sealed class Bc2Encoder : IBlockEncoder, IBlockEncoder8
{
    private readonly Bc1Quality _colorQuality;

    /// <param name="colorQuality">Quality for the BC1 color half.</param>
    public Bc2Encoder(Bc1Quality colorQuality = Bc1Quality.Default) => _colorQuality = colorQuality;

    public BlockFormat Format => BlockFormat.Bc2;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 16;

    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
    {
        // bytes 0..7: explicit 4-bit alpha, two texels per byte (texel 2b -> low nibble, 2b+1 -> high).
        // 4-bit quantization is round(A/17): (A+8)/17, which round-trips exactly with the *17 decode.
        for (int b = 0; b < 8; b++)
        {
            int a0 = (pixels[2 * b].A + 8) / 17;
            int a1 = (pixels[2 * b + 1].A + 8) / 17;
            output[b] = (byte)(a0 | (a1 << 4));
        }
        // bytes 8..15: BC1 4-color color block.
        Bc1Block.EncodeBlock(pixels, output.Slice(8, 8), _colorQuality);
    }

    // BC1 color half batches 8-wide; the explicit 4-bit alpha stays per-block (trivial). Bit-identical.
    bool IBlockEncoder8.SupportsBatch8 =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
#else
        false;
#endif

    void IBlockEncoder8.EncodeBlocks8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8)
    {
#if NET8_0_OR_GREATER
        for (int blk = 0; blk < 8; blk++)
        {
            ReadOnlySpan<ColorRgba> px = px8.Slice(blk * 16, 16);
            Span<byte> a = out8.Slice(blk * 16, 8);
            for (int b = 0; b < 8; b++)
                a[b] = (byte)(((px[2 * b].A + 8) / 17) | (((px[2 * b + 1].A + 8) / 17) << 4));
        }
        Bc1Block.EncodeColorStrided8(px8, out8, 16, 8, _colorQuality);
#else
        throw new NotSupportedException();
#endif
    }
}