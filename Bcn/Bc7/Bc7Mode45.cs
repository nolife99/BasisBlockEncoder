// Bc7Mode45.cs — Modes 4 & 5 (dual plane), port of pack_mode4_or_5.
// One colour plane (RGB) + one scalar plane carrying the decorrelated channel, which is rotated into
// the alpha slot (component rotation). Mode 4: 5-bit RGB / 6-bit scalar, 2+3-bit dual indices with an
// index-selection bit. Mode 5: 7-bit RGB / 8-bit scalar, 2+2-bit dual indices.
// The least-squares endpoint refinement is not applied here (a quality refinement, not correctness).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bcn.Bc7;

internal static partial class Bc7Block
{
    private static bool PackMode4Or5(scoped Span<byte> block, scoped ReadOnlySpan<ColorRgba> origPx,
        int dpChan, float sseEstToBeat, Bc7Flags flags, bool wantSse, out float estSse, out uint trueSse)
    {
        estSse = float.MaxValue;
        trueSse = uint.MaxValue;
        // Rotate the decorrelated channel into the alpha slot (dpChan is 0/1/2 on the RGB path).
        Span<ColorRgba> px = stackalloc ColorRgba[16];
        origPx.CopyTo(px);
        if (dpChan != 3)
            for (int i = 0; i < 16; i++)
            {
                ColorRgba c = px[i]; byte a = c.A;
                if (dpChan == 0) { byte t = c.R; c.R = a; c.A = t; }
                else if (dpChan == 1) { byte t = c.G; c.G = a; c.A = t; }
                else { byte t = c.B; c.B = a; c.A = t; }
                px[i] = c;
            }

        int totR = 0, totG = 0, totB = 0, totA = 0, minA = 255, maxA = 0;
        ref ColorRgba _pp0 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq0 = ref Unsafe.Add(ref _pp0, i);
            int r = _qq0.R, g = _qq0.G, b = _qq0.B, a = _qq0.A;
            totR += r; totG += g; totB += b; totA += a;
            if (a < minA) minA = a; if (a > maxA) maxA = a;
        }
        int meanR = (totR + 8) >> 4, meanG = (totG + 8) >> 4, meanB = (totB + 8) >> 4;

        Span<int> icov = stackalloc int[6];
        ref ColorRgba _pp1 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq1 = ref Unsafe.Add(ref _pp1, i);
            int r = _qq1.R - meanR, g = _qq1.G - meanG, b = _qq1.B - meanB;
            icov[0] += r * r; icov[1] += r * g; icov[2] += r * b;
            icov[3] += g * g; icov[4] += g * b; icov[5] += b * b;
        }
        int bmv = Math.Max(icov[0], Math.Max(icov[3], icov[5]));
        Span<float> cov = stackalloc float[6];
        for (int i = 0; i < 6; i++) cov[i] = icov[i];
        EstimatePrincipalAxis(cov, bmv, out float ax, out float ay, out float az);
        float rgbSlam = EstimateSlamToLineSse(cov, ax, ay, az, out _);

        int sr = 306, sg = 601, sb = 117;
        float kk = MathF.Max(MathF.Abs(ax), MathF.Max(MathF.Abs(ay), MathF.Abs(az)));
        if (kk >= SmallFloat) { float m = 2048f / kk; sr = (int)(ax * m); sg = (int)(ay * m); sb = (int)(az * m); }
        sr <<= 4; sg <<= 4; sb <<= 4;
        int lowDot = int.MaxValue, highDot = int.MinValue;
        ref ColorRgba _pp2 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq2 = ref Unsafe.Add(ref _pp2, i);
            int dot = _qq2.R * sr + _qq2.G * sg + _qq2.B * sb + i;
            if (dot < lowDot) lowDot = dot;
            if (dot > highDot) highDot = dot;
        }
        int loc = lowDot & 15, hic = highDot & 15;

        Span<int> rgbSpans = stackalloc int[4];
        rgbSpans[0] = px[hic].R - px[loc].R; rgbSpans[1] = px[hic].G - px[loc].G; rgbSpans[2] = px[hic].B - px[loc].B;
        int aSpan = maxA - minA;

        float m4r3a2 = rgbSlam + AnalyticalQuantEstSse(32, 8, 3, rgbSpans, default, 1f, 16) + AnalyticalQuantEstSse(64, 4, aSpan, 1f, 1f, 16);
        float m4r2a3 = rgbSlam + AnalyticalQuantEstSse(32, 4, 3, rgbSpans, default, 1f, 16) + AnalyticalQuantEstSse(64, 8, aSpan, 1f, 1f, 16);
        float m5     = rgbSlam + AnalyticalQuantEstSse(128, 4, 3, rgbSpans, default, 1f, 16) + AnalyticalQuantEstSse(256, 4, aSpan, 1f, 1f, 16);

        uint rot = (uint)((dpChan + 1) & 3);
        Span<int> w0 = stackalloc int[16]; Span<int> w1 = stackalloc int[16];
        Span<float> xl = stackalloc float[4]; Span<float> xh = stackalloc float[4];
        float nal, nah;

        if (m5 < MathF.Min(m4r3a2, m4r2a3))
        {
            estSse = m5;
            if (m5 >= sseEstToBeat) return false;
            int lr = To7(px[loc].R), lg = To7(px[loc].G), lb = To7(px[loc].B), la = minA;
            int hr = To7(px[hic].R), hg = To7(px[hic].G), hb = To7(px[hic].B), ha = maxA;
            EvalRgbPlane(px, w0, From7(lr), From7(lg), From7(lb), From7(hr), From7(hg), From7(hb), 4);
            if (ComputeLs3D(w0, px, Bc7Tables.LsTab2, totR, totG, totB, xl, xh))
            {
                lr = RoundPos(xl[0]*127f/255f); lg = RoundPos(xl[1]*127f/255f); lb = RoundPos(xl[2]*127f/255f);
                hr = RoundPos(xh[0]*127f/255f); hg = RoundPos(xh[1]*127f/255f); hb = RoundPos(xh[2]*127f/255f);
                EvalRgbPlane(px, w0, From7(lr), From7(lg), From7(lb), From7(hr), From7(hg), From7(hb), 4);
            }
            EvalAPlane(px, w1, la, ha, 4);
            if (ComputeLs1D(w1, px, 3, Bc7Tables.LsTab2, totA, out nal, out nah)) { la = RoundPos(nal); ha = RoundPos(nah); EvalAPlane(px, w1, la, ha, 4); }
            if (wantSse) { long sx = SseDualPlane(From7(lr), From7(lg), From7(lb), la, From7(hr), From7(hg), From7(hb), ha, Bc7Tables.Weights2, Bc7Tables.Weights2, w0, w1, px);
              trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
            EncodeMode5(block, lr, lg, lb, la, hr, hg, hb, ha, w0, w1, rot);
        }
        else if (m4r3a2 < m4r2a3)
        {
            estSse = m4r3a2;
            if (m4r3a2 >= sseEstToBeat) return false;
            int lr = To5(px[loc].R), lg = To5(px[loc].G), lb = To5(px[loc].B), la = To6(minA);
            int hr = To5(px[hic].R), hg = To5(px[hic].G), hb = To5(px[hic].B), ha = To6(maxA);
            EvalRgbPlane(px, w0, From5(lr), From5(lg), From5(lb), From5(hr), From5(hg), From5(hb), 8); // rgb 3-bit
            if (ComputeLs3D(w0, px, Bc7Tables.LsTab3, totR, totG, totB, xl, xh))
            {
                lr = RoundPos(xl[0]*31f/255f); lg = RoundPos(xl[1]*31f/255f); lb = RoundPos(xl[2]*31f/255f);
                hr = RoundPos(xh[0]*31f/255f); hg = RoundPos(xh[1]*31f/255f); hb = RoundPos(xh[2]*31f/255f);
                EvalRgbPlane(px, w0, From5(lr), From5(lg), From5(lb), From5(hr), From5(hg), From5(hb), 8);
            }
            EvalAPlane(px, w1, From6(la), From6(ha), 4);                                                // a 2-bit
            if (ComputeLs1D(w1, px, 3, Bc7Tables.LsTab2, totA, out nal, out nah)) { la = RoundPos(nal*63f/255f); ha = RoundPos(nah*63f/255f); EvalAPlane(px, w1, From6(la), From6(ha), 4); }
            if (wantSse) { long sx = SseDualPlane(From5(lr), From5(lg), From5(lb), From6(la), From5(hr), From5(hg), From5(hb), From6(ha), Bc7Tables.Weights3, Bc7Tables.Weights2, w0, w1, px);
              trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
            EncodeMode4(block, lr, lg, lb, la, hr, hg, hb, ha, w0, w1, rot, 1);
        }
        else
        {
            estSse = m4r2a3;
            if (m4r2a3 >= sseEstToBeat) return false;
            int lr = To5(px[loc].R), lg = To5(px[loc].G), lb = To5(px[loc].B), la = To6(minA);
            int hr = To5(px[hic].R), hg = To5(px[hic].G), hb = To5(px[hic].B), ha = To6(maxA);
            EvalRgbPlane(px, w0, From5(lr), From5(lg), From5(lb), From5(hr), From5(hg), From5(hb), 4); // rgb 2-bit
            if (ComputeLs3D(w0, px, Bc7Tables.LsTab2, totR, totG, totB, xl, xh))
            {
                lr = RoundPos(xl[0]*31f/255f); lg = RoundPos(xl[1]*31f/255f); lb = RoundPos(xl[2]*31f/255f);
                hr = RoundPos(xh[0]*31f/255f); hg = RoundPos(xh[1]*31f/255f); hb = RoundPos(xh[2]*31f/255f);
                EvalRgbPlane(px, w0, From5(lr), From5(lg), From5(lb), From5(hr), From5(hg), From5(hb), 4);
            }
            EvalAPlane(px, w1, From6(la), From6(ha), 8);                                                // a 3-bit
            if (ComputeLs1D(w1, px, 3, Bc7Tables.LsTab3, totA, out nal, out nah)) { la = RoundPos(nal*63f/255f); ha = RoundPos(nah*63f/255f); EvalAPlane(px, w1, From6(la), From6(ha), 8); }
            if (wantSse) { long sx = SseDualPlane(From5(lr), From5(lg), From5(lb), From6(la), From5(hr), From5(hg), From5(hb), From6(ha), Bc7Tables.Weights2, Bc7Tables.Weights3, w0, w1, px);
              trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
            EncodeMode4(block, lr, lg, lb, la, hr, hg, hb, ha, w0, w1, rot, 0);
        }
        return true;
    }

    // Colour-plane index eval given 8-bit endpoints; `levels` is the weight-level count (4 -> 2-bit, 8 -> 3-bit).
    private static void EvalRgbPlane(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w,
        int lr, int lg, int lb, int hr, int hg, int hb, int levels)
    {
        int dr = hr - lr, dg = hg - lg, db = hb - lb, mx = levels - 1;
        float f = (levels - 1) / (dr * dr + dg * dg + db * db + 0.00000125f);
        int sofs = lr * dr + lg * dg + lb * db;
        EvalSelRgb(px, w, dr, dg, db, -sofs, f, mx);
    }

    // Scalar-plane (rotated channel, stored in .A) index eval; 1-D.
    private static void EvalAPlane(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w, int la, int ha, int levels)
    {
        int da = ha - la, mx = levels - 1;
        float f = (levels - 1) / (da + 0.00000125f);
        EvalSelA(px, w, la, f, mx);
    }

    private static void EncodeMode5(scoped Span<byte> block,
        int lr, int lg, int lb, int la, int hr, int hg, int hb, int ha,
        scoped ReadOnlySpan<int> colW, scoped ReadOnlySpan<int> alpW, uint rot)
    {
        uint Lr = (uint)lr, Lg = (uint)lg, Lb = (uint)lb, La = (uint)la, Hr = (uint)hr, Hg = (uint)hg, Hb = (uint)hb, Ha = (uint)ha;
        uint colorInv = 0, alphaInv = 0;
        if ((colW[0] & 2) != 0) { (Lr, Hr) = (Hr, Lr); (Lg, Hg) = (Hg, Lg); (Lb, Hb) = (Hb, Lb); colorInv = 3; }
        if ((alpW[0] & 2) != 0) { (La, Ha) = (Ha, La); alphaInv = 3; }

        ulong low = (1UL << 5) | ((ulong)rot << 6)
            | ((ulong)Lr << 8)  | ((ulong)Hr << 15)
            | ((ulong)Lg << 22) | ((ulong)Hg << 29)
            | ((ulong)Lb << 36) | ((ulong)Hb << 43)
            | ((ulong)La << 50) | ((ulong)Ha << 58);
        block[0] = (byte)low;         block[1] = (byte)(low >> 8);  block[2] = (byte)(low >> 16); block[3] = (byte)(low >> 24);
        block[4] = (byte)(low >> 32); block[5] = (byte)(low >> 40); block[6] = (byte)(low >> 48); block[7] = (byte)(low >> 56);

        ulong high = ((ulong)Ha >> 6) & 3;
        int ofs = 2;
        for (int i = 0; i < 16; i++) { ulong w = (uint)(colW[i] ^ (int)colorInv); high |= w << ofs; ofs += 2 - (i == 0 ? 1 : 0); }
        for (int i = 0; i < 16; i++) { ulong w = (uint)(alpW[i] ^ (int)alphaInv); high |= w << ofs; ofs += 2 - (i == 0 ? 1 : 0); }
        block[8]  = (byte)high;         block[9]  = (byte)(high >> 8);  block[10] = (byte)(high >> 16); block[11] = (byte)(high >> 24);
        block[12] = (byte)(high >> 32); block[13] = (byte)(high >> 40); block[14] = (byte)(high >> 48); block[15] = (byte)(high >> 56);
    }

    // BC7 mode-4 block writer (encode_mode4_rgba_block). 5-bit RGB, 6-bit A, dual 2/3-bit indices + index-flag.
    private static void EncodeMode4(scoped Span<byte> block,
        int lr, int lg, int lb, int la, int hr, int hg, int hb, int ha,
        scoped ReadOnlySpan<int> w0, scoped ReadOnlySpan<int> w1, uint rot, uint flag)
    {
        uint Lr = (uint)lr, Lg = (uint)lg, Lb = (uint)lb, La = (uint)la, Hr = (uint)hr, Hg = (uint)hg, Hb = (uint)hb, Ha = (uint)ha;
        uint inv0 = 0, inv1 = 0;
        ReadOnlySpan<int> p2 = flag != 0 ? w1 : w0;   // 2-bit plane
        ReadOnlySpan<int> p3 = flag != 0 ? w0 : w1;   // 3-bit plane

        if ((p3[0] & 4) != 0)
        {
            inv0 = 7;
            if (flag != 0) { (Lr, Hr) = (Hr, Lr); (Lg, Hg) = (Hg, Lg); (Lb, Hb) = (Hb, Lb); }
            else { (La, Ha) = (Ha, La); }
        }
        if ((p2[0] & 2) != 0)
        {
            inv1 = 3;
            if (flag != 0) { (La, Ha) = (Ha, La); }
            else { (Lr, Hr) = (Hr, Lr); (Lg, Hg) = (Hg, Lg); (Lb, Hb) = (Hb, Lb); }
        }

        block[0] = (byte)(0b10000 | (rot << 5) | (flag << 7));

        ulong x = Lr | (Hr << 5) | (Lg << 10) | (Hg << 15) | (Lb << 20) | (Hb << 25) | ((ulong)La << 30) | ((ulong)Ha << 36);
        block[1] = (byte)x; block[2] = (byte)(x >> 8); block[3] = (byte)(x >> 16); block[4] = (byte)(x >> 24); block[5] = (byte)(x >> 32);
        x >>= 40;
        int ofs0 = 2;
        for (int i = 0; i < 16; i++) { ulong w = (uint)(p2[i] ^ (int)inv1); x |= w << ofs0; ofs0 += 2 - (i == 0 ? 1 : 0); }
        block[6] = (byte)x; block[7] = (byte)(x >> 8); block[8] = (byte)(x >> 16); block[9] = (byte)(x >> 24);
        x >>= 32;
        int ofs1 = 1;
        for (int i = 0; i < 16; i++) { ulong w = (uint)(p3[i] ^ (int)inv0); x |= w << ofs1; ofs1 += 3 - (i == 0 ? 1 : 0); }
        block[10] = (byte)x; block[11] = (byte)(x >> 8); block[12] = (byte)(x >> 16); block[13] = (byte)(x >> 24); block[14] = (byte)(x >> 32); block[15] = (byte)(x >> 40);
    }
}
