// Bc7Mode7.cs — Mode 7 (2 subsets, RGBA). Port of pack_mode7_rgba.
// 5-bit endpoints, 4 unique p-bits, 2-bit indices, 64 partitions. Uses fixed p-bits; no LS refinement.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bcn.Bc7;

internal static partial class Bc7Block
{
    private static bool PackMode7Rgba(scoped Span<byte> block, scoped ReadOnlySpan<ColorRgba> px,
        float axR, float axG, float axB, float axA,
        int meanR, int meanG, int meanB, int meanA,
        float sseEstToBeat, Bc7Flags flags, bool wantSse, out float estSse, out uint trueSse)
    {
        estSse = float.MaxValue;
        trueSse = uint.MaxValue;

        // (a) partition selection: split by the block 4D axis sign, pick the closest 2-subset pattern.
        int desired = 0;
        ref ColorRgba _pp0 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq0 = ref Unsafe.Add(ref _pp0, i);
            float r = _qq0.R - meanR, g = _qq0.G - meanG, b = _qq0.B - meanB, a = _qq0.A - meanA;
            int s = (r * axR + g * axG + b * axB + a * axA) > 0f ? 1 : 0;
            desired |= s << i;
        }
        int best = int.MaxValue;
        for (int p = 0; p < 64; p++)
        {
            int diff = BitOperations.PopCount((uint)((Bc7Tables.Part2Bitmasks[p] ^ desired) & 0xFFFF));
            int md = Math.Min(diff, 16 - diff);
            int key = (md << 8) | p;
            if (key < best) best = key;
        }
        int bestPat = best & 0xFF;
        int patBits = Bc7Tables.Part2Bitmasks[bestPat];

        // (b) per-subset totals/means
        Span<int> tR = stackalloc int[2]; Span<int> tG = stackalloc int[2]; Span<int> tB = stackalloc int[2];
        Span<int> tA = stackalloc int[2]; Span<int> tC = stackalloc int[2];
        ref ColorRgba _pp1 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq1 = ref Unsafe.Add(ref _pp1, i);
            int s = (patBits >> i) & 1;
            tR[s] += _qq1.R; tG[s] += _qq1.G; tB[s] += _qq1.B; tA[s] += _qq1.A; tC[s]++;
        }
        if (tC[0] == 0 || tC[1] == 0) return false;
        Span<int> mR = stackalloc int[2]; Span<int> mG = stackalloc int[2]; Span<int> mB = stackalloc int[2]; Span<int> mA = stackalloc int[2];
        for (int s = 0; s < 2; s++) { int t = tC[s], h = t >> 1; mR[s] = (tR[s] + h) / t; mG[s] = (tG[s] + h) / t; mB[s] = (tB[s] + h) / t; mA[s] = (tA[s] + h) / t; }

        // (c) per-subset 4D covariance
        Span<int> icov = stackalloc int[20]; // [s*10 + k]
        ref ColorRgba _pp3 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            int s = (patBits >> i) & 1, o = s * 10;
            ref readonly ColorRgba _qq3 = ref Unsafe.Add(ref _pp3, i);
            int r = _qq3.R - mR[s], g = _qq3.G - mG[s], b = _qq3.B - mB[s], a = _qq3.A - mA[s];
            icov[o]+=r*r; icov[o+1]+=r*g; icov[o+2]+=r*b; icov[o+3]+=r*a;
            icov[o+4]+=g*g; icov[o+5]+=g*b; icov[o+6]+=g*a;
            icov[o+7]+=b*b; icov[o+8]+=b*a; icov[o+9]+=a*a;
        }

        // (d) per-subset 4D axis (two power iterations, unnormalized) + slam + integer axis + projection
        Span<int> ar = stackalloc int[2]; Span<int> ag = stackalloc int[2]; Span<int> ab = stackalloc int[2]; Span<int> aa = stackalloc int[2];
        Span<float> cov = stackalloc float[10];
        float slam = 0;
        for (int s = 0; s < 2; s++)
        {
            int o = s * 10;
            int bmv = Math.Max(Math.Max(icov[o], icov[o + 4]), Math.Max(icov[o + 7], icov[o + 9]));
            for (int j = 0; j < 10; j++) cov[j] = icov[o + j];
            float sc = bmv != 0 ? 1f / bmv : 0f;
            float wx = sc * cov[0], wy = sc * cov[4], wz = sc * cov[7], wa = sc * cov[9];
            float x0 = cov[0]*wx + cov[1]*wy + cov[2]*wz + cov[3]*wa;
            float y0 = cov[1]*wx + cov[4]*wy + cov[5]*wz + cov[6]*wa;
            float z0 = cov[2]*wx + cov[5]*wy + cov[7]*wz + cov[8]*wa;
            float w0 = cov[3]*wx + cov[6]*wy + cov[8]*wz + cov[9]*wa;
            float x1 = cov[0]*x0 + cov[1]*y0 + cov[2]*z0 + cov[3]*w0;
            float y1 = cov[1]*x0 + cov[4]*y0 + cov[5]*z0 + cov[6]*w0;
            float z1 = cov[2]*x0 + cov[5]*y0 + cov[7]*z0 + cov[8]*w0;
            float w1 = cov[3]*x0 + cov[6]*y0 + cov[8]*z0 + cov[9]*w0;
            slam += EstimateSlamToLineSse4D(cov, x1, y1, z1, w1, out _);
            int sr = 256, sg = 256, sb = 256, sa = 256;
            float k = MathF.Max(MathF.Max(MathF.Abs(x1), MathF.Abs(y1)), MathF.Max(MathF.Abs(z1), MathF.Abs(w1)));
            if (k >= SmallFloat) { float m = 2048f / k; sr=(int)(x1*m); sg=(int)(y1*m); sb=(int)(z1*m); sa=(int)(w1*m); }
            ar[s]=sr<<4; ag[s]=sg<<4; ab[s]=sb<<4; aa[s]=sa<<4;
        }
        Span<int> lowDot = stackalloc int[2]; lowDot[0]=lowDot[1]=int.MaxValue;
        Span<int> highDot = stackalloc int[2]; highDot[0]=highDot[1]=int.MinValue;
        ref ColorRgba _pp2 = ref MemoryMarshal.GetReference(px);
        for (int i = 0; i < 16; i++)
        {
            ref readonly ColorRgba _qq2 = ref Unsafe.Add(ref _pp2, i);
            int s = (patBits >> i) & 1;
            int dot = _qq2.R*ar[s] + _qq2.G*ag[s] + _qq2.B*ab[s] + _qq2.A*aa[s] + i;
            if (dot < lowDot[s]) lowDot[s] = dot;
            if (dot > highDot[s]) highDot[s] = dot;
        }
        Span<int> loC = stackalloc int[2]; Span<int> hiC = stackalloc int[2];
        for (int s = 0; s < 2; s++) { loC[s]=lowDot[s]&15; hiC[s]=highDot[s]&15; }

        // (e) estimate; bail if it can't beat the ceiling
        float disc = (flags & Bc7Flags.PBitOpt) != 0 ? UniquePbitDiscount : 1f;
        Span<int> spans = stackalloc int[4];
        float quant = 0;
        for (int s = 0; s < 2; s++)
        {
            int lc = loC[s], hc = hiC[s];
            spans[0]=px[hc].R-px[lc].R; spans[1]=px[hc].G-px[lc].G; spans[2]=px[hc].B-px[lc].B; spans[3]=px[hc].A-px[lc].A;
            quant += AnalyticalQuantEstSse(32, 4, 4, spans, default, disc, tC[s]);
        }
        estSse = slam + quant;
        if (estSse >= sseEstToBeat) return false;

        // (f) endpoints (p-bit optimization or fixed p-bits), LS refinement, eval, encode.
        bool pbitOpt = (flags & Bc7Flags.PBitOpt) != 0;
        Span<uint> lr = stackalloc uint[2]; Span<uint> lg = stackalloc uint[2]; Span<uint> lb = stackalloc uint[2]; Span<uint> la = stackalloc uint[2];
        Span<uint> hr = stackalloc uint[2]; Span<uint> hg = stackalloc uint[2]; Span<uint> hb = stackalloc uint[2]; Span<uint> ha = stackalloc uint[2];
        Span<uint> pbits = stackalloc uint[4];
        Span<float> xl = stackalloc float[4]; Span<float> xh = stackalloc float[4];
        Span<float> sxl = stackalloc float[4]; Span<float> sxh = stackalloc float[4];
        Span<int> minC = stackalloc int[4]; Span<int> maxC = stackalloc int[4]; Span<uint> pbo = stackalloc uint[2];
        Span<int> eLoR = stackalloc int[2]; Span<int> eLoG = stackalloc int[2]; Span<int> eLoB = stackalloc int[2]; Span<int> eLoA = stackalloc int[2];
        Span<int> eHiR = stackalloc int[2]; Span<int> eHiG = stackalloc int[2]; Span<int> eHiB = stackalloc int[2]; Span<int> eHiA = stackalloc int[2];
        for (int s = 0; s < 2; s++)
        {
            int lc = loC[s], hc = hiC[s];
            if (pbitOpt)
            {
                sxl[0]=px[lc].R/255f; sxl[1]=px[lc].G/255f; sxl[2]=px[lc].B/255f; sxl[3]=px[lc].A/255f;
                sxh[0]=px[hc].R/255f; sxh[1]=px[hc].G/255f; sxh[2]=px[hc].B/255f; sxh[3]=px[hc].A/255f;
                DetermineUniquePbits(4, 5, sxl, sxh, minC, maxC, pbo);
                pbits[s*2]=pbo[0]; pbits[s*2+1]=pbo[1];
                lr[s]=(uint)minC[0]; lg[s]=(uint)minC[1]; lb[s]=(uint)minC[2]; la[s]=(uint)minC[3];
                hr[s]=(uint)maxC[0]; hg[s]=(uint)maxC[1]; hb[s]=(uint)maxC[2]; ha[s]=(uint)maxC[3];
            }
            else
            {
                uint lp = (uint)(px[lc].A >= 129 ? 1 : 0), hp = (uint)(px[hc].A >= 129 ? 1 : 0);
                pbits[s*2] = lp; pbits[s*2+1] = hp;
                lr[s]=(uint)To5(px[lc].R,lp); lg[s]=(uint)To5(px[lc].G,lp); lb[s]=(uint)To5(px[lc].B,lp); la[s]=(uint)To5(px[lc].A,lp);
                hr[s]=(uint)To5(px[hc].R,hp); hg[s]=(uint)To5(px[hc].G,hp); hb[s]=(uint)To5(px[hc].B,hp); ha[s]=(uint)To5(px[hc].A,hp);
            }
        }
        Span<int> w = stackalloc int[16];
        EvalWeightsMode7(px, w, lr, lg, lb, la, hr, hg, hb, ha, pbits, patBits);
        for (int s = 0; s < 2; s++)
        {
            if (!ComputeLs4DSub(w, px, patBits, s, Bc7Tables.LsTab2, tR[s], tG[s], tB[s], tA[s], xl, xh)) continue;
            if (pbitOpt)
            {
                sxl[0]=xl[0]/255f; sxl[1]=xl[1]/255f; sxl[2]=xl[2]/255f; sxl[3]=xl[3]/255f;
                sxh[0]=xh[0]/255f; sxh[1]=xh[1]/255f; sxh[2]=xh[2]/255f; sxh[3]=xh[3]/255f;
                DetermineUniquePbits(4, 5, sxl, sxh, minC, maxC, pbo);
                pbits[s*2]=pbo[0]; pbits[s*2+1]=pbo[1];
                lr[s]=(uint)minC[0]; lg[s]=(uint)minC[1]; lb[s]=(uint)minC[2]; la[s]=(uint)minC[3];
                hr[s]=(uint)maxC[0]; hg[s]=(uint)maxC[1]; hb[s]=(uint)maxC[2]; ha[s]=(uint)maxC[3];
            }
            else
            {
                int la8 = RoundPos(xl[3]), ha8 = RoundPos(xh[3]);
                uint lp = (uint)(la8 >= 129 ? 1 : 0), hp = (uint)(ha8 >= 129 ? 1 : 0);
                pbits[s*2] = lp; pbits[s*2+1] = hp;
                lr[s]=(uint)To5(RoundPos(xl[0]),lp); lg[s]=(uint)To5(RoundPos(xl[1]),lp); lb[s]=(uint)To5(RoundPos(xl[2]),lp); la[s]=(uint)To5(la8,lp);
                hr[s]=(uint)To5(RoundPos(xh[0]),hp); hg[s]=(uint)To5(RoundPos(xh[1]),hp); hb[s]=(uint)To5(RoundPos(xh[2]),hp); ha[s]=(uint)To5(ha8,hp);
            }
        }
        EvalWeightsMode7(px, w, lr, lg, lb, la, hr, hg, hb, ha, pbits, patBits);
        for (int s = 0; s < 2; s++) {
            eLoR[s] = From5((int)lr[s], pbits[s * 2]); eLoG[s] = From5((int)lg[s], pbits[s * 2]); eLoB[s] = From5((int)lb[s], pbits[s * 2]); eLoA[s] = From5((int)la[s], pbits[s * 2]);
            eHiR[s] = From5((int)hr[s], pbits[s * 2 + 1]); eHiG[s] = From5((int)hg[s], pbits[s * 2 + 1]); eHiB[s] = From5((int)hb[s], pbits[s * 2 + 1]); eHiA[s] = From5((int)ha[s], pbits[s * 2 + 1]); }
        if (wantSse) { long sx = SseMultiSubsetRgba(eLoR, eLoG, eLoB, eLoA, eHiR, eHiG, eHiB, eHiA, Bc7Tables.Partition2, bestPat * 16, Bc7Tables.Weights2, w, px);
          trueSse = sx > uint.MaxValue ? uint.MaxValue : (uint)sx; }
        EncodeMode7(block, (uint)bestPat, lr, lg, lb, la, hr, hg, hb, ha, pbits, w);
        return true;
    }

    // 2-subset 4D 2-bit index eval (eval_weights_mode7_rgba). from_5 with p-bit.
    private static void EvalWeightsMode7(scoped ReadOnlySpan<ColorRgba> px, scoped Span<int> w,
        scoped ReadOnlySpan<uint> blr, scoped ReadOnlySpan<uint> blg, scoped ReadOnlySpan<uint> blb, scoped ReadOnlySpan<uint> bla,
        scoped ReadOnlySpan<uint> bhr, scoped ReadOnlySpan<uint> bhg, scoped ReadOnlySpan<uint> bhb, scoped ReadOnlySpan<uint> bha,
        scoped ReadOnlySpan<uint> pbits, int subsetBitmask)
    {
        Span<int> dr = stackalloc int[2]; Span<int> dg = stackalloc int[2]; Span<int> db = stackalloc int[2]; Span<int> da = stackalloc int[2];
        Span<float> f = stackalloc float[2]; Span<int> sofs = stackalloc int[2];
        Span<int> lr = stackalloc int[2]; Span<int> lg = stackalloc int[2]; Span<int> lb = stackalloc int[2]; Span<int> la = stackalloc int[2];
        for (int s = 0; s < 2; s++)
        {
            uint lp = pbits[s*2], hp = pbits[s*2+1];
            lr[s]=From5((int)blr[s],lp); lg[s]=From5((int)blg[s],lp); lb[s]=From5((int)blb[s],lp); la[s]=From5((int)bla[s],lp);
            int hr=From5((int)bhr[s],hp), hg=From5((int)bhg[s],hp), hb=From5((int)bhb[s],hp), ha=From5((int)bha[s],hp);
            dr[s]=hr-lr[s]; dg[s]=hg-lg[s]; db[s]=hb-lb[s]; da[s]=ha-la[s];
            f[s]=3.0f/(dr[s]*dr[s]+dg[s]*dg[s]+db[s]*db[s]+da[s]*da[s]+0.00000125f);
            sofs[s]=lr[s]*dr[s]+lg[s]*dg[s]+lb[s]*db[s]+la[s]*da[s];
        }
        EvalSelRgba2(px, w, dr, dg, db, da, sofs, f, subsetBitmask, 3);
    }

    // BC7 mode-7 block writer (encode_mode7_rgba_block).
    private static void EncodeMode7(scoped Span<byte> block, uint partId,
        scoped Span<uint> lr, scoped Span<uint> lg, scoped Span<uint> lb, scoped Span<uint> la,
        scoped Span<uint> hr, scoped Span<uint> hg, scoped Span<uint> hb, scoped Span<uint> ha,
        scoped Span<uint> p, scoped ReadOnlySpan<int> weights)
    {
        int anchor = Bc7Tables.AnchorIndexSecondSubset[(int)partId];
        Span<uint> winv = stackalloc uint[2];
        if ((weights[0] & 2) != 0)
        {
            (lr[0],hr[0])=(hr[0],lr[0]); (lg[0],hg[0])=(hg[0],lg[0]); (lb[0],hb[0])=(hb[0],lb[0]); (la[0],ha[0])=(ha[0],la[0]);
            (p[0],p[1])=(p[1],p[0]); winv[0]=3;
        }
        if ((weights[anchor] & 2) != 0)
        {
            (lr[1],hr[1])=(hr[1],lr[1]); (lg[1],hg[1])=(hg[1],lg[1]); (lb[1],hb[1])=(hb[1],lb[1]); (la[1],ha[1])=(ha[1],la[1]);
            (p[2],p[3])=(p[3],p[2]); winv[1]=3;
        }

        ulong x = 0x80UL | ((ulong)partId << 8)
            | ((ulong)lr[0] << 14) | ((ulong)hr[0] << 19) | ((ulong)lr[1] << 24) | ((ulong)hr[1] << 29)
            | ((ulong)lg[0] << 34) | ((ulong)hg[0] << 39) | ((ulong)lg[1] << 44) | ((ulong)hg[1] << 49)
            | ((ulong)lb[0] << 54) | ((ulong)hb[0] << 59);
        block[0]=(byte)x; block[1]=(byte)(x>>8); block[2]=(byte)(x>>16); block[3]=(byte)(x>>24);
        block[4]=(byte)(x>>32); block[5]=(byte)(x>>40); block[6]=(byte)(x>>48); block[7]=(byte)(x>>56);

        ulong y = lb[1] | ((ulong)hb[1] << 5)
            | ((ulong)la[0] << 10) | ((ulong)ha[0] << 15) | ((ulong)la[1] << 20) | ((ulong)ha[1] << 25)
            | ((ulong)p[0] << 30) | ((ulong)p[1] << 31) | ((ulong)p[2] << 32) | ((ulong)p[3] << 33);
        int ofs = 34;
        for (int i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Partition2[(int)partId * 16 + i];
            ulong wv = (uint)(weights[i] ^ (int)winv[s]);
            y |= wv << ofs;
            ofs += 2 - ((i == 0 || i == anchor) ? 1 : 0);
        }
        block[8]=(byte)y; block[9]=(byte)(y>>8); block[10]=(byte)(y>>16); block[11]=(byte)(y>>24);
        block[12]=(byte)(y>>32); block[13]=(byte)(y>>40); block[14]=(byte)(y>>48); block[15]=(byte)(y>>56);
    }
}
