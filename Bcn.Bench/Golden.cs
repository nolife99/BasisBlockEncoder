// Golden.cs — cross-platform byte-identity gate for the MANAGED encoder.
//
// The managed encoder is deterministic and every SIMD tier (scalar / V128 / V256 / V512, x86 + ARM NEON) is
// meant to emit identical bytes. This encodes a fixed, architecture-independent corpus with every format's
// managed encoder and records a SHA-256 per case. Running it on x64 and on arm64 must produce identical
// digests — that is the only way to validate the ARM/NEON paths without the native reference (which is x64-
// only for some platforms). The corpus is restricted to gate-safe inputs: integer/LCG-generated synthetic
// surfaces and integer-decoded artworks (no transcendental functions in the *input*, so the pixels are
// bit-for-bit identical on every platform). The SkySun synthetic (Exp/Sqrt) is excluded.
//
//   dotnet run -c Release -- golden-write golden.bin     # write digests for this architecture
//   dotnet run -c Release -- golden-check golden.bin     # re-encode and assert reproduction of golden.bin
//
// In CI both architectures write their file in parallel and a final job asserts the two files are identical.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Bcn;            // BlockImage
using Bcn.Ldr;        // Bc1Quality
using MHdr = Bcn.Hdr; // Bc6hImage, Bc6hQuality

namespace Bcn.Bench;

internal static class Golden
{
    const uint Magic = 0x4E_6E_63_42; // 'Bcn N'
    const int Version = 1;
    static readonly string[] LdrFormats = { "Bc1", "Bc2", "Bc3", "Bc4", "Bc5", "Bc7" };

    static int Bpb(string fmt) => fmt is "Bc1" or "Bc4" ? 8 : 16;

    static byte[] EncodeLdr(string fmt, LdrImage im)
    {
        var dst = new byte[(long)im.Blocks * Bpb(fmt)];
        switch (fmt)
        {
            case "Bc1": BlockImage.EncodeBc1(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc1Quality.HighQuality); break;
            case "Bc2": BlockImage.EncodeBc2(im.Rgba, im.W, im.H, im.StrideBytes, dst); break;
            case "Bc3": BlockImage.EncodeBc3(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc1Quality.HighQuality); break;
            case "Bc4": BlockImage.EncodeBc4(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0); break;
            case "Bc5": BlockImage.EncodeBc5(im.Rgba, im.W, im.H, im.StrideBytes, dst, 0, 1); break;
            case "Bc7": BlockImage.EncodeBc7(im.Rgba, im.W, im.H, im.StrideBytes, dst, Bc7Flags.Default); break;
        }
        return dst;
    }

    static byte[] EncodeHdr(HdrImage im)
    {
        var dst = new byte[(long)im.Blocks * 16];
        MHdr.Bc6hImage.Encode(im.Rgb, im.W, im.H, im.StrideUshorts, dst, MHdr.Bc6hQuality.Default);
        return dst;
    }

    // (case-id, sha256-hex) for every gate-safe image x format, in a deterministic order.
    static IEnumerable<(string id, string sha)> Cases()
    {
        foreach (var im in Images.LdrCorpus())
        {
            if (!im.GateSafe) continue;
            foreach (var fmt in LdrFormats)
                yield return ($"{im.Name}/{fmt}", Sha(EncodeLdr(fmt, im)));
        }
        foreach (var im in Images.HdrCorpus())
        {
            if (!im.GateSafe) continue;
            yield return ($"{im.Name}/Bc6h", Sha(EncodeHdr(im)));
        }
    }

    static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static int Write(string path)
    {
        var cases = new List<(string id, string sha)>(Cases());
        using (var w = new StreamWriter(File.Create(path)))
        {
            w.WriteLine($"# {Magic:X8} v{Version} cases={cases.Count}");
            foreach (var (id, sha) in cases) w.WriteLine($"{id}\t{sha}");
        }
        Console.WriteLine($"Golden written: {cases.Count} cases -> {path}");
        Console.WriteLine($"{"case",-32} sha256");
        Console.WriteLine(new string('-', 48));
        foreach (var (id, sha) in cases) Console.WriteLine($"{id,-32} {sha[..16]}...");
        Console.WriteLine("\nIn CI, the x64 and arm64 jobs each write this file; a follow-up job asserts the two are identical.");
        return 0;
    }

    public static int Check(string path)
    {
        if (!File.Exists(path)) { Console.Error.WriteLine($"golden file not found: {path}"); return 2; }
        var expected = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var p = line.Split('\t');
            if (p.Length == 2) expected[p[0]] = p[1];
        }
        int mismatch = 0, checked_ = 0;
        foreach (var (id, sha) in Cases())
        {
            checked_++;
            if (!expected.TryGetValue(id, out var want)) { Console.Error.WriteLine($"  missing in golden: {id}"); mismatch++; }
            else if (want != sha) { Console.Error.WriteLine($"  MISMATCH {id}: golden {want[..12]}.. got {sha[..12]}.."); mismatch++; }
        }
        Console.WriteLine(mismatch == 0
            ? $"golden-check PASS: {checked_} cases reproduce {path} bit-for-bit."
            : $"golden-check FAIL: {mismatch}/{checked_} cases differ.");
        return mismatch == 0 ? 0 : 1;
    }
}
