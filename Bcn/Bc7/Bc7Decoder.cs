// Bc7Decoder.cs — internal, zero-allocation BC7 block decoder + actual-SSE helper. Used only by the
// partially-analytical (HighQuality) path to compute true reconstruction error for mode selection
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bcn.Bc7;

internal static class Bc7Decoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From7(int v, int p) => (v << 1) | p;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From6(int v, int p){ uint t=((uint)v<<1)|(uint)p; t=(t<<1)|(t>>6); return (int)t; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From5(int v){ uint t=(uint)v; t=(t<<3)|(t>>2); return (int)t; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From4(int v, int p){ uint t=((uint)v<<1)|(uint)p; t=(t<<3)|(t>>2); return (int)t; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From7(int v){ uint t=(uint)v; return (int)((t<<1)|(t>>6)); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From6(int v){ uint t=(uint)v; return (int)((t<<2)|(t>>4)); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int From5(int v,int p){ uint t=((uint)v<<1)|(uint)p; t=(t<<2)|(t>>4); return (int)t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Lerp(int a, int b, int w) => (a * (64 - w) + b * w + 32) >> 6;

    static void Unrotate(scoped Span<ColorRgba> o, int rot)
    {
        if (rot == 0) return;
        for (int i = 0; i < 16; i++)
        {
            var c = o[i]; byte a = c.A;
            if (rot == 1) { byte t = c.R; c.R = a; c.A = t; }
            else if (rot == 2) { byte t = c.G; c.G = a; c.A = t; }
            else { byte t = c.B; c.B = a; c.A = t; }
            o[i] = c;
        }
    }

    static int Bits(ulong lo, ulong hi, int start, int len)
    {
        ulong v;
        if (start < 64) { v = lo >> start; if (start + len > 64) v |= hi << (64 - start); }
        else v = hi >> (start - 64);
        ulong mask = (len == 64) ? ~0UL : ((1UL << len) - 1);
        return (int)(v & mask);
    }
    static (ulong, ulong) Load(scoped ReadOnlySpan<byte> blk)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 8; i++) lo |= (ulong)blk[i] << (8 * i);
        for (int i = 0; i < 8; i++) hi |= (ulong)blk[8 + i] << (8 * i);
        return (lo, hi);
    }

    static void DecodeMode6(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk);
        int lr = Bits(lo,hi,7,7), hr = Bits(lo,hi,14,7), lg = Bits(lo,hi,21,7), hg = Bits(lo,hi,28,7);
        int lb = Bits(lo,hi,35,7), hb = Bits(lo,hi,42,7), la = Bits(lo,hi,49,7), ha = Bits(lo,hi,56,7);
        int p0 = Bits(lo,hi,63,1), p1 = Bits(lo,hi,64,1);
        int lr8=From7(lr,p0), lg8=From7(lg,p0), lb8=From7(lb,p0), la8=From7(la,p0);
        int hr8=From7(hr,p1), hg8=From7(hg,p1), hb8=From7(hb,p1), ha8=From7(ha,p1);
        int bit = 65;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights4);
        for (int i = 0; i < 16; i++)
        {
            int len = (i == 0) ? 3 : 4; int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr8,hr8,w),(byte)Lerp(lg8,hg8,w),(byte)Lerp(lb8,hb8,w),(byte)Lerp(la8,ha8,w));
        }
    }
    static void DecodeMode3(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int part = Bits(lo,hi,4,6);
        int lr0=Bits(lo,hi,10,7),hr0=Bits(lo,hi,17,7),lr1=Bits(lo,hi,24,7),hr1=Bits(lo,hi,31,7);
        int lg0=Bits(lo,hi,38,7),hg0=Bits(lo,hi,45,7),lg1=Bits(lo,hi,52,7),hg1=Bits(lo,hi,59,7);
        int lb0=Bits(lo,hi,66,7),hb0=Bits(lo,hi,73,7),lb1=Bits(lo,hi,80,7),hb1=Bits(lo,hi,87,7);
        int p0=Bits(lo,hi,94,1),p1=Bits(lo,hi,95,1),p2=Bits(lo,hi,96,1),p3=Bits(lo,hi,97,1);
        Span<int> lr = stackalloc int[2]; lr[0]=From7(lr0,p0); lr[1]=From7(lr1,p2);
        Span<int> hr = stackalloc int[2]; hr[0]=From7(hr0,p1); hr[1]=From7(hr1,p3);
        Span<int> lg = stackalloc int[2]; lg[0]=From7(lg0,p0); lg[1]=From7(lg1,p2);
        Span<int> hg = stackalloc int[2]; hg[0]=From7(hg0,p1); hg[1]=From7(hg1,p3);
        Span<int> lb = stackalloc int[2]; lb[0]=From7(lb0,p0); lb[1]=From7(lb1,p2);
        Span<int> hb = stackalloc int[2]; hb[0]=From7(hb0,p1); hb[1]=From7(hb1,p3);
        int anchor = Bc7Tables.AnchorIndexSecondSubset[part]; int bit = 98;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref byte P = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition2);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights2);
        for (int i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref P, part*16+i); int len = (i==0||i==anchor)?1:2;
            int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr[s],hr[s],w),(byte)Lerp(lg[s],hg[s],w),(byte)Lerp(lb[s],hb[s],w),255);
        }
    }
    static void DecodeMode1(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int part = Bits(lo,hi,2,6);
        int lr0=Bits(lo,hi,8,6),hr0=Bits(lo,hi,14,6),lr1=Bits(lo,hi,20,6),hr1=Bits(lo,hi,26,6);
        int lg0=Bits(lo,hi,32,6),hg0=Bits(lo,hi,38,6),lg1=Bits(lo,hi,44,6),hg1=Bits(lo,hi,50,6);
        int lb0=Bits(lo,hi,56,6),hb0=Bits(lo,hi,62,6),lb1=Bits(lo,hi,68,6),hb1=Bits(lo,hi,74,6);
        int p0=Bits(lo,hi,80,1),p1=Bits(lo,hi,81,1);
        Span<int> lr = stackalloc int[2]; lr[0]=From6(lr0,p0); lr[1]=From6(lr1,p1);
        Span<int> hr = stackalloc int[2]; hr[0]=From6(hr0,p0); hr[1]=From6(hr1,p1);
        Span<int> lg = stackalloc int[2]; lg[0]=From6(lg0,p0); lg[1]=From6(lg1,p1);
        Span<int> hg = stackalloc int[2]; hg[0]=From6(hg0,p0); hg[1]=From6(hg1,p1);
        Span<int> lb = stackalloc int[2]; lb[0]=From6(lb0,p0); lb[1]=From6(lb1,p1);
        Span<int> hb = stackalloc int[2]; hb[0]=From6(hb0,p0); hb[1]=From6(hb1,p1);
        int anchor = Bc7Tables.AnchorIndexSecondSubset[part]; int bit = 82;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref byte P = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition2);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights3);
        for (int i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref P, part*16+i); int len = (i==0||i==anchor)?2:3;
            int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr[s],hr[s],w),(byte)Lerp(lg[s],hg[s],w),(byte)Lerp(lb[s],hb[s],w),255);
        }
    }
    static void DecodeMode0(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int part = Bits(lo,hi,1,4);
        int lr0=Bits(lo,hi,5,4),hr0=Bits(lo,hi,9,4),lr1=Bits(lo,hi,13,4),hr1=Bits(lo,hi,17,4),lr2=Bits(lo,hi,21,4),hr2=Bits(lo,hi,25,4);
        int lg0=Bits(lo,hi,29,4),hg0=Bits(lo,hi,33,4),lg1=Bits(lo,hi,37,4),hg1=Bits(lo,hi,41,4),lg2=Bits(lo,hi,45,4),hg2=Bits(lo,hi,49,4);
        int lb0=Bits(lo,hi,53,4),hb0=Bits(lo,hi,57,4),lb1=Bits(lo,hi,61,4),hb1=Bits(lo,hi,65,4),lb2=Bits(lo,hi,69,4),hb2=Bits(lo,hi,73,4);
        int p0=Bits(lo,hi,77,1),p1=Bits(lo,hi,78,1),p2=Bits(lo,hi,79,1),p3=Bits(lo,hi,80,1),p4=Bits(lo,hi,81,1),p5=Bits(lo,hi,82,1);
        Span<int> lr = stackalloc int[3]; lr[0]=From4(lr0,p0); lr[1]=From4(lr1,p2); lr[2]=From4(lr2,p4);
        Span<int> hr = stackalloc int[3]; hr[0]=From4(hr0,p1); hr[1]=From4(hr1,p3); hr[2]=From4(hr2,p5);
        Span<int> lg = stackalloc int[3]; lg[0]=From4(lg0,p0); lg[1]=From4(lg1,p2); lg[2]=From4(lg2,p4);
        Span<int> hg = stackalloc int[3]; hg[0]=From4(hg0,p1); hg[1]=From4(hg1,p3); hg[2]=From4(hg2,p5);
        Span<int> lb = stackalloc int[3]; lb[0]=From4(lb0,p0); lb[1]=From4(lb1,p2); lb[2]=From4(lb2,p4);
        Span<int> hb = stackalloc int[3]; hb[0]=From4(hb0,p1); hb[1]=From4(hb1,p3); hb[2]=From4(hb2,p5);
        int a0 = Bc7Tables.AnchorThird1[part], a1 = Bc7Tables.AnchorThird2[part]; int bit = 83;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref byte P = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition3);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights3);
        for (int i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref P, part*16+i); int len = (i==0||i==a0||i==a1)?2:3;
            int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr[s],hr[s],w),(byte)Lerp(lg[s],hg[s],w),(byte)Lerp(lb[s],hb[s],w),255);
        }
    }
    static void DecodeMode2(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int part = Bits(lo,hi,3,6);
        int lr0=Bits(lo,hi,9,5),hr0=Bits(lo,hi,14,5),lr1=Bits(lo,hi,19,5),hr1=Bits(lo,hi,24,5),lr2=Bits(lo,hi,29,5),hr2=Bits(lo,hi,34,5);
        int lg0=Bits(lo,hi,39,5),hg0=Bits(lo,hi,44,5),lg1=Bits(lo,hi,49,5),hg1=Bits(lo,hi,54,5),lg2=Bits(lo,hi,59,5);
        int hg2=Bits(lo,hi,64,5),lb0=Bits(lo,hi,69,5),hb0=Bits(lo,hi,74,5),lb1=Bits(lo,hi,79,5),hb1=Bits(lo,hi,84,5),lb2=Bits(lo,hi,89,5),hb2=Bits(lo,hi,94,5);
        Span<int> lr = stackalloc int[3]; lr[0]=From5(lr0); lr[1]=From5(lr1); lr[2]=From5(lr2);
        Span<int> hr = stackalloc int[3]; hr[0]=From5(hr0); hr[1]=From5(hr1); hr[2]=From5(hr2);
        Span<int> lg = stackalloc int[3]; lg[0]=From5(lg0); lg[1]=From5(lg1); lg[2]=From5(lg2);
        Span<int> hg = stackalloc int[3]; hg[0]=From5(hg0); hg[1]=From5(hg1); hg[2]=From5(hg2);
        Span<int> lb = stackalloc int[3]; lb[0]=From5(lb0); lb[1]=From5(lb1); lb[2]=From5(lb2);
        Span<int> hb = stackalloc int[3]; hb[0]=From5(hb0); hb[1]=From5(hb1); hb[2]=From5(hb2);
        int a0 = Bc7Tables.AnchorThird1[part], a1 = Bc7Tables.AnchorThird2[part]; int bit = 99;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref byte P = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition3);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights2);
        for (int i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref P, part*16+i); int len = (i==0||i==a0||i==a1)?1:2;
            int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr[s],hr[s],w),(byte)Lerp(lg[s],hg[s],w),(byte)Lerp(lb[s],hb[s],w),255);
        }
    }
    static void DecodeMode5(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int rot = Bits(lo,hi,6,2);
        int lr=Bits(lo,hi,8,7),hr=Bits(lo,hi,15,7),lg=Bits(lo,hi,22,7),hg=Bits(lo,hi,29,7),lb=Bits(lo,hi,36,7),hb=Bits(lo,hi,43,7);
        int la=Bits(lo,hi,50,8),ha=Bits(lo,hi,58,8);
        int r0=From7(lr),g0=From7(lg),b0=From7(lb),r1=From7(hr),g1=From7(hg),b1=From7(hb);
        int cb = 66, ab = 97;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights2);
        for (int i = 0; i < 16; i++)
        {
            int cl=(i==0)?1:2; int wc=Unsafe.Add(ref W, Bits(lo,hi,cb,cl)); cb+=cl;
            int al=(i==0)?1:2; int wa=Unsafe.Add(ref W, Bits(lo,hi,ab,al)); ab+=al;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(r0,r1,wc),(byte)Lerp(g0,g1,wc),(byte)Lerp(b0,b1,wc),(byte)Lerp(la,ha,wa));
        }
        Unrotate(o, rot);
    }
    static void DecodeMode4(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int rot = Bits(lo,hi,5,2); int flag = Bits(lo,hi,7,1);
        int lr=Bits(lo,hi,8,5),hr=Bits(lo,hi,13,5),lg=Bits(lo,hi,18,5),hg=Bits(lo,hi,23,5),lb=Bits(lo,hi,28,5),hb=Bits(lo,hi,33,5);
        int la=Bits(lo,hi,38,6),ha=Bits(lo,hi,44,6);
        int r0=From5(lr),g0=From5(lg),b0=From5(lb),r1=From5(hr),g1=From5(hg),b1=From5(hb);
        int a0=From6(la),a1=From6(ha);
        Span<int> idx2 = stackalloc int[16]; Span<int> idx3 = stackalloc int[16]; int b2 = 50, b3 = 81;
        ref int I2 = ref MemoryMarshal.GetReference(idx2); ref int I3 = ref MemoryMarshal.GetReference(idx3);
        for (int i = 0; i < 16; i++) { int l=(i==0)?1:2; Unsafe.Add(ref I2,i)=Bits(lo,hi,b2,l); b2+=l; }
        for (int i = 0; i < 16; i++) { int l=(i==0)?2:3; Unsafe.Add(ref I3,i)=Bits(lo,hi,b3,l); b3+=l; }
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref int W2 = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights2);
        ref int W3 = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights3);
        for (int i = 0; i < 16; i++)
        {
            int wc = flag!=0 ? Unsafe.Add(ref W3, Unsafe.Add(ref I3,i)) : Unsafe.Add(ref W2, Unsafe.Add(ref I2,i));
            int wa = flag!=0 ? Unsafe.Add(ref W2, Unsafe.Add(ref I2,i)) : Unsafe.Add(ref W3, Unsafe.Add(ref I3,i));
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(r0,r1,wc),(byte)Lerp(g0,g1,wc),(byte)Lerp(b0,b1,wc),(byte)Lerp(a0,a1,wa));
        }
        Unrotate(o, rot);
    }
    static void DecodeMode7(scoped ReadOnlySpan<byte> blk, scoped Span<ColorRgba> o)
    {
        var (lo, hi) = Load(blk); int part = Bits(lo,hi,8,6);
        int lr0=Bits(lo,hi,14,5),hr0=Bits(lo,hi,19,5),lr1=Bits(lo,hi,24,5),hr1=Bits(lo,hi,29,5);
        int lg0=Bits(lo,hi,34,5),hg0=Bits(lo,hi,39,5),lg1=Bits(lo,hi,44,5),hg1=Bits(lo,hi,49,5);
        int lb0=Bits(lo,hi,54,5),hb0=Bits(lo,hi,59,5),lb1=Bits(lo,hi,64,5),hb1=Bits(lo,hi,69,5);
        int la0=Bits(lo,hi,74,5),ha0=Bits(lo,hi,79,5),la1=Bits(lo,hi,84,5),ha1=Bits(lo,hi,89,5);
        int p0=Bits(lo,hi,94,1),p1=Bits(lo,hi,95,1),p2=Bits(lo,hi,96,1),p3=Bits(lo,hi,97,1);
        Span<int> lr = stackalloc int[2]; lr[0]=From5(lr0,p0); lr[1]=From5(lr1,p2);
        Span<int> hr = stackalloc int[2]; hr[0]=From5(hr0,p1); hr[1]=From5(hr1,p3);
        Span<int> lg = stackalloc int[2]; lg[0]=From5(lg0,p0); lg[1]=From5(lg1,p2);
        Span<int> hg = stackalloc int[2]; hg[0]=From5(hg0,p1); hg[1]=From5(hg1,p3);
        Span<int> lb = stackalloc int[2]; lb[0]=From5(lb0,p0); lb[1]=From5(lb1,p2);
        Span<int> hb = stackalloc int[2]; hb[0]=From5(hb0,p1); hb[1]=From5(hb1,p3);
        Span<int> la = stackalloc int[2]; la[0]=From5(la0,p0); la[1]=From5(la1,p2);
        Span<int> ha = stackalloc int[2]; ha[0]=From5(ha0,p1); ha[1]=From5(ha1,p3);
        int anchor = Bc7Tables.AnchorIndexSecondSubset[part]; int bit = 98;
        ref ColorRgba op = ref MemoryMarshal.GetReference(o);
        ref byte P = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Partition2);
        ref int W = ref MemoryMarshal.GetArrayDataReference(Bc7Tables.Weights2);
        for (int i = 0; i < 16; i++)
        {
            int s = Unsafe.Add(ref P, part*16+i); int len = (i==0||i==anchor)?1:2;
            int w = Unsafe.Add(ref W, Bits(lo,hi,bit,len)); bit += len;
            Unsafe.Add(ref op, i) = new ColorRgba((byte)Lerp(lr[s],hr[s],w),(byte)Lerp(lg[s],hg[s],w),(byte)Lerp(lb[s],hb[s],w),(byte)Lerp(la[s],ha[s],w));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ModeOf(byte b0) => (b0&1)!=0?0:(b0&2)!=0?1:(b0&4)!=0?2:(b0&8)!=0?3:(b0&0x10)!=0?4:(b0&0x20)!=0?5:(b0&0x40)!=0?6:(b0&0x80)!=0?7:-1;

    static void DecodeBlock(scoped ReadOnlySpan<byte> b, scoped Span<ColorRgba> o)
    {
        switch (ModeOf(b[0]))
        {
            case 0: DecodeMode0(b, o); break;
            case 1: DecodeMode1(b, o); break;
            case 2: DecodeMode2(b, o); break;
            case 3: DecodeMode3(b, o); break;
            case 4: DecodeMode4(b, o); break;
            case 5: DecodeMode5(b, o); break;
            case 6: DecodeMode6(b, o); break;
            case 7: DecodeMode7(b, o); break;
            default: o.Clear(); break; // unreachable for valid blocks
        }
    }

    // Decode a finished BC7 block and return the true sum of squared errors vs the source pixels
    // (R,G,B,A — matching bc7f's bc7_sse, which always includes alpha). Reconstruction is identical to
    // hardware: Lerp(lo,hi,w) = lo + ((hi-lo)*w+32>>6), so these SSE values equal upstream's exactly.
    public static uint ActualSse(scoped ReadOnlySpan<byte> block, scoped ReadOnlySpan<ColorRgba> px)
    {
        Span<ColorRgba> dec = stackalloc ColorRgba[16];
        DecodeBlock(block, dec);
        long sse = Bc7Block.SsdRgba(px, dec);
        return sse > uint.MaxValue ? uint.MaxValue : (uint)sse;
    }
}
