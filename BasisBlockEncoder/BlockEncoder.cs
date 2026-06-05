using System;
using System.Runtime.InteropServices;

namespace BasisBlockEncoder;

/// <summary>
/// Managed CPU block compressor for BC1/BC3/BC4/BC5/BC6H/BC7.
/// </summary>
public static class BlockEncoder
{
    // analytical BC7 bits implemented by the engine (drops NonAnalyticalRgb/Rgba = 1024/2048)
    private const uint Bc7AnalyticalMask = 0x3FF;

    private static Bcn.Ldr.Bc1Quality MapBc1(Bc1Quality q)
        => q == Bc1Quality.HighQuality ? Bcn.Ldr.Bc1Quality.HighQuality : Bcn.Ldr.Bc1Quality.Fast;

    private static Bcn.Hdr.Bc6hQuality MapBc6h(Bc6hQuality q) => q switch
    {
        Bc6hQuality.Fast => Bcn.Hdr.Bc6hQuality.Fast,
        Bc6hQuality.HighQuality => Bcn.Hdr.Bc6hQuality.HighQuality,
        _ => Bcn.Hdr.Bc6hQuality.Default,
    };

    private static Bcn.Bc7Flags MapBc7(uint flags)
    {
        uint m = flags & Bc7AnalyticalMask;
        if (m == 0) m = (uint)Bcn.Bc7Flags.Default;
        return (Bcn.Bc7Flags)m;
    }

    /// <summary>Bytes per 4x4 block for <paramref name="format"/> (8 for BC1/BC4, otherwise 16).</summary>
    public static int BlockBytes(BcFormat format) => format is BcFormat.Bc1 or BcFormat.Bc4 ? 8 : 16;

    /// <summary>Number of 4x4 block columns covering <paramref name="width"/> pixels.</summary>
    public static int BlocksWide(int width) => (width + 3) / 4;

    /// <summary>Number of 4x4 block rows covering <paramref name="height"/> pixels.</summary>
    public static int BlocksHigh(int height) => (height + 3) / 4;

    /// <summary>Tightly-packed encoded size, in bytes, for a <paramref name="width"/> x <paramref name="height"/> surface.</summary>
    public static int EncodedSize(BcFormat format, int width, int height)
        => BlocksWide(width) * BlocksHigh(height) * BlockBytes(format);

    /// <summary>
    /// Encodes a whole RGBA8 surface to an LDR block format (BC1/BC3/BC4/BC5/BC7). For BC7 <paramref name="flags"/>
    /// is <see cref="Bc7Flags"/>; for BC1/BC3 it is <see cref="Bc1Quality"/>; for BC4/BC5 it is ignored.
    /// Negative <paramref name="channel0"/>/<paramref name="channel1"/> select the defaults (0=R, 1=G).
    /// </summary>
    public static void Encode(BcFormat format, ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        Span<byte> dst, uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        int ch0 = channel0 < 0 ? 0 : channel0;
        int ch1 = channel1 < 0 ? 1 : channel1;
        switch (format)
        {
            case BcFormat.Bc1: Bcn.BlockImage.EncodeBc1(srcRgba, width, height, strideBytes, dst, MapBc1((Bc1Quality)flags)); break;
            case BcFormat.Bc3: Bcn.BlockImage.EncodeBc3(srcRgba, width, height, strideBytes, dst, MapBc1((Bc1Quality)flags)); break;
            case BcFormat.Bc4: Bcn.BlockImage.EncodeBc4(srcRgba, width, height, strideBytes, dst, ch0); break;
            case BcFormat.Bc5: Bcn.BlockImage.EncodeBc5(srcRgba, width, height, strideBytes, dst, ch0, ch1); break;
            case BcFormat.Bc7: Bcn.BlockImage.EncodeBc7(srcRgba, width, height, strideBytes, dst, MapBc7(flags)); break;
            case BcFormat.Bc6h: throw new ArgumentException("Use EncodeBc6h for BC6H (RGB FP16 input).", nameof(format));
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>Encodes a single tight 4x4 block (16 RGBA8 pixels, 64 bytes) to <see cref="BlockBytes(BcFormat)"/> bytes.</summary>
    public static void EncodeBlock(BcFormat format, ReadOnlySpan<byte> block16Rgba, Span<byte> dst,
        uint flags = 0, int channel0 = -1, int channel1 = -1)
        => Encode(format, block16Rgba, 4, 4, 4 * 4, dst, flags, channel0, channel1);

    /// <summary>
    /// Encodes a horizontal band of <paramref name="pixelRows"/> rows (the streaming primitive). Bands must be a
    /// multiple of 4 rows except the final band. <paramref name="dst"/> receives just this band's blocks.
    /// </summary>
    public static void EncodeRows(BcFormat format, ReadOnlySpan<byte> src, int width, int pixelRows, int strideBytes,
        Span<byte> dst, uint flags = 0, int channel0 = -1, int channel1 = -1)
        => Encode(format, src, width, pixelRows, strideBytes, dst, flags, channel0, channel1);

    // ---- per-format LDR ----

    /// <summary>Encode RGBA8 to BC1 (RGB +1-bit alpha).</summary>
    public static void EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height, int strideBytes, Span<byte> dst,
        Bc1Quality quality = Bc1Quality.HighQuality)
        => Bcn.BlockImage.EncodeBc1(rgba, width, height, strideBytes, dst, MapBc1(quality));

    /// <summary>Encode RGBA8 to BC3 (BC4 alpha + BC1 color).</summary>
    public static void EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height, int strideBytes, Span<byte> dst,
        Bc1Quality quality = Bc1Quality.HighQuality)
        => Bcn.BlockImage.EncodeBc3(rgba, width, height, strideBytes, dst, MapBc1(quality));

    /// <summary>Encode one channel of RGBA8 to BC4 (default channel 0 = R).</summary>
    public static void EncodeBc4(ReadOnlySpan<byte> rgba, int width, int height, int strideBytes, Span<byte> dst,
        int channel = 0)
        => Bcn.BlockImage.EncodeBc4(rgba, width, height, strideBytes, dst, channel);

    /// <summary>Encode two channels of RGBA8 to BC5 (defaults channel0 = R, channel1 = G).</summary>
    public static void EncodeBc5(ReadOnlySpan<byte> rgba, int width, int height, int strideBytes, Span<byte> dst,
        int channel0 = 0, int channel1 = 1)
        => Bcn.BlockImage.EncodeBc5(rgba, width, height, strideBytes, dst, channel0, channel1);

    /// <summary>Encode RGBA8 to BC7 (high quality RGBA).</summary>
    public static void EncodeBc7(ReadOnlySpan<byte> rgba, int width, int height, int strideBytes, Span<byte> dst,
        Bc7Flags flags = Bc7Flags.Default)
        => Bcn.BlockImage.EncodeBc7(rgba, width, height, strideBytes, dst, MapBc7((uint)flags));

    // ---- BC6H (HDR, RGB FP16) ----

    /// <summary>Encode an RGB half-float surface to BC6H. <paramref name="strideBytes"/> == width*3*sizeof(ushort).</summary>
    public static void EncodeBc6h(ReadOnlySpan<Half> rgbHalf, int width, int height, int strideBytes, Span<byte> dst,
        Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBc6h(MemoryMarshal.Cast<Half, ushort>(rgbHalf), width, height, strideBytes, dst, quality);

    /// <summary>Encode RGB FP16 bit patterns to BC6H. <paramref name="strideBytes"/> == width*3*sizeof(ushort).</summary>
    public static void EncodeBc6h(ReadOnlySpan<ushort> rgbHalf, int width, int height, int strideBytes, Span<byte> dst,
        Bc6hQuality quality = Bc6hQuality.Default)
        => Bcn.Hdr.Bc6hImage.Encode(rgbHalf, width, height, strideBytes / sizeof(ushort), dst, MapBc6h(quality));

    /// <summary>Encode a single 4x4 BC6H block (48 Half = 16 RGB texels).</summary>
    public static void EncodeBc6hBlock(ReadOnlySpan<Half> block48, Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBc6h(block48, 4, 4, 4 * 3 * sizeof(ushort), dst, quality);

    /// <summary>Encode a single 4x4 BC6H block (48 ushort FP16 bit patterns = 16 RGB texels).</summary>
    public static void EncodeBc6hBlock(ReadOnlySpan<ushort> block48, Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBc6h(block48, 4, 4, 4 * 3 * sizeof(ushort), dst, quality);

    /// <summary>Encode a band of <paramref name="pixelRows"/> BC6H rows (streaming primitive).</summary>
    public static void EncodeBc6hRows(ReadOnlySpan<Half> src, int width, int pixelRows, int strideBytes, Span<byte> dst,
        Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBc6h(src, width, pixelRows, strideBytes, dst, quality);

    /// <summary>Encode a band of <paramref name="pixelRows"/> BC6H rows (streaming primitive).</summary>
    public static void EncodeBc6hRows(ReadOnlySpan<ushort> src, int width, int pixelRows, int strideBytes, Span<byte> dst,
        Bc6hQuality quality = Bc6hQuality.Default)
        => EncodeBc6h(src, width, pixelRows, strideBytes, dst, quality);
}