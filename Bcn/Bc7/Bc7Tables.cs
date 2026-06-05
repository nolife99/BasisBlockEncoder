// Bc7Tables.cs — BC7 lookup tables (verbatim from Basis Universal, Apache-2.0)

namespace Bcn.Bc7;

using System;
using System.Runtime.Intrinsics;

static class Bc7Tables
{
    // Per-pixel subset (0/1) for each of the 64 two-subset partitions. g_bc7_partition2[64*16].
    public static readonly byte[] Partition2 =
    {
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0,
        0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0,
        0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1,
        1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 1,
        0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0,
        1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1,
        0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1
    };

    // Anchor index of the second subset per partition. g_bc7_table_anchor_index_second_subset[64].
    public static readonly byte[] AnchorIndexSecondSubset =
    {
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2, 15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15
    };

    // 2-bit interpolation weights. g_bc7_weights2.
    public static readonly int[] Weights2 =
    {
        0, 21, 43, 64
    };

    // 3-bit interpolation weights. g_bc7_weights3.
    public static readonly int[] Weights3 =
    {
        0, 9, 18, 27, 37, 46, 55, 64
    };

    // Per-partition subset bitmask (bit i = subset of pixel i), derived from Partition2.
    public static readonly ushort[] Part2Bitmasks = BuildBitmasks();

    // Per-pixel subset (0/1/2) for each of the 64 three-subset partitions. g_bc7_partition3[64*16].
    public static readonly byte[] Partition3 =
    {
        0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2, 0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1, 0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1, 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
        1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1,
        0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 1, 0, 0,
        0, 1, 2, 2, 2, 1, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 0, 2, 2, 1, 1, 0, 2,
        1, 1, 0, 2, 0, 0, 2, 2, 0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1,
        2, 0, 0, 1, 2, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1,
        2, 1, 2, 1, 0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1, 0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1,
        1, 1, 0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0
    };

    // Anchor index of subset 1 (third_subset_1) and subset 2 (third_subset_2) per 3-subset partition.
    public static readonly byte[] AnchorThird1 =
    {
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15, 8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3
    };

    public static readonly byte[] AnchorThird2 =
    {
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8, 15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8
    };

    // Packed 3-subset masks: low 16 bits = subset-0 pixels, high 16 bits = subset-1 pixels. g_part3_bitmasks.
    public static readonly uint[] Part3Bitmasks = BuildPart3();

    // Mode-6 (4-bit) interpolation weights, g_bc7_weights4.
    public static readonly int[] Weights4 =
    {
        0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64
    };

    public static readonly EndpointPair[] Mode5OptimalEndpoints = BuildMode5Optimal();

    // Least-squares selector-weight tables: per selector w, (w^2, (1-w)w, (1-w)^2, w). g_bc7_Nbit_ls_tab.
    public static readonly float[] LsTab2 = BuildLs(Weights2);
    public static readonly float[] LsTab3 = BuildLs(Weights3);
    public static readonly float[] LsTab4 = BuildLs(Weights4);
    internal static readonly LsCols LsCols2 = new(LsTab2);
    internal static readonly LsCols LsCols3 = new(LsTab3);
    internal static readonly LsCols LsCols4 = new(LsTab4);

    static ushort[] BuildBitmasks()
    {
        var m = new ushort[64];
        for (var p = 0; p < 64; p++)
        {
            ushort y = 0;
            for (var i = 0; i < 16; i++)
                if (Partition2[p * 16 + i] != 0)
                    y |= (ushort)(1 << i);

            m[p] = y;
        }

        return m;
    }

    static EndpointPair[] BuildMode5Optimal()
    {
        const int wt = 21; // g_bc7_weights2[BC7ENC_MODE_5_OPTIMAL_INDEX], BC7ENC_MODE_5_OPTIMAL_INDEX = 1
        var t = new EndpointPair[256];
        for (var c = 0; c < 256; c++)
        {
            var bestErr = int.MaxValue;
            byte bl = 0, bh = 0;
            for (var l = 0; l < 128; l++)
            {
                var low = l << 1 | l >> 6; // expand 7-bit -> 8-bit (from_7, no p-bit)
                for (var h = 0; h < 128; h++)
                {
                    var high = h << 1 | h >> 6;
                    var k = low * (64 - wt) + high * wt + 32 >> 6;
                    var e = (k - c) * (k - c);
                    if (e < bestErr)
                    {
                        bestErr = e;
                        bl = (byte)l;
                        bh = (byte)h;
                    }
                }
            }

            t[c] = new(bl, bh);
        }

        return t;
    }

    // tab lengths are 16 / 32 / 64 (4 / 8 / 16 rows). Single-subset LS only ever uses these three.
    internal static LsCols LsColsFor(int tabLen) => tabLen == 16 ? LsCols2 :
        tabLen == 32 ? LsCols3 : LsCols4;

    static float[] BuildLs(int[] wt)
    {
        var t = new float[wt.Length * 4];
        for (var i = 0; i < wt.Length; i++)
        {
            var w = wt[i] * (1f / 64f);
            t[i * 4 + 0] = w * w;
            t[i * 4 + 1] = (1f - w) * w;
            t[i * 4 + 2] = (1f - w) * (1f - w);
            t[i * 4 + 3] = w;
        }

        return t;
    }

    static uint[] BuildPart3()
    {
        var m = new uint[64];
        for (var p = 0; p < 64; p++)
        {
            uint s0 = 0, s1 = 0;
            for (var i = 0; i < 16; i++)
            {
                int s = Partition3[p * 16 + i];
                if (s == 0) s0 |= (uint)(1 << i);
                else if (s == 1) s1 |= (uint)(1 << i);
            }

            m[p] = s0 | s1 << 16;
        }

        return m;
    }

    // Per-8-bit-value optimal mode-5 solid endpoints (7-bit, no p-bit) at color index 1 (g_bc7_weights2[1]=21),
    // brute-forced exactly like basis_universal's g_bc7_mode_5_optimal_endpoints. Used by pack_mode5_solid.
    public readonly struct EndpointPair
    {
        public readonly byte Lo, Hi;

        public EndpointPair(byte lo, byte hi)
        {
            Lo = lo;
            Hi = hi;
        }
    }

    // Column-major form of the LS tables for the AVX2 VPERMPS gather in LsAccum3D/4D. Each of the 4 columns
    // (z00, z10, z11, ww) is laid into Vector256 lanes indexed by the weight value, so one PermuteVar8x32
    // gathers all 8 lanes by w[]. 16-row tables (4-bit indices) also keep the rows-8..15 half + blend on bit 3.
    // Built once at type init (Vector256.Create is plain data construction — no AVX needed to build it).
    internal readonly struct LsCols
    {
        public readonly Vector256<float> C0, C1, C2, Cw; // rows 0..7
        public readonly Vector256<float> C0h, C1h, C2h, Cwh; // rows 8..15 (16-row tables; padded otherwise)
#if NET8_0_OR_GREATER
        public readonly Vector512<float> V0, V1, V2, Vw; // full 16-row columns for the AVX-512 16-wide permute
#endif
        public readonly bool Wide;

        internal LsCols(float[] t)
        {
            var rows = t.Length / 4;
            Wide = rows > 8;
            Span<float> a0 = stackalloc float[8], a1 = stackalloc float[8], a2 = stackalloc float[8], aw = stackalloc float[8];
            Span<float> b0 = stackalloc float[8], b1 = stackalloc float[8], b2 = stackalloc float[8], bw = stackalloc float[8];
            for (var v = 0; v < 8; v++)
            {
                var r = v < rows ? v : rows - 1;
                a0[v] = t[r * 4];
                a1[v] = t[r * 4 + 1];
                a2[v] = t[r * 4 + 2];
                aw[v] = t[r * 4 + 3];
            }

            for (var v = 0; v < 8; v++)
            {
                var r = 8 + v < rows ? 8 + v : rows - 1;
                b0[v] = t[r * 4];
                b1[v] = t[r * 4 + 1];
                b2[v] = t[r * 4 + 2];
                bw[v] = t[r * 4 + 3];
            }

            C0 = Vector256.Create(a0);
            C1 = Vector256.Create(a1);
            C2 = Vector256.Create(a2);
            Cw = Vector256.Create(aw);
            C0h = Vector256.Create(b0);
            C1h = Vector256.Create(b1);
            C2h = Vector256.Create(b2);
            Cwh = Vector256.Create(bw);
#if NET8_0_OR_GREATER
            // 16-lane columns = [rows 0..7 | rows 8..15]; a single PermuteVar16x32 gathers all 16 (no blend needed).
            V0 = Vector512.Create(C0, C0h);
            V1 = Vector512.Create(C1, C1h);
            V2 = Vector512.Create(C2, C2h);
            Vw = Vector512.Create(Cw, Cwh);
#endif
        }
    }
}