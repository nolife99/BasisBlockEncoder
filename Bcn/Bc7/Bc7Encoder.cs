namespace Bcn;

using System;
using Bc7;

/// <summary>Public real-time BC7 encoder (a C# port of Basis Universal's analytical `bc7f`).</summary>
public sealed class Bc7Encoder : IBlockEncoder
{
    readonly Bc7Flags _flags;
    public Bc7Encoder(Bc7Flags flags = Bc7Flags.DefaultFast) => _flags = flags;
    public BlockFormat Format => BlockFormat.Bc7;
    public int BlockWidth => 4;
    public int BlockHeight => 4;
    public int BytesPerBlock => 16;

    public void EncodeBlock(scoped ReadOnlySpan<ColorRgba> pixels, scoped Span<byte> output)
        => Bc7Block.Encode(pixels, output, _flags);
}