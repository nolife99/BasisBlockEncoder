using System;

namespace BasisBlockEncoder;

/// <summary>
/// Encodes an LDR surface (BC1/BC3/BC4/BC5/BC7) incrementally, one horizontal band at a time,
/// into a fixed destination buffer. Lets you "stream" a texture in as it is produced (e.g. while
/// decoding) without ever holding the whole source image.
/// </summary>
/// <remarks>
/// Feed rows top-to-bottom with <see cref="WriteRows"/>. Every band except the last must be a
/// multiple of 4 pixel rows (blocks are 4 tall); the final band may be any remaining count and is
/// edge-clamped. The writer tracks the destination offset for you.
/// </remarks>
public sealed class BlockRowStreamWriter
{
    private readonly BcFormat _format;
    private readonly int _width;
    private readonly int _height;
    private readonly uint _flags;
    private readonly int _channel0;
    private readonly int _channel1;
    private readonly Memory<byte> _dst;
    private readonly int _rowBytes;
    private int _pixelRowsDone;

    /// <summary>Creates a writer that fills <paramref name="dst"/> with a <paramref name="width"/> x <paramref name="height"/> encode.</summary>
    /// <param name="format">Target LDR format. Must not be <see cref="BcFormat.Bc6h"/>.</param>
    /// <param name="dst">Destination buffer; must be at least <see cref="BlockEncoder.EncodedSize"/> bytes.</param>
    public BlockRowStreamWriter(BcFormat format, int width, int height, Memory<byte> dst,
                                uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        if (format == BcFormat.Bc6h)
            throw new ArgumentException("Use Bc6hBlockRowStreamWriter for BC6H.", nameof(format));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Dimensions must be positive.");

        long need = BlockEncoder.EncodedSize(format, width, height);
        if (dst.Length < need)
            throw new ArgumentException($"Destination is too small: have {dst.Length} bytes, need >= {need}.", nameof(dst));

        _format = format;
        _width = width;
        _height = height;
        _flags = flags;
        _channel0 = channel0;
        _channel1 = channel1;
        _dst = dst;
        _rowBytes = BlockEncoder.BlocksWide(width) * BlockEncoder.BlockBytes(format);
    }

    /// <summary>The format being written.</summary>
    public BcFormat Format => _format;
    /// <summary>Surface width in pixels.</summary>
    public int Width => _width;
    /// <summary>Surface height in pixels.</summary>
    public int Height => _height;
    /// <summary>Pixel rows consumed so far.</summary>
    public int PixelRowsWritten => _pixelRowsDone;
    /// <summary>Pixel rows still expected.</summary>
    public int PixelRowsRemaining => _height - _pixelRowsDone;
    /// <summary>True once every row has been written.</summary>
    public bool IsComplete => _pixelRowsDone >= _height;

    /// <summary>
    /// Encodes the next <paramref name="pixelRows"/> rows from <paramref name="bandRgba"/> (RGBA8,
    /// <paramref name="strideBytes"/> per row) into the destination.
    /// </summary>
    public void WriteRows(scoped ReadOnlySpan<byte> bandRgba, int pixelRows, int strideBytes)
    {
        if (IsComplete)
            throw new InvalidOperationException("All rows have already been written.");
        if (pixelRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelRows), "Must be positive.");
        if (_pixelRowsDone + pixelRows > _height)
            throw new ArgumentException($"Writing {pixelRows} rows would exceed height {_height} (already wrote {_pixelRowsDone}).", nameof(pixelRows));

        bool last = _pixelRowsDone + pixelRows == _height;
        if (!last && (pixelRows % 4 != 0))
            throw new ArgumentException("Every band except the final one must be a multiple of 4 pixel rows.", nameof(pixelRows));

        int dstOffset = (_pixelRowsDone / 4) * _rowBytes;
        BlockEncoder.EncodeRows(_format, bandRgba, _width, pixelRows, strideBytes,
                                _dst.Span.Slice(dstOffset), _flags, _channel0, _channel1);
        _pixelRowsDone += pixelRows;
    }
}

/// <summary>
/// BC6H counterpart of <see cref="BlockRowStreamWriter"/>. Streams an RGB half-float HDR surface in
/// bands. Source bands are 3 halfs/pixel (R,G,B).
/// </summary>
public sealed class Bc6hBlockRowStreamWriter
{
    private readonly int _width;
    private readonly int _height;
    private readonly Bc6hQuality _quality;
    private readonly Memory<byte> _dst;
    private readonly int _rowBytes;
    private int _pixelRowsDone;

    /// <summary>Creates a writer that fills <paramref name="dst"/> with a BC6H encode of the given size.</summary>
    public Bc6hBlockRowStreamWriter(int width, int height, Memory<byte> dst,
                                    Bc6hQuality quality = Bc6hQuality.Default)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Dimensions must be positive.");

        long need = BlockEncoder.EncodedSize(BcFormat.Bc6h, width, height);
        if (dst.Length < need)
            throw new ArgumentException($"Destination is too small: have {dst.Length} bytes, need >= {need}.", nameof(dst));

        _width = width;
        _height = height;
        _quality = quality;
        _dst = dst;
        _rowBytes = BlockEncoder.BlocksWide(width) * 16;
    }

    /// <summary>Surface width in pixels.</summary>
    public int Width => _width;
    /// <summary>Surface height in pixels.</summary>
    public int Height => _height;
    /// <summary>Pixel rows consumed so far.</summary>
    public int PixelRowsWritten => _pixelRowsDone;
    /// <summary>Pixel rows still expected.</summary>
    public int PixelRowsRemaining => _height - _pixelRowsDone;
    /// <summary>True once every row has been written.</summary>
    public bool IsComplete => _pixelRowsDone >= _height;

    /// <summary>Encodes the next <paramref name="pixelRows"/> rows from an RGB FP16 band.</summary>
    public void WriteRows(scoped ReadOnlySpan<ushort> bandRgbHalf, int pixelRows, int strideBytes)
    {
        if (IsComplete)
            throw new InvalidOperationException("All rows have already been written.");
        if (pixelRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelRows), "Must be positive.");
        if (_pixelRowsDone + pixelRows > _height)
            throw new ArgumentException($"Writing {pixelRows} rows would exceed height {_height} (already wrote {_pixelRowsDone}).", nameof(pixelRows));

        bool last = _pixelRowsDone + pixelRows == _height;
        if (!last && (pixelRows % 4 != 0))
            throw new ArgumentException("Every band except the final one must be a multiple of 4 pixel rows.", nameof(pixelRows));

        int dstOffset = (_pixelRowsDone / 4) * _rowBytes;
        BlockEncoder.EncodeBc6hRows(bandRgbHalf, _width, pixelRows, strideBytes,
                                    _dst.Span.Slice(dstOffset), _quality);
        _pixelRowsDone += pixelRows;
    }

    /// <summary>Encodes the next band from a <see cref="Half"/> span.</summary>
    public void WriteRows(scoped ReadOnlySpan<Half> bandRgbHalf, int pixelRows, int strideBytes) =>
        WriteRows(System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(bandRgbHalf), pixelRows, strideBytes);
}
