using System;
using Xunit;

namespace BasisBlockEncoder.Tests;

public class SmokeTests
{
    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4 + 0] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return px;
    }

    private static byte[] GradientRgba(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[i + 2] = 128;
                px[i + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void Initializes()
    {
        BlockEncoder.Initialize();
    }

    [Theory]
    [InlineData(BcFormat.Bc1)]
    [InlineData(BcFormat.Bc3)]
    [InlineData(BcFormat.Bc4)]
    [InlineData(BcFormat.Bc5)]
    [InlineData(BcFormat.Bc7)]
    public void EncodesSolidSurface(BcFormat format)
    {
        const int w = 16, h = 12; // not a power of two; exercises multiple blocks
        var src = SolidRgba(w, h, 200, 100, 50, 255);
        var dst = new byte[BlockEncoder.EncodedSize(format, w, h)];

        uint flags = format == BcFormat.Bc7 ? (uint)Bc7Flags.Default : (uint)Bc1Quality.HighQuality;
        BlockEncoder.Encode(format, src, w, h, w * 4, dst, flags);

        Assert.Contains(dst, b => b != 0);
    }

    [Fact]
    public void EncodedSizeMatchesBlockMath()
    {
        Assert.Equal(2L * 3 * 8, BlockEncoder.EncodedSize(BcFormat.Bc1, 5, 9));   // 2x3 blocks * 8
        Assert.Equal(2L * 3 * 16, BlockEncoder.EncodedSize(BcFormat.Bc7, 5, 9));  // 2x3 blocks * 16
    }

    [Fact]
    public void StreamingBandsMatchWholeSurface()
    {
        const int w = 16, h = 16;
        var src = GradientRgba(w, h);

        var whole = new byte[BlockEncoder.EncodedSize(BcFormat.Bc7, w, h)];
        BlockEncoder.EncodeBc7(src, w, h, w * 4, whole);

        var streamed = new byte[whole.Length];
        var writer = new BlockRowStreamWriter(BcFormat.Bc7, w, h, streamed, (uint)Bc7Flags.Default);

        // feed in 4-row bands
        int stride = w * 4;
        for (int y = 0; y < h; y += 4)
        {
            var band = src.AsSpan(y * stride, 4 * stride);
            writer.WriteRows(band, 4, stride);
        }

        Assert.True(writer.IsComplete);
        Assert.Equal(whole, streamed);
    }

    [Fact]
    public void StreamingHandlesNonMultipleOfFourFinalBand()
    {
        const int w = 8, h = 10; // 10 rows = 4 + 4 + 2
        var src = GradientRgba(w, h);

        var whole = new byte[BlockEncoder.EncodedSize(BcFormat.Bc7, w, h)];
        BlockEncoder.EncodeBc7(src, w, h, w * 4, whole);

        var streamed = new byte[whole.Length];
        var writer = new BlockRowStreamWriter(BcFormat.Bc7, w, h, streamed, (uint)Bc7Flags.Default);
        int stride = w * 4;
        writer.WriteRows(src.AsSpan(0 * stride, 4 * stride), 4, stride);
        writer.WriteRows(src.AsSpan(4 * stride, 4 * stride), 4, stride);
        writer.WriteRows(src.AsSpan(8 * stride, 2 * stride), 2, stride); // final partial band

        Assert.True(writer.IsComplete);
        Assert.Equal(whole, streamed);
    }

    [Fact]
    public void SingleBlockEncodes()
    {
        var block = SolidRgba(4, 4, 10, 20, 30, 255);
        var dst = new byte[16];
        BlockEncoder.EncodeBlock(BcFormat.Bc7, block, dst);
        Assert.Contains(dst, b => b != 0);
    }

    [Fact]
    public void Bc6hEncodesHalfSurface()
    {
        const int w = 8, h = 8;
        var src = new Half[w * h * 3];
        for (int i = 0; i < src.Length; i++)
            src[i] = (Half)0.5f;

        var dst = new byte[BlockEncoder.EncodedSize(BcFormat.Bc6h, w, h)];
        BlockEncoder.EncodeBc6h(src, w, h, w * 3 * sizeof(ushort), dst, Bc6hQuality.Default);

        Assert.Contains(dst, b => b != 0);
    }

    [Fact]
    public void Bc6hStreamingMatchesWhole()
    {
        const int w = 8, h = 8;
        var src = new ushort[w * h * 3];
        var rng = new Random(1234);
        for (int i = 0; i < src.Length; i++)
            src[i] = (ushort)BitConverter.HalfToUInt16Bits((Half)(rng.NextSingle() * 4f));

        var whole = new byte[BlockEncoder.EncodedSize(BcFormat.Bc6h, w, h)];
        BlockEncoder.EncodeBc6h(src, w, h, w * 3 * sizeof(ushort), whole);

        var streamed = new byte[whole.Length];
        var writer = new Bc6hBlockRowStreamWriter(w, h, streamed);
        int stride = w * 3 * sizeof(ushort);
        writer.WriteRows(src.AsSpan(0 * w * 3, 4 * w * 3), 4, stride);
        writer.WriteRows(src.AsSpan(4 * w * 3, 4 * w * 3), 4, stride);

        Assert.True(writer.IsComplete);
        Assert.Equal(whole, streamed);
    }

    [Fact]
    public void TooSmallDestinationThrows()
    {
        var src = SolidRgba(8, 8, 1, 2, 3, 4);
        var tiny = new byte[4];
        Assert.Throws<ArgumentException>(() => BlockEncoder.EncodeBc7(src, 8, 8, 32, tiny));
    }
}
