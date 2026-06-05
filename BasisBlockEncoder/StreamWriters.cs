using System;
using System.Runtime.InteropServices;

namespace BasisBlockEncoder;

/// <summary>Streams an RGBA8 surface to an LDR block format (BC1/BC3/BC4/BC5/BC7) one band at a time.</summary>
public sealed class BlockRowStreamWriter
{
    private readonly Memory<byte> _dst;
    private readonly uint _flags;
    private readonly int _ch0, _ch1, _bpb, _blocksWide;
    private int _rowsWritten, _byteOffset;

    public BcFormat Format { get; }
    public int Width { get; }
    public int Height { get; }

    /// <param name="format">Target block format (must not be <see cref="BcFormat.Bc6h"/>).</param>
    /// <param name="width">Surface width in pixels.</param>
    /// <param name="height">Surface height in pixels.</param>
    /// <param name="destination">Output buffer; must hold <see cref="BlockEncoder.EncodedSize"/> bytes.</param>
    /// <param name="flags">BC7: <see cref="Bc7Flags"/> bits; BC1/BC3: <see cref="Bc1Quality"/>; BC4/BC5: ignored.</param>
    /// <param name="channel0">BC4/BC5 source channel 0 (negative = default 0 = R).</param>
    /// <param name="channel1">BC5 source channel 1 (negative = default 1 = G).</param>
    public BlockRowStreamWriter(BcFormat format, int width, int height, Memory<byte> destination,
        uint flags = 0, int channel0 = -1, int channel1 = -1)
    {
        if (format == BcFormat.Bc6h) throw new ArgumentException("Use Bc6hBlockRowStreamWriter for BC6H.", nameof(format));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Format = format;
        Width = width;
        Height = height;
        _dst = destination;
        _flags = flags;
        _ch0 = channel0;
        _ch1 = channel1;
        _bpb = BlockEncoder.BlockBytes(format);
        _blocksWide = BlockEncoder.BlocksWide(width);
        int need = BlockEncoder.EncodedSize(format, width, height);
        if (destination.Length < need)
            throw new ArgumentException($"Destination too small: need {need} bytes, got {destination.Length}.", nameof(destination));
    }

    /// <summary>True once every pixel row has been written.</summary>
    public bool IsComplete => _rowsWritten >= Height;
    /// <summary>Pixel rows not yet written.</summary>
    public int PixelRowsRemaining => Height - _rowsWritten;
    /// <summary>Pixel rows written so far.</summary>
    public int PixelRowsWritten => _rowsWritten;

    /// <summary>Encode the next <paramref name="rows"/> pixel rows from <paramref name="band"/> (stride in bytes).</summary>
    public void WriteRows(ReadOnlySpan<byte> band, int rows, int strideBytes)
    {
        ValidateBand(rows, _rowsWritten, Height);
        int bandBlockRows = (rows + 3) / 4;
        int bandBytes = bandBlockRows * _blocksWide * _bpb;
        BlockEncoder.EncodeRows(Format, band, Width, rows, strideBytes,
            _dst.Span.Slice(_byteOffset, bandBytes), _flags, _ch0, _ch1);
        _byteOffset += bandBytes;
        _rowsWritten += rows;
    }

    internal static void ValidateBand(int rows, int written, int height)
    {
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (written + rows > height) throw new ArgumentException("Band exceeds the remaining image height.", nameof(rows));
        bool isFinal = written + rows == height;
        if (!isFinal && (rows & 3) != 0)
            throw new ArgumentException("Non-final bands must be a multiple of 4 rows.", nameof(rows));
    }
}

/// <summary>Streams an RGB half-float surface to BC6H one band at a time.</summary>
public sealed class Bc6hBlockRowStreamWriter
{
    private readonly Memory<byte> _dst;
    private readonly Bc6hQuality _quality;
    private readonly int _blocksWide;
    private int _rowsWritten, _byteOffset;

    public int Width { get; }
    public int Height { get; }

    public Bc6hBlockRowStreamWriter(int width, int height, Memory<byte> destination, Bc6hQuality quality = Bc6hQuality.Default)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _dst = destination;
        _quality = quality;
        _blocksWide = BlockEncoder.BlocksWide(width);
        int need = BlockEncoder.EncodedSize(BcFormat.Bc6h, width, height);
        if (destination.Length < need)
            throw new ArgumentException($"Destination too small: need {need} bytes, got {destination.Length}.", nameof(destination));
    }

    public bool IsComplete => _rowsWritten >= Height;
    public int PixelRowsRemaining => Height - _rowsWritten;
    public int PixelRowsWritten => _rowsWritten;

    /// <summary>Encode the next <paramref name="rows"/> rows of RGB Half texels (stride in bytes).</summary>
    public void WriteRows(ReadOnlySpan<Half> band, int rows, int strideBytes)
        => WriteRows(MemoryMarshal.Cast<Half, ushort>(band), rows, strideBytes);

    /// <summary>Encode the next <paramref name="rows"/> rows of RGB FP16 bit patterns (stride in bytes).</summary>
    public void WriteRows(ReadOnlySpan<ushort> band, int rows, int strideBytes)
    {
        BlockRowStreamWriter.ValidateBand(rows, _rowsWritten, Height);
        int bandBlockRows = (rows + 3) / 4;
        int bandBytes = bandBlockRows * _blocksWide * 16;
        BlockEncoder.EncodeBc6hRows(band, Width, rows, strideBytes, _dst.Span.Slice(_byteOffset, bandBytes), _quality);
        _byteOffset += bandBytes;
        _rowsWritten += rows;
    }
}