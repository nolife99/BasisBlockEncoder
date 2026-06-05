// Bc4Block8.cs — BC4 multi-block ("lane = block") encoder path (namespace Bcn.Ldr).
//
// One pass encodes Vector<int>.Count independent BC4 blocks (one channel each) in parallel, one block per
// SIMD lane, at the hardware's native width (4 on NEON / SSE, 8 on AVX2, 16 on AVX-512). A call processes
// a *custom batch* of blocks — any multiple of the lane width — by running that many lane-passes back to
// back (BatchGroups passes for BC4/BC5). Each pass is fully independent, so a larger batch is byte-identical
// to more separate calls; it just amortizes the per-call/setup cost and lets the out-of-order engine overlap
// the latency-bound count chains of adjacent passes. Bit-identical to N x Bc4Block.Encode: every step is the
// scalar integer arithmetic done lane-parallel, so a lane matches scalar exactly and neither the lane count
// nor the batch size changes a lane's result. The path is purely vertical — no horizontal reduction and no
// cross-lane permute:
//   * min/max is lane-parallel across the 16 pixels (no per-block horizontal reduction),
//   * BaseTran[count] is computed branchlessly (8 - count, with the two endpoints patched) rather than by a
//     table gather, so no Shuffle/permute is needed — which is what lets one body serve every width,
//   * the 48-bit selector word is packed by widening the per-pixel selector codes to long lanes and
//     ShiftLeft-ing into two long accumulators (the low/high halves of the lanes).
// Solid blocks need no branch: delta == 0 makes all thresholds 0, so count == 7 -> selector 0, i.e. an
// all-zero selector word and endpoints max == min.

namespace Bcn.Ldr;

using System;
using System.Numerics;
using System.Runtime.InteropServices;

static partial class Bc4Block
{
    // Encode `channel` for px.Length/16 raster-contiguous blocks (px[blk*16 + i]) into `output`, each block's
    // 8 bytes at [blk*blockBytes + offset]. The block count must be a multiple of Vector<int>.Count (the
    // driver guarantees it). blockBytes/offset let BC3 (16,0) and BC5 (16,0 then 16,8) share this with
    // standalone BC4 (8,0).
    internal static void EncodeAlphaBatch(scoped ReadOnlySpan<ColorRgba> px,
        scoped Span<byte> output,
        int channel,
        int blockBytes,
        int offset)
    {
        int n = Vector<int>.Count, half = Vector<long>.Count; // half == n / 2
        var src = MemoryMarshal.Cast<ColorRgba, uint>(px);
        var sh = channel * 8;
        var total = px.Length / 16; // blocks in this batch; a multiple of n

        Span<int> tC = stackalloc int[16 * n];
        Vector<int> eight = new(8), one = new(1), seven = new(7), zero = Vector<int>.Zero;

        for (var g0 = 0; g0 < total; g0 += n)
        {
            // transpose + channel extract: tC[i*n + l] = channel of block (g0+l), pixel i
            for (var l = 0; l < n; l++)
            for (var i = 0; i < 16; i++)
                tC[i * n + l] = (int)(src[(g0 + l) * 16 + i] >> sh & 0xFFu);

            Vector<int> min = new(tC), max = min;
            for (var i = 1; i < 16; i++)
            {
                Vector<int> v = new(tC.Slice(i * n));
                min = Vector.Min(min, v);
                max = Vector.Max(max, v);
            }

            var delta = max - min;
            var bias = new Vector<int>(4) - min * 14;
            Vector<int> t0 = delta * 13, t1 = delta * 11, t2 = delta * 9,
                t3 = delta * 7, t4 = delta * 5, t5 = delta * 3, t6 = delta;

            Vector<long> accLo = Vector<long>.Zero, accHi = Vector<long>.Zero;
            for (var i = 0; i < 16; i++)
            {
                var v = new Vector<int>(tC.Slice(i * n)) * 14 + bias;
                var cnt = zero; // GreaterThanOrEqual yields -1; subtract to count 0..7
                cnt -= Vector.GreaterThanOrEqual(v, t0);
                cnt -= Vector.GreaterThanOrEqual(v, t1);
                cnt -= Vector.GreaterThanOrEqual(v, t2);
                cnt -= Vector.GreaterThanOrEqual(v, t3);
                cnt -= Vector.GreaterThanOrEqual(v, t4);
                cnt -= Vector.GreaterThanOrEqual(v, t5);
                cnt -= Vector.GreaterThanOrEqual(v, t6);
                // BaseTran[cnt] = {0->1,1->7,2->6,3->5,4->4,5->3,6->2,7->0} == 8-cnt with the two endpoints patched.
                var sel = eight - cnt;
                sel = Vector.ConditionalSelect(Vector.Equals(cnt, zero), one, sel); // cnt==0 -> 1
                sel = Vector.ConditionalSelect(Vector.Equals(cnt, seven), zero, sel); // cnt==7 -> 0
                Vector.Widen(sel, out var selLo, out var selHi);
                accLo |= Vector.ShiftLeft(selLo, 3 * i); // low half of the lanes
                accHi |= Vector.ShiftLeft(selHi, 3 * i); // high half of the lanes
            }

            for (var l = 0; l < n; l++)
            {
                var o = (g0 + l) * blockBytes + offset;
                output[o] = (byte)max[l];
                output[o + 1] = (byte)min[l];
                var w = l < half ? accLo[l] : accHi[l - half];
                output[o + 2] = (byte)w;
                output[o + 3] = (byte)(w >> 8);
                output[o + 4] = (byte)(w >> 16);
                output[o + 5] = (byte)(w >> 24);
                output[o + 6] = (byte)(w >> 32);
                output[o + 7] = (byte)(w >> 40);
            }
        }
    }
}