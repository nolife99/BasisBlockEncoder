// ColorRgba.cs — shared RGBA8 texel used by every block codec (namespace Bcn).
using System.Runtime.InteropServices;

namespace Bcn;

/// <summary>RGBA8 texel. Sequential 4-byte layout so it reinterprets cleanly to uint / byte spans.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
public struct ColorRgba
{
    public byte R, G, B, A;
    public ColorRgba(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
}
