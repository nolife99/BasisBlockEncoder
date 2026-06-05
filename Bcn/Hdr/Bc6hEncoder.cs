namespace Bcn.Hdr;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

public enum Bc6hQuality { Fast, Default, HighQuality }

static partial class Bc6hEncoder
{
    const float MIN_HALF_FLOAT = 0.00006103515625f; // 2^-14
    const int MAX_BC6H_HALF = 0x7BFF;
    const float SMALL_FLOAT_VAL = 1e-18f, REALLY_SMALL_FLOAT_VAL = 1e-30f, BIG_FLOAT_VAL = 1e30f;
    const float TH_SIMPLE = 256f * 256f * 16f, TH_COMPLEX = 512f * 512f * 16f, TH_VERYCOMPLEX = 2048f * 2048f * 16f;

    static readonly float HtoFK = BitConverter.UInt32BitsToSingle(0x77800000); // 2^112

    static readonly byte[] ModeBits =
    {
        0b00, 0b01, 0b00010, 0b00110, 0b01010, 0b01110, 0b10010, 0b10110, 0b11010, 0b11110, 0b00011, 0b00111, 0b01011, 0b01111
    };

    static readonly float[] LsW4 = BuildLsW(Bc6hTables.Weight4);
    static readonly float[] LsW3 = BuildLsW(Bc6hTables.Weight3);

    static Params ForQuality(Bc6hQuality q) => q switch
    {
        Bc6hQuality.Fast => new()
        {
            HqLs = false,
            Brute = false,
            Max2SubsetPats = 1,
            NumDiffEndpointModes = 2
        },
        Bc6hQuality.Default => new()
        {
            HqLs = true,
            Brute = false,
            Max2SubsetPats = 1,
            NumDiffEndpointModes = 2
        },
        _ => new()
        {
            HqLs = true,
            Brute = true,
            Max2SubsetPats = 4,
            NumDiffEndpointModes = 2
        }
    };

    static float[] BuildLsW(byte[] wtab)
    {
        var t = new float[wtab.Length * 4];
        for (var i = 0; i < wtab.Length; i++)
        {
            var w = wtab[i] * (1f / 64f);
            t[i * 4 + 0] = w * w;
            t[i * 4 + 1] = (1f - w) * w;
            t[i * 4 + 2] = (1f - w) * (1f - w);
            t[i * 4 + 3] = w;
        }

        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float HtoF(int hbits) => BitConverter.UInt32BitsToSingle((uint)hbits << 13) * HtoFK;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HalfToBlog(int h, int bits) => (h * 64 + 30) / (31 << 16 - bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HalfToBlog16(int h) => (h * 64 + 30) / 31;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Dequant(int v, int bits)
    {
        if (bits >= 15) return v;
        if (v == 0) return 0;
        if (v == (1 << bits) - 1) return 0xFFFF;

        return (v << 16) + 0x8000 >> bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ConvertToHalf(int val) => val * 31 >> 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int QuantDequant(int half, int bits) => ConvertToHalf(Dequant(HalfToBlog(half, bits), bits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int RoundI(float x) => x >= 0f ? (int)(x + 0.5f) : (int)(x - 0.5f); // == std::round / fast_roundf_int

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float InvSqrt(float v) => 1f / MathF.Sqrt(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Sq(float x) => x * x;

    /// <summary>Encode one block of 16 RGB half-float (FP16 bit pattern) texels into a 16-byte BC6H block.</summary>
    public static void EncodeBlock(ReadOnlySpan<ushort> rgbHalf48, Span<byte> dst16, Bc6hQuality quality)
    {
        var prm = ForQuality(quality);

        // --- de-interleave to SoA + min/max/sum (SIMD: planar loads, horizontal min/max/add) ---
        Span<int> ir = stackalloc int[16], ig = stackalloc int[16], ib = stackalloc int[16];
        int ominR = int.MaxValue, ominG = int.MaxValue, ominB = int.MaxValue, omaxR = 0, omaxG = 0, omaxB = 0;
        long totR = 0, totG = 0, totB = 0;
        for (var i = 0; i < 16; i++)
        {
            int r = rgbHalf48[i * 3], g = rgbHalf48[i * 3 + 1], b = rgbHalf48[i * 3 + 2];
            ir[i] = r;
            ig[i] = g;
            ib[i] = b;
            totR += r;
            totG += g;
            totB += b;
            if (r < ominR) ominR = r;
            if (g < ominG) ominG = g;
            if (b < ominB) ominB = b;
            if (r > omaxR) omaxR = r;
            if (g > omaxG) omaxG = g;
            if (b > omaxB) omaxB = b;
        }

        Span<int> ep = stackalloc int[12];
        Span<byte> w = stackalloc byte[16];

        // Solid -> mode 13
        if (ominR == omaxR && ominG == omaxG && ominB == omaxB)
        {
            ep[0] = HalfToBlog16(ominR);
            ep[4] = HalfToBlog16(ominG);
            ep[8] = HalfToBlog16(ominB);
            w.Clear();
            Pack(13, 0, ep, w, dst16);
            return;
        }

        int meanR = (int)((totR + 8) / 16), meanG = (int)((totG + 8) / 16), meanB = (int)((totB + 8) / 16);

        // covariance on half-bits, accumulated in float (SIMD: planar FMA). Magnitudes (~1e10) blow past
        // float's 2^24 exact range, so int64 buys almost nothing here while making a portable widening
        // multiply painful. Boundary blocks may reclassify by a hair vs int64 — within the PSNR contract.
        float cov0, cov1, cov2, cov3, cov4, cov5, blockMaxVar;
        if (UseSimdGeometry)
            CovarianceSimd(ir, ig, ib, meanR, meanG, meanB, out cov0, out cov1, out cov2, out cov3, out cov4, out cov5, out blockMaxVar);
        else
        {
            cov0 = cov1 = cov2 = cov3 = cov4 = cov5 = 0;
            for (var i = 0; i < 16; i++)
            {
                float r = ir[i] - meanR, g = ig[i] - meanG, b = ib[i] - meanB;
                cov0 += r * r;
                cov1 += r * g;
                cov2 += r * b;
                cov3 += g * g;
                cov4 += g * b;
                cov5 += b * b;
            }

            blockMaxVar = MathF.Max(cov0, MathF.Max(cov3, cov5));
        }

        // float pixels (real values) + relative-error scales, computed once, planar (SIMD: bit-trick + reciprocal)
        Span<float> fr = stackalloc float[16], fg = stackalloc float[16], fb = stackalloc float[16], ps = stackalloc float[16];
        for (var i = 0; i < 16; i++)
        {
            fr[i] = HtoF(ir[i]);
            fg[i] = HtoF(ig[i]);
            fb[i] = HtoF(ib[i]);
            ps[i] = 1f / (fr[i] * fr[i] + fg[i] * fg[i] + fb[i] * fb[i] + MIN_HALF_FLOAT);
        }

        int minR, minG, minB, maxR, maxG, maxB;

        // Simple block (low variance): inset min/max endpoints
        if (blockMaxVar < TH_SIMPLE)
        {
            minR = (omaxR - ominR) / 32 + ominR;
            minG = (omaxG - ominG) / 32 + ominG;
            minB = (omaxB - ominB) / 32 + ominB;
            maxR = (omaxR - ominR) * 31 / 32 + ominR;
            maxG = (omaxG - ominG) * 31 / 32 + ominG;
            maxB = (omaxB - ominB) * 31 / 32 + ominB;
            minR = QuantDequant(minR, 10);
            minG = QuantDequant(minG, 10);
            minB = QuantDequant(minB, 10);
            maxR = QuantDequant(maxR, 10);
            maxG = QuantDequant(maxG, 10);
            maxB = QuantDequant(maxB, 10);

            AssignWeights4(fr, fg, fb, ps, w, minR, minG, minB, maxR, maxG, maxB, blockMaxVar, false, prm);
            ep[0] = HalfToBlog(minR, 10);
            ep[1] = HalfToBlog(maxR, 10);
            ep[4] = HalfToBlog(minG, 10);
            ep[5] = HalfToBlog(maxG, 10);
            ep[8] = HalfToBlog(minB, 10);
            ep[9] = HalfToBlog(maxB, 10);
            AnchorFlip1(ep, w);
            Pack(10, 0, ep, w, dst16);
            return;
        }

        // Complex block: PCA principal axis in half-bits space
        var sc = 1f / blockMaxVar;
        float wx = sc * cov0, wy = sc * cov3, wz = sc * cov5;
        float altR = cov0 * wx + cov1 * wy + cov2 * wz, altG = cov1 * wx + cov3 * wy + cov4 * wz, altB = cov2 * wx + cov4 * wy + cov5 * wz;
        var ll = altR * altR + altG * altG + altB * altB;
        float axisR = 0.57735027f, axisG = 0.57735027f, axisB = 0.57735027f;
        if (MathF.Abs(ll) >= SMALL_FLOAT_VAL)
        {
            var invL = InvSqrt(ll);
            axisR = altR * invL;
            axisG = altG * invL;
            axisB = altB * invL;
        }

        float tr = axisR * cov0 + axisG * cov1 + axisB * cov2, tg = axisR * cov1 + axisG * cov3 + axisB * cov4, tb = axisR * cov2 + axisG * cov4 + axisB * cov5;
        var principleAxisVar = tr * axisR + tg * axisG + tb * axisB;
        var invPav = 1f / (principleAxisVar + REALLY_SMALL_FLOAT_VAL);
        axisR = tr * invPav;
        axisG = tg * invPav;
        axisB = tb * invPav;
        var totalVar = cov0 + cov3 + cov5;
        var try2 = principleAxisVar < totalVar * 0.995f;

        // project onto axis -> dots (SIMD: planar FMA), then scalar argmin/argmax for the extremes
        Span<float> dots = stackalloc float[16];
        if (UseSimdGeometry)
            ProjectionSimd(ir, ig, ib, axisR, axisG, axisB, dots);
        else
            for (var i = 0; i < 16; i++)
                dots[i] = ir[i] * axisR + ig[i] * axisG + ib[i] * axisB;

        int minIdx = 0, maxIdx = 0;
        float minDot = BIG_FLOAT_VAL, maxDot = -BIG_FLOAT_VAL;
        for (var i = 0; i < 16; i++)
        {
            var dot = dots[i];
            if (dot < minDot)
            {
                minDot = dot;
                minIdx = i;
            }

            if (dot > maxDot)
            {
                maxDot = dot;
                maxIdx = i;
            }
        }

        minR = ir[minIdx];
        minG = ig[minIdx];
        minB = ib[minIdx];
        maxR = ir[maxIdx];
        maxG = ig[maxIdx];
        maxB = ib[maxIdx];
        minR = QuantDequant(minR, 10);
        minG = QuantDequant(minG, 10);
        minB = QuantDequant(minB, 10);
        maxR = QuantDequant(maxR, 10);
        maxG = QuantDequant(maxG, 10);
        maxB = QuantDequant(maxB, 10);

        var curErr = AssignWeights4(fr, fg, fb, ps, w, minR, minG, minB, maxR, maxG, maxB, blockMaxVar, try2, prm);

        var maxLsPasses = prm.HqLs ? 2 : 1;
        Span<byte> tw = stackalloc byte[16]; // trial weights, reused across passes (rewritten each iteration)
        for (var pass = 0; pass < maxLsPasses; pass++)
        {
            float z00 = 0, z10 = 0, z11 = 0, q00r = 0, tR = 0, q00g = 0, tG = 0, q00b = 0, tB = 0;
            for (var i = 0; i < 16; i++) // SIMD: gather LsW4[sel] (or per-weight histogram) + FMA
            {
                float r = ir[i], g = ig[i], b = ib[i];
                int sel = w[i];
                z00 += LsW4[sel * 4 + 0];
                z10 += LsW4[sel * 4 + 1];
                z11 += LsW4[sel * 4 + 2];
                var wt = LsW4[sel * 4 + 3];
                q00r += wt * r;
                tR += r;
                q00g += wt * g;
                tG += g;
                q00b += wt * b;
                tB += b;
            }

            float q10r = tR - q00r, q10g = tG - q00g, q10b = tB - q00b, z01 = z10;
            var det = z00 * z11 - z01 * z10;
            if (MathF.Abs(det) < SMALL_FLOAT_VAL) break;

            det = 1f / det;
            float iz00 = z11 * det, iz01 = -z01 * det, iz10 = -z10 * det, iz11 = z00 * det;
            var tMaxR = Math.Clamp(RoundI(iz00 * q00r + iz01 * q10r), 0, MAX_BC6H_HALF);
            var tMinR = Math.Clamp(RoundI(iz10 * q00r + iz11 * q10r), 0, MAX_BC6H_HALF);
            var tMaxG = Math.Clamp(RoundI(iz00 * q00g + iz01 * q10g), 0, MAX_BC6H_HALF);
            var tMinG = Math.Clamp(RoundI(iz10 * q00g + iz11 * q10g), 0, MAX_BC6H_HALF);
            var tMaxB = Math.Clamp(RoundI(iz00 * q00b + iz01 * q10b), 0, MAX_BC6H_HALF);
            var tMinB = Math.Clamp(RoundI(iz10 * q00b + iz11 * q10b), 0, MAX_BC6H_HALF);
            tMinR = QuantDequant(tMinR, 10);
            tMinG = QuantDequant(tMinG, 10);
            tMinB = QuantDequant(tMinB, 10);
            tMaxR = QuantDequant(tMaxR, 10);
            tMaxG = QuantDequant(tMaxG, 10);
            tMaxB = QuantDequant(tMaxB, 10);
            var trialErr = AssignWeights4(fr, fg, fb, ps, tw, tMinR, tMinG, tMinB, tMaxR, tMaxG, tMaxB, blockMaxVar, try2, prm);
            if (trialErr < curErr)
            {
                curErr = trialErr;
                minR = tMinR;
                maxR = tMaxR;
                minG = tMinG;
                maxG = tMaxG;
                minB = tMinB;
                maxB = tMaxB;
                tw.CopyTo(w);
            }
            else break;
        }

        ep[0] = HalfToBlog(minR, 10);
        ep[1] = HalfToBlog(maxR, 10);
        ep[4] = HalfToBlog(minG, 10);
        ep[5] = HalfToBlog(maxG, 10);
        ep[8] = HalfToBlog(minB, 10);
        ep[9] = HalfToBlog(maxB, 10);
        AnchorFlip1(ep, w);

        int mode = 10, partition = 0;
        if (prm.Max2SubsetPats > 0 && try2 && blockMaxVar > TH_COMPLEX)
            TwoSubset(ir, ig, ib, fr, fg, fb, ps, ref curErr, ref mode, ref partition, ep, w, meanR, meanG, meanB, axisR, axisG, axisB, prm);

        Pack(mode, partition, ep, w, dst16);
    }

    // 1-subset anchor: texel-0 weight MSB (bit 3) must be 0; if set, swap endpoints + invert weights.
    static void AnchorFlip1(Span<int> ep, Span<byte> w)
    {
        if ((w[0] & 8) != 0)
        {
            for (var i = 0; i < 16; i++) w[i] = (byte)(15 - w[i]);
            for (var c = 0; c < 3; c++) (ep[c * 4 + 0], ep[c * 4 + 1]) = (ep[c * 4 + 1], ep[c * 4 + 0]);
        }
    }

    // Assign 4-bit weights minimizing 1/luma^2-weighted squared error; returns that error.
    // SIMD: cr/cg/cb are planar; the Brute path is a branchless argmin over 16 candidates (vectorizable);
    // the binary-search path is the scalar-friendly alternative.
    static double AssignWeights4(
        ReadOnlySpan<float> qr16,
        ReadOnlySpan<float> qg16,
        ReadOnlySpan<float> qb16,
        ReadOnlySpan<float> scales,
        Span<byte> weights,
        int minR,
        int minG,
        int minB,
        int maxR,
        int maxG,
        int maxB,
        float blockMaxVar,
        bool try2,
        Params prm)
    {
        Span<float> cr = stackalloc float[16], cg = stackalloc float[16], cb = stackalloc float[16];
        if (UseSimdWeights)
            ComputeCandidatesSimd(minR, minG, minB, maxR, maxG, maxB, cr, cg, cb);
        else
            for (var i = 0; i < 16; i++)
            {
                int wv = Bc6hTables.Weight4[i];
                cr[i] = HtoF(minR * (64 - wv) + maxR * wv + 32 >> 6);
                cg[i] = HtoF(minG * (64 - wv) + maxG * wv + 32 >> 6);
                cb[i] = HtoF(minB * (64 - wv) + maxB * wv + 32 >> 6);
            }

        double totalErr = 0;
        if (prm.Brute)
        {
            if (UseSimdWeights) return BrutePathSimd(cr, cg, cb, qr16, qg16, qb16, scales, weights);

            for (var i = 0; i < 16; i++)
            {
                float qr = qr16[i], qg = qg16[i], qb = qb16[i];
                var bestE = Sq(cr[0] - qr) + Sq(cg[0] - qg) + Sq(cb[0] - qb);
                var bestIdx = 0;
                for (var j = 1; j < 16; j++)
                {
                    var e = Sq(cr[j] - qr) + Sq(cg[j] - qg) + Sq(cb[j] - qb);
                    if (e < bestE)
                    {
                        bestE = e;
                        bestIdx = j;
                    }
                }

                weights[i] = (byte)bestIdx;
                totalErr += bestE * scales[i];
            }

            return totalErr;
        }

        float dirR = cr[15] - cr[0], dirG = cg[15] - cg[0], dirB = cb[15] - cb[0];
        Span<float> dots = stackalloc float[16];
        for (var i = 0; i < 16; i++) dots[i] = cr[i] * dirR + cg[i] * dirG + cb[i] * dirB;
        Span<float> midDots = stackalloc float[15];
        var mono = true;
        for (var i = 0; i < 15; i++)
        {
            midDots[i] = (dots[i] + dots[i + 1]) * 0.5f;
            if (dots[i] > dots[i + 1]) mono = false;
        }

        var checkMore = blockMaxVar > TH_VERYCOMPLEX;

        if (!mono)
        {
            for (var i = 0; i < 16; i++)
            {
                float qr = qr16[i], qg = qg16[i], qb = qb16[i];
                var d = qr * dirR + qg * dirG + qb * dirB;
                var bestE = MathF.Abs(d - dots[0]);
                var bestIdx = 0;
                for (var j = 1; j < 16; j++)
                {
                    var e = MathF.Abs(d - dots[j]);
                    if (e < bestE)
                    {
                        bestE = e;
                        bestIdx = j;
                    }
                }

                weights[i] = (byte)bestIdx;
                totalErr += (Sq(qr - cr[bestIdx]) + Sq(qg - cg[bestIdx]) + Sq(qb - cb[bestIdx])) * scales[i];
            }
        }
        else if (!try2 || !checkMore)
        {
            for (var i = 0; i < 16; i++)
            {
                float qr = qr16[i], qg = qg16[i], qb = qb16[i];
                var d = qr * dirR + qg * dirG + qb * dirB;
                int low = 0, mid = low + 7;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low + 3;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low + 1;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low;
                if (d >= midDots[mid]) low = mid + 1;
                weights[i] = (byte)low;
                totalErr += (Sq(qr - cr[low]) + Sq(qg - cg[low]) + Sq(qb - cb[low])) * scales[i];
            }
        }
        else
        {
            for (var i = 0; i < 16; i++)
            {
                float qr = qr16[i], qg = qg16[i], qb = qb16[i];
                var d = qr * dirR + qg * dirG + qb * dirB;
                int low = 0, mid = low + 7;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low + 3;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low + 1;
                if (d >= midDots[mid]) low = mid + 1;
                mid = low;
                if (d >= midDots[mid]) low = mid + 1;
                var bestIdx = low;
                var err = Sq(qr - cr[bestIdx]) + Sq(qg - cg[bestIdx]) + Sq(qb - cb[bestIdx]);
                var alt = bestIdx + 1;
                if (alt > 15) alt = 13;
                var altE = Sq(qr - cr[alt]) + Sq(qg - cg[alt]) + Sq(qb - cb[alt]);
                if (altE < err)
                {
                    err = altE;
                    bestIdx = alt;
                }

                var alt2 = bestIdx - 1;
                if (alt2 < 0) alt2 = 2;
                var altE2 = Sq(qr - cr[alt2]) + Sq(qg - cg[alt2]) + Sq(qb - cb[alt2]);
                if (altE2 < err)
                {
                    err = altE2;
                    bestIdx = alt2;
                }

                weights[i] = (byte)bestIdx;
                totalErr += err * scales[i];
            }
        }

        return totalErr;
    }

    // Forward pack_bc6h_block (general; handles 1- and 2-subset).
    static void Pack(int mode, int partition, ReadOnlySpan<int> ep, ReadOnlySpan<byte> weights, Span<byte> dst16)
    {
        ulong l = ModeBits[mode], h = 0;
        var pos = mode >= 2 ? 5 : 2;
        var numSubsets = mode <= 9 ? 2 : 1;
        var layout = Bc6hTables.BitLayouts[mode];
        for (var e = 0; e < layout.Length; e += 4)
        {
            int comp = layout[e], idx = layout[e + 1], last = layout[e + 2], first = layout[e + 3];
            var v = (uint)(comp == 3 ? partition : ep[comp * 4 + idx]);
            if (first == -1) WriteBits(v >> last & 1u, 1, ref pos, ref l, ref h);
            else
            {
                var total = Math.Abs(last - first) + 1;
                var vv = v >> Math.Min(first, last) & (1u << total) - 1u;
                if (first > last) WriteRevBits(vv, total, ref pos, ref l, ref h);
                else WriteBits(vv, total, ref pos, ref l, ref h);
            }
        }

        var patBase = partition * 16;
        for (var i = 0; i < 16; i++)
        {
            var nb = numSubsets == 1 ? 4 : 3;
            if (numSubsets == 2)
            {
                if ((Bc6hTables.Patterns[patBase + i] & 0x80) != 0) nb--;
            }
            else if (i == 0) nb--;

            WriteBits(weights[i], nb, ref pos, ref l, ref h);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dst16, (uint)l);
        BinaryPrimitives.WriteUInt32LittleEndian(dst16.Slice(4), (uint)(l >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(dst16.Slice(8), (uint)h);
        BinaryPrimitives.WriteUInt32LittleEndian(dst16.Slice(12), (uint)(h >> 32));
    }

    static void WriteBits(uint v, int n, ref int pos, ref ulong l, ref ulong h)
    {
        if (pos < 64)
        {
            l |= (ulong)v << pos;
            if (pos + n > 64) h |= (ulong)v >> 64 - pos;
        }
        else h |= (ulong)v << pos - 64;

        pos += n;
    }

    static void WriteRevBits(uint v, int n, ref int pos, ref ulong l, ref ulong h)
    {
        for (var i = 0; i < n; i++) WriteBits(v >> n - 1 - i & 1u, 1, ref pos, ref l, ref h);
    }

    struct Params
    {
        public bool HqLs;
        public bool Brute;
        public int Max2SubsetPats;
        public int NumDiffEndpointModes;
    }
}