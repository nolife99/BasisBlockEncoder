using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BasisBlockEncoder;

/// <summary>
/// CPU block compressor for BC1/BC3/BC4/BC5/BC6H/BC7, backed by Binomial's basis_universal
/// analytical real-time encoders. All entry points are safe (span-based, no caller-side
/// <c>unsafe</c>), allocation-free, and operate on independent 4x4 blocks.
/// </summary>
/// <remarks>
/// <para>
/// LDR formats take 8-bit RGBA (4 bytes/pixel, R,G,B,A order). <see cref="BcFormat.Bc6h"/> takes
/// RGB half-float and has dedicated <c>EncodeBc6h*</c> methods.
/// </para>
/// <para>
/// Surfaces whose width/height are not multiples of 4 are accepted: partial edge blocks replicate
/// the last valid row/column. Destination size for any surface is <see cref="EncodedSize"/>.
/// </para>
/// <para>
/// Initialization is lazy and thread-safe; call <see cref="Initialize"/> once up front if you want
/// to pay that cost deterministically.
/// </para>
/// </remarks>
public static class BlockEncoder
{
    private static int s_initialized; // 0 = no, 1 = yes

    /// <summary>
    /// Initializes the native encoder tables. Idempotent and thread-safe.
    /// </summary>
    /// <exception cref="BasisEncoderException">If native initialization fails.</exception>
    public static void Initialize()
    {
        if (Volatile.Read(ref s_initialized) != 0)
            return;

        int r = NativeMethods.bbe_init();
        if (r != 0)
            throw new BasisEncoderException(r, "bbe_init");

        Volatile.Write(ref s_initialized, 1);
    }

    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref s_initialized) == 0)
            Initialize();
    }

    /// <summary>Bytes per 4x4 block for <paramref name="format"/> (8 for BC1/BC4, otherwise 16).</summary>
    public static int BlockBytes(BcFormat format) =>
        format is BcFormat.Bc1 or BcFormat.Bc4 ? 8 : 16;

    /// <summary>Number of 4x4 block columns for a given pixel width.</summary>
    public static int BlocksWide(int width) => (width + 3) / 4;

    /// <summary>Number of 4x4 block rows for a given pixel height.</summary>
    public static int BlocksHigh(int height) => (height + 3) / 4;

    /// <summary>Tightly-packed encoded size, in bytes, for a <paramref name="width"/> x <paramref name="height"/> surface.</summary>
    public static long EncodedSize(BcFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Dimensions must be positive.");
        return (long)BlocksWide(width) * BlocksHigh(height) * BlockBytes(format);
    }

    // -------------------------------------------------------------------------------------------
    // LDR whole-surface
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes a whole RGBA8 surface to an LDR block format (BC1/BC3/BC4/BC5/BC7).
    /// </summary>
    /// <param name="format">Target format. Must not be <see cref="BcFormat.Bc6h"/>.</param>
    /// <param name="srcRgba">Source pixels, R,G,B,A byte order.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="strideBytes">Bytes between rows (>= <paramref name="width"/>*4).</param>
    /// <param name="dst">Destination buffer (>= <see cref="EncodedSize"/>).</param>
    /// <param name="flags">BC7: <see cref="Bc7Flags"/> bits (0 selects Default). BC1/BC3: <see cref="Bc1Quality"/>. BC4/BC5: ignored.</param>
    /// <param name="channel0">BC4/BC5 source channel 0 (default 0 = R when negative).</param>
    /// <param name="channel1">BC5 source channel 1 (default 1 = G when negative).</param>
    public static unsafe void Encode(
        BcFormat format, scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        if (format == BcFormat.Bc6h)
            throw new ArgumentException("Use EncodeBc6h for BC6H (FP16 input).", nameof(format));

        ValidateLdr(format, srcRgba, width, height, strideBytes, dst);
        EnsureInitialized();

        fixed (byte* s = srcRgba)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode((int)format, s, width, height, strideBytes,
                                             d, (nuint)dst.Length, flags, channel0, channel1);
            Check(r, "bbe_encode");
        }
    }

    /// <summary>Encodes a whole RGBA8 surface to BC7.</summary>
    public static void EncodeBc7(scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, Bc7Flags flags = Bc7Flags.Default) =>
        Encode(BcFormat.Bc7, srcRgba, width, height, strideBytes, dst, (uint)flags);

    /// <summary>Encodes a whole RGBA8 surface to BC1 (color only; alpha is ignored).</summary>
    public static void EncodeBc1(scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, Bc1Quality quality = Bc1Quality.HighQuality) =>
        Encode(BcFormat.Bc1, srcRgba, width, height, strideBytes, dst, (uint)quality);

    /// <summary>Encodes a whole RGBA8 surface to BC3 (BC4 alpha + BC1 color).</summary>
    public static void EncodeBc3(scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, Bc1Quality quality = Bc1Quality.HighQuality) =>
        Encode(BcFormat.Bc3, srcRgba, width, height, strideBytes, dst, (uint)quality);

    /// <summary>Encodes a single channel of a whole RGBA8 surface to BC4.</summary>
    public static void EncodeBc4(scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, int channel = 0) =>
        Encode(BcFormat.Bc4, srcRgba, width, height, strideBytes, dst, 0, channel, -1);

    /// <summary>Encodes two channels of a whole RGBA8 surface to BC5 (red+green by default).</summary>
    public static void EncodeBc5(scoped ReadOnlySpan<byte> srcRgba, int width, int height, int strideBytes,
        scoped Span<byte> dst, int channel0 = 0, int channel1 = 1) =>
        Encode(BcFormat.Bc5, srcRgba, width, height, strideBytes, dst, 0, channel0, channel1);

    // -------------------------------------------------------------------------------------------
    // LDR streaming band
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes a horizontal band of <paramref name="pixelRows"/> rows. This is the streaming
    /// primitive: feed a surface top-to-bottom in bands whose height is a multiple of 4 (the final
    /// band may be any remaining row count). See <see cref="BlockRowStreamWriter"/> for a wrapper
    /// that tracks the destination offset for you.
    /// </summary>
    public static unsafe void EncodeRows(
        BcFormat format, scoped ReadOnlySpan<byte> srcRgba, int width, int pixelRows, int strideBytes,
        scoped Span<byte> dst, uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        if (format == BcFormat.Bc6h)
            throw new ArgumentException("Use EncodeBc6hRows for BC6H (FP16 input).", nameof(format));

        ValidateLdrRows(format, srcRgba, width, pixelRows, strideBytes, dst);
        EnsureInitialized();

        fixed (byte* s = srcRgba)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode_rows((int)format, s, width, pixelRows, strideBytes,
                                                  d, (nuint)dst.Length, flags, channel0, channel1);
            Check(r, "bbe_encode_rows");
        }
    }

    // -------------------------------------------------------------------------------------------
    // LDR single block
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes a single tight 4x4 block. <paramref name="block16Rgba"/> is 16 RGBA8 pixels
    /// (64 bytes) in raster order; <paramref name="dst"/> receives <see cref="BlockBytes"/> bytes.
    /// </summary>
    public static unsafe void EncodeBlock(
        BcFormat format, scoped ReadOnlySpan<byte> block16Rgba, scoped Span<byte> dst,
        uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        if (format == BcFormat.Bc6h)
            throw new ArgumentException("Use EncodeBc6hBlock for BC6H (FP16 input).", nameof(format));
        if (block16Rgba.Length < 64)
            throw new ArgumentException("A 4x4 RGBA8 block is 64 bytes.", nameof(block16Rgba));

        int bb = BlockBytes(format);
        if (dst.Length < bb)
            throw new ArgumentException($"Destination must be at least {bb} bytes for {format}.", nameof(dst));

        EnsureInitialized();

        fixed (byte* s = block16Rgba)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode_block((int)format, s, d, flags, channel0, channel1);
            Check(r, "bbe_encode_block");
        }
    }

    // -------------------------------------------------------------------------------------------
    // BC6H (RGB half-float)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes a whole RGB half-float surface to BC6H (unsigned). Source layout is 3 halfs/pixel
    /// (R,G,B), as raw FP16 bit patterns.
    /// </summary>
    /// <param name="srcRgbHalf">Source pixels: <c>width*height*3</c> FP16 values.</param>
    /// <param name="strideBytes">Bytes between rows (>= <paramref name="width"/>*6).</param>
    public static unsafe void EncodeBc6h(
        scoped ReadOnlySpan<ushort> srcRgbHalf, int width, int height, int strideBytes,
        scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default)
    {
        ValidateBc6h(srcRgbHalf, width, height, strideBytes, dst);
        EnsureInitialized();

        fixed (ushort* s = srcRgbHalf)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode_bc6h(s, width, height, strideBytes,
                                                  d, (nuint)dst.Length, (uint)quality);
            Check(r, "bbe_encode_bc6h");
        }
    }

    /// <summary>
    /// Encodes a whole RGB <see cref="Half"/> surface to BC6H. Convenience overload that
    /// reinterprets the <see cref="Half"/> span as raw FP16 bits.
    /// </summary>
    public static void EncodeBc6h(
        scoped ReadOnlySpan<Half> srcRgbHalf, int width, int height, int strideBytes,
        scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default) =>
        EncodeBc6h(MemoryMarshal.Cast<Half, ushort>(srcRgbHalf), width, height, strideBytes, dst, quality);

    /// <summary>Streaming band variant of <see cref="EncodeBc6h(ReadOnlySpan{ushort},int,int,int,Span{byte},Bc6hQuality)"/>.</summary>
    public static unsafe void EncodeBc6hRows(
        scoped ReadOnlySpan<ushort> srcRgbHalf, int width, int pixelRows, int strideBytes,
        scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default)
    {
        ValidateBc6hRows(srcRgbHalf, width, pixelRows, strideBytes, dst);
        EnsureInitialized();

        fixed (ushort* s = srcRgbHalf)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode_bc6h_rows(s, width, pixelRows, strideBytes,
                                                       d, (nuint)dst.Length, (uint)quality);
            Check(r, "bbe_encode_bc6h_rows");
        }
    }

    /// <summary>Streaming band variant accepting a <see cref="Half"/> span.</summary>
    public static void EncodeBc6hRows(
        scoped ReadOnlySpan<Half> srcRgbHalf, int width, int pixelRows, int strideBytes,
        scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default) =>
        EncodeBc6hRows(MemoryMarshal.Cast<Half, ushort>(srcRgbHalf), width, pixelRows, strideBytes, dst, quality);

    /// <summary>
    /// Encodes a single 4x4 BC6H block. <paramref name="block48RgbHalf"/> is 16 pixels x 3 halfs
    /// (48 FP16 values) in raster order; <paramref name="dst"/> receives 16 bytes.
    /// </summary>
    public static unsafe void EncodeBc6hBlock(
        scoped ReadOnlySpan<ushort> block48RgbHalf, scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default)
    {
        if (block48RgbHalf.Length < 48)
            throw new ArgumentException("A 4x4 RGB FP16 block is 48 halfs.", nameof(block48RgbHalf));
        if (dst.Length < 16)
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(dst));

        EnsureInitialized();

        fixed (ushort* s = block48RgbHalf)
        fixed (byte* d = dst)
        {
            int r = NativeMethods.bbe_encode_bc6h_block(s, d, (uint)quality);
            Check(r, "bbe_encode_bc6h_block");
        }
    }

    /// <summary>Encodes a single 4x4 BC6H block from a <see cref="Half"/> span.</summary>
    public static void EncodeBc6hBlock(
        scoped ReadOnlySpan<Half> block48RgbHalf, scoped Span<byte> dst, Bc6hQuality quality = Bc6hQuality.Default) =>
        EncodeBc6hBlock(MemoryMarshal.Cast<Half, ushort>(block48RgbHalf), dst, quality);

    // -------------------------------------------------------------------------------------------
    // Validation helpers
    // -------------------------------------------------------------------------------------------

    private static void ValidateLdr(BcFormat format, scoped ReadOnlySpan<byte> src, int width, int height,
                                    int strideBytes, scoped Span<byte> dst)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Dimensions must be positive.");
        if (strideBytes < width * 4)
            throw new ArgumentException($"strideBytes ({strideBytes}) must be >= width*4 ({width * 4}).", nameof(strideBytes));
        RequireSrcRows(src.Length, strideBytes, height);
        RequireDst(dst.Length, EncodedSize(format, width, height));
    }

    private static void ValidateLdrRows(BcFormat format, scoped ReadOnlySpan<byte> src, int width, int pixelRows,
                                        int strideBytes, scoped Span<byte> dst)
    {
        if (width <= 0 || pixelRows <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(pixelRows), "Dimensions must be positive.");
        if (strideBytes < width * 4)
            throw new ArgumentException($"strideBytes ({strideBytes}) must be >= width*4 ({width * 4}).", nameof(strideBytes));
        RequireSrcRows(src.Length, strideBytes, pixelRows);
        RequireDst(dst.Length, EncodedSize(format, width, pixelRows));
    }

    private static void ValidateBc6h(scoped ReadOnlySpan<ushort> src, int width, int height, int strideBytes, scoped Span<byte> dst)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Dimensions must be positive.");
        if (strideBytes < width * 3 * sizeof(ushort))
            throw new ArgumentException($"strideBytes ({strideBytes}) must be >= width*6 ({width * 6}).", nameof(strideBytes));
        RequireSrcRows((long)src.Length * sizeof(ushort), strideBytes, height);
        RequireDst(dst.Length, EncodedSize(BcFormat.Bc6h, width, height));
    }

    private static void ValidateBc6hRows(scoped ReadOnlySpan<ushort> src, int width, int pixelRows, int strideBytes, scoped Span<byte> dst)
    {
        if (width <= 0 || pixelRows <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(pixelRows), "Dimensions must be positive.");
        if (strideBytes < width * 3 * sizeof(ushort))
            throw new ArgumentException($"strideBytes ({strideBytes}) must be >= width*6 ({width * 6}).", nameof(strideBytes));
        RequireSrcRows((long)src.Length * sizeof(ushort), strideBytes, pixelRows);
        RequireDst(dst.Length, EncodedSize(BcFormat.Bc6h, width, pixelRows));
    }

    private static void RequireSrcRows(long srcBytes, int strideBytes, int rows)
    {
        // The encoder reads up to (rows-1)*stride + full last row; require at least that many bytes.
        long need = (long)(rows - 1) * strideBytes + strideBytes;
        if (srcBytes < need)
            throw new ArgumentException($"Source buffer is too small: have {srcBytes} bytes, need >= {need}.", "src");
    }

    private static void RequireDst(long dstBytes, long need)
    {
        if (dstBytes < need)
            throw new ArgumentException($"Destination buffer is too small: have {dstBytes} bytes, need >= {need}.", "dst");
    }

    private static void Check(int result, string fn)
    {
        if (result != 0)
            throw new BasisEncoderException(result, fn);
    }
}
