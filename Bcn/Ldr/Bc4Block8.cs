// Bc4Block8.cs — BC4 multi-block ("lane = block") encoder path (namespace Bcn.Ldr).
//
// 8 independent BC4 blocks (one channel each) are encoded in parallel, one block per Vector256 lane.
// Bit-identical to 8x Bc4Block.Encode: every step is the same integer arithmetic as the scalar core, done
// lane-parallel, so each lane matches scalar exactly. Three things make it fully vectorized (no scalar
// gather, no per-block reduction):
//   * min/max is lane-parallel across the 16 pixels (no per-block horizontal reduction),
//   * BaseTran[count] is an 8->8 permutation, so Vector256.Shuffle(baseTran, count) (vpermd) replaces the
//     per-lane table gather,
//   * the 48-bit selector word is packed by widening the per-pixel selector codes to long lanes and
//     ShiftLeft-ing into two Vector256<long> accumulators (4 blocks each).
// Solid blocks need no branch: delta == 0 makes all thresholds 0, so count == 7 and BaseTran[7] == 0,
// i.e. an all-zero selector word and endpoints max == min.
#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bcn.Ldr;

internal static partial class Bc4Block
{
    // Encode the channel-`channel` BC4 block of 8 block-contiguous blocks (px8[b*16 + i]) into `out8`, each
    // block's 8 bytes at [b*blockBytes + offset]. blockBytes/offset let BC3 (16,0) and BC5 (16,0 then 16,8)
    // share this with standalone BC4 (8,0).
    internal static void EncodeAlpha8(scoped ReadOnlySpan<ColorRgba> px8, scoped Span<byte> out8,
        int channel, int blockBytes, int offset)
    {
        // transpose + channel extract: tC[i*8 + b] = channel of px8[b*16 + i]
        Span<int> tC = stackalloc int[128];
        ReadOnlySpan<uint> src = MemoryMarshal.Cast<ColorRgba, uint>(px8);
        int sh = channel * 8;
        for (int b = 0; b < 8; b++)
            for (int i = 0; i < 16; i++)
                tC[i * 8 + b] = (int)((src[b * 16 + i] >> sh) & 0xFFu);
        ref int t0r = ref MemoryMarshal.GetReference(tC);

        Vector256<int> min = Vector256.LoadUnsafe(ref t0r, 0), max = min;
        for (int i = 1; i < 16; i++)
        {
            Vector256<int> v = Vector256.LoadUnsafe(ref t0r, (nuint)(i * 8));
            min = Vector256.Min(min, v); max = Vector256.Max(max, v);
        }

        Vector256<int> delta = max - min;
        Vector256<int> bias = Vector256.Create(4) - min * Vector256.Create(14);
        Vector256<int> f14 = Vector256.Create(14);
        Vector256<int> t0 = delta * Vector256.Create(13), t1 = delta * Vector256.Create(11), t2 = delta * Vector256.Create(9),
                       t3 = delta * Vector256.Create(7), t4 = delta * Vector256.Create(5), t5 = delta * Vector256.Create(3), t6 = delta;
        Vector256<int> baseTran = Vector256.Create(1, 7, 6, 5, 4, 3, 2, 0); // crossing-count -> selector code
        Vector256<long> accLo = Vector256<long>.Zero, accHi = Vector256<long>.Zero;
        for (int i = 0; i < 16; i++)
        {
            Vector256<int> v = Vector256.LoadUnsafe(ref t0r, (nuint)(i * 8)) * f14 + bias;
            Vector256<int> cnt = Vector256<int>.Zero;       // GreaterThanOrEqual yields -1; subtract to count 0..7
            cnt -= Vector256.GreaterThanOrEqual(v, t0); cnt -= Vector256.GreaterThanOrEqual(v, t1);
            cnt -= Vector256.GreaterThanOrEqual(v, t2); cnt -= Vector256.GreaterThanOrEqual(v, t3);
            cnt -= Vector256.GreaterThanOrEqual(v, t4); cnt -= Vector256.GreaterThanOrEqual(v, t5);
            cnt -= Vector256.GreaterThanOrEqual(v, t6);
            Vector256<int> sel = Vector256.Shuffle(baseTran, cnt);   // BaseTran[count] per lane (vpermd)
            accLo |= Vector256.ShiftLeft(Vector256.WidenLower(sel), 3 * i);   // blocks 0..3
            accHi |= Vector256.ShiftLeft(Vector256.WidenUpper(sel), 3 * i);   // blocks 4..7
        }

        for (int b = 0; b < 8; b++)
        {
            int o = b * blockBytes + offset;
            out8[o] = (byte)max.GetElement(b);
            out8[o + 1] = (byte)min.GetElement(b);
            long w = b < 4 ? accLo.GetElement(b) : accHi.GetElement(b - 4);
            out8[o + 2] = (byte)w; out8[o + 3] = (byte)(w >> 8); out8[o + 4] = (byte)(w >> 16);
            out8[o + 5] = (byte)(w >> 24); out8[o + 6] = (byte)(w >> 32); out8[o + 7] = (byte)(w >> 40);
        }
    }
}
#endif
