using System.Runtime.InteropServices;

namespace BasisBlockEncoder;

/// <summary>
/// Raw P/Invoke surface for the native <c>basis_block_encoder</c> library. All pointer-based;
/// the public <see cref="BlockEncoder"/> API pins spans and calls into these.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "basis_block_encoder";

    [LibraryImport(Lib)]
    internal static partial int bbe_init();

    [LibraryImport(Lib)]
    internal static partial int bbe_block_bytes(int format);

    [LibraryImport(Lib)]
    internal static partial nuint bbe_encoded_size(int format, int width, int height);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode(
        int format, byte* srcRgba, int width, int height, int srcStrideBytes,
        byte* dst, nuint dstSize, uint flags, int channel0, int channel1);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode_rows(
        int format, byte* srcRgba, int width, int pixelRows, int srcStrideBytes,
        byte* dst, nuint dstSize, uint flags, int channel0, int channel1);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode_block(
        int format, byte* blockRgba16, byte* dst, uint flags, int channel0, int channel1);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode_bc6h(
        ushort* srcRgbHalf, int width, int height, int srcStrideBytes,
        byte* dst, nuint dstSize, uint quality);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode_bc6h_rows(
        ushort* srcRgbHalf, int width, int pixelRows, int srcStrideBytes,
        byte* dst, nuint dstSize, uint quality);

    [LibraryImport(Lib)]
    internal static partial int bbe_encode_bc6h_block(
        ushort* blockRgbHalf48, byte* dst, uint quality);
}
