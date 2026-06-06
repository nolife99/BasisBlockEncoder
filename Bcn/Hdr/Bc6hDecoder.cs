// BC6H (unsigned) block decoder. Inverts basis_universal's pack_bc6h_block:
//   - 128-bit block = two little-endian dwords for l (bits 0..63) and h (bits 64..127)
//   - mode from the low 5 bits via ModeLookup (modes 0/1 are really 2-bit; the lookup folds that in)
//   - endpoint/partition bits read sequentially from a running cursor per BitLayouts
//     (single bit when firstBit==-1; otherwise a range, reversed when firstBit>lastBit)
//   - non-base endpoints are signed deltas from the base, except modes 9 & 10 (absolute)
//   - unquantize p-bit endpoint -> 16-bit "blog", interpolate, then (v*31)>>6 -> FP16 bits
// This matches the standard D3D11 unsigned-BC6H decode, which is what the encoder targets.

namespace Bcn.Hdr;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

static class Bc6hDecoder
{
    // Decode one 16-byte BC6H block into 16 RGB half-float triples (FP16 bit patterns),
    // raster order: outRgbHalf48[texel*3 + channel]. Length must be 48.
    internal static void DecodeBlock(scoped ReadOnlySpan<byte> src16, scoped Span<ushort> outRgbHalf48)
    {
        var l = BinaryPrimitives.ReadUInt32LittleEndian(src16)
            | (ulong)BinaryPrimitives.ReadUInt32LittleEndian(src16.Slice(4)) << 32;

        var h = BinaryPrimitives.ReadUInt32LittleEndian(src16.Slice(8))
            | (ulong)BinaryPrimitives.ReadUInt32LittleEndian(src16.Slice(12)) << 32;

        int mode = Bc6hTables.ModeLookup[(int)(l & 0x1F)];
        if (mode < 0)
        {
            outRgbHalf48.Slice(0, 48).Clear();
            return;
        } // reserved/invalid

        int baseBits = Bc6hTables.ModeSigBits[mode * 4];
        var numSubsets = mode <= 9 ? 2 : 1;
        var pos = mode >= 2 ? 5 : 2;

        Span<uint> ep = stackalloc uint[12]; // [comp*4 + idx], idx: 0=s0L 1=s0H 2=s1L 3=s1H
        ep.Clear();
        uint partition = 0;

        var layout = Bc6hTables.BitLayouts[mode];
        for (var e = 0; e < layout.Length; e += 4)
        {
            int comp = layout[e], idx = layout[e + 1], last = layout[e + 2], first = layout[e + 3];
            if (first == -1)
            {
                var bit = ReadBits(l, h, pos, 1);
                pos += 1;
                if (comp == 3) partition |= bit << last;
                else ep[comp * 4 + idx] |= bit << last;
            }
            else
            {
                var width = Math.Abs(last - first) + 1;
                var raw = ReadBits(l, h, pos, width);
                pos += width;
                if (first > last) raw = ReverseBits(raw, width);
                var lo = Math.Min(first, last);
                if (comp == 3) partition |= raw << lo;
                else ep[comp * 4 + idx] |= raw << lo;
            }
        }

        // Delta reconstruction (modes 9 & 10 store absolute endpoints).
        if (mode != 9 && mode != 10)
        {
            var mask = (1u << baseBits) - 1u;
            var nIdx = numSubsets == 2 ? 4 : 2;
            for (var c = 0; c < 3; c++)
            {
                int dbits = Bc6hTables.ModeSigBits[mode * 4 + c + 1];
                var baseV = (int)ep[c * 4];
                for (var idx = 1; idx < nIdx; idx++)
                    ep[c * 4 + idx] = (uint)(baseV + SignExtend(ep[c * 4 + idx], dbits)) & mask;
            }
        }

        // Unquantize used endpoints to 16-bit blog.
        var numEp = numSubsets * 2;
        Span<int> uq = stackalloc int[12];
        for (var c = 0; c < 3; c++)
        for (var idx = 0; idx < numEp; idx++)
            uq[c * 4 + idx] = Unquantize(ep[c * 4 + idx], baseBits);

        // Weights (anchor texels use one fewer bit).
        var weightBits = numSubsets == 2 ? 3 : 4;
        var wtab = numSubsets == 2 ? Bc6hTables.Weight3 : Bc6hTables.Weight4;
        var patBase = (int)partition * 16;
        Span<int> w = stackalloc int[16];
        var patterns = Bc6hTables.Patterns.AsSpan();

        for (var i = 0; i < 16; i++)
        {
            var nb = weightBits;
            if (numSubsets == 2)
            {
                if ((patterns[patBase + i] & 0x80) != 0) nb--;
            }
            else if (i == 0) nb--;

            w[i] = (int)ReadBits(l, h, pos, nb);
            pos += nb;
        }

        // Texels: pick subset, interpolate endpoints in blog16, convert to FP16 bits.
        for (var i = 0; i < 16; i++)
        {
            var subset = numSubsets == 2 ? patterns[patBase + i] & 1 : 0;
            int epLow = subset * 2, epHigh = subset * 2 + 1;
            int wq = wtab[w[i]];
            for (var c = 0; c < 3; c++)
            {
                var v = uq[c * 4 + epLow] * (64 - wq) + uq[c * 4 + epHigh] * wq + 32 >> 6;
                outRgbHalf48[i * 3 + c] = (ushort)(v * 31 >> 6);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint MaskLowBits(uint value, int bits)
    {
        if (Bmi2.IsSupported)
            return Bmi2.ZeroHighBits(value, (uint)bits);

        return value & (uint)((1UL << bits) - 1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint ReadBits(ulong l, ulong h, int pos, int n)
    {
        ulong value;

        if ((uint)pos < 64u)
        {
            value = l >> pos;
            if (pos + n > 64)
                value |= h << 64 - pos;
        }
        else
        {
            value = h >> pos - 64;
        }

        return MaskLowBits((uint)value, n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint ReverseBits(uint value, int n)
    {
        value = value >> 1 & 0x5555_5555u |
            (value & 0x5555_5555u) << 1;

        value = value >> 2 & 0x3333_3333u |
            (value & 0x3333_3333u) << 2;

        value = value >> 4 & 0x0F0F_0F0Fu |
            (value & 0x0F0F_0F0Fu) << 4;

        value = BinaryPrimitives.ReverseEndianness(value);
        return (uint)((ulong)value >> 32 - n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int SignExtend(uint value, int bits)
    {
        var shift = 32 - bits;
        return (int)(value << shift) >> shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Unquantize(uint comp, int bits)
    {
        if (bits >= 15)
            return (int)comp;

        var max = (1u << bits) - 1u;
        if (unchecked(comp - 1u) < max - 1u)
            return (int)((comp << 16) + 0x8000u >> bits);

        return comp == 0 ? 0 : 0xFFFF;
    }
}