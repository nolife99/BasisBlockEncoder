using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using MHdr = Bcn.Hdr; // Bc6hImage, Bc6hQuality
using NEnc = BasisBlockEncoder.BlockEncoder;
using NBc1Q = BasisBlockEncoder.Bc1Quality;
using NBc6hQ = BasisBlockEncoder.Bc6hQuality;
using NBc7F = BasisBlockEncoder.Bc7Flags;

namespace Bcn.Bench;

public abstract class LdrBench
{
    [ParamsSource(nameof(Artworks))] public string Artwork = "";
    public static IEnumerable<string> Artworks => Images.LdrNames;
    protected LdrImage Img;
    protected byte[] Dst = Array.Empty<byte>();
    protected bool NativeOk;
    protected abstract int Bpb { get; }

    [GlobalSetup]
    public void Setup()
    {
        try { NEnc.Initialize(); NativeOk = true; } catch { NativeOk = false; }
        Img = Images.LoadLdr(Artwork);
        Dst = new byte[(long)Img.Blocks * Bpb];
    }
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc1Benchmarks : LdrBench
{
    protected override int Bpb => 8;
    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (NativeOk) NEnc.EncodeBc1(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, NBc1Q.HighQuality); }
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc1(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, Ldr.Bc1Quality.HighQuality);
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc3Benchmarks : LdrBench
{
    protected override int Bpb => 16;
    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (NativeOk) NEnc.EncodeBc3(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, NBc1Q.HighQuality); }
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc3(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, Ldr.Bc1Quality.HighQuality);
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc4Benchmarks : LdrBench
{
    protected override int Bpb => 8;
    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (NativeOk) NEnc.EncodeBc4(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, 0); }
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc4(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, 0);
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc5Benchmarks : LdrBench
{
    protected override int Bpb => 16;
    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (NativeOk) NEnc.EncodeBc5(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, 0, 1); }
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc5(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, 0, 1);
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc7Benchmarks : LdrBench
{
    protected override int Bpb => 16;
    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (NativeOk) NEnc.EncodeBc7(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, NBc7F.Default); }
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc7(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst, Bc7Flags.Default);
}

// BC2 has no native counterpart in the reference package, so it is timed managed-only.
[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc2Benchmarks : LdrBench
{
    protected override int Bpb => 16;
    [Benchmark(Description = "managed")] public void Managed() => BlockImage.EncodeBc2(Img.Rgba, Img.W, Img.H, Img.StrideBytes, Dst);
}

[MemoryDiagnoser, HideColumns("Error", "StdDev", "Median", "RatioSD"), Config(typeof(QualityConfig))]
public class Bc6hBenchmarks
{
    [ParamsSource(nameof(Artworks))] public string Artwork = "";
    public static IEnumerable<string> Artworks => Images.HdrNames;
    HdrImage _im;
    byte[] _dst = Array.Empty<byte>();
    bool _nativeOk;

    [GlobalSetup]
    public void Setup()
    {
        try { NEnc.Initialize(); _nativeOk = true; } catch { _nativeOk = false; }
        _im = Images.LoadHdr(Artwork);
        _dst = new byte[(long)_im.Blocks * 16];
    }

    [Benchmark(Baseline = true, Description = "native")] public void Native() { if (_nativeOk) NEnc.EncodeBc6h(_im.Rgb, _im.W, _im.H, _im.StrideUshorts * sizeof(ushort), _dst, NBc6hQ.Default); }
    [Benchmark(Description = "managed")] public void Managed() => MHdr.Bc6hImage.Encode(_im.Rgb, _im.W, _im.H, _im.StrideUshorts, _dst, MHdr.Bc6hQuality.Default);
}

// ---- non-timed quality columns (PSNR + bit-identity vs native), wired to Quality ----

internal static class ColMap
{
    public static string Format(BenchmarkCase c) => c.Descriptor.Type.Name.Replace("Benchmarks", "");
    public static bool Managed(BenchmarkCase c) => c.Descriptor.WorkloadMethod.Name == "Managed";
    public static string Artwork(BenchmarkCase c) => (string)c.Parameters["Artwork"];
}

internal sealed class PsnrColumn : IColumn
{
    public string Id => nameof(PsnrColumn);
    public string ColumnName => "PSNR dB";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Metric;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "PSNR of this encoder's output vs the source, decoded by the managed decoder (SNR-style for BC6H).";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, null!);
    public string GetValue(Summary summary, BenchmarkCase c, SummaryStyle style)
    {
        var r = Quality.Get(ColMap.Format(c), ColMap.Artwork(c));
        bool managed = ColMap.Managed(c);
        if (!managed && !r.HasNative) return "n/a";
        double v = managed ? r.PsnrManaged : r.PsnrNative;
        return double.IsInfinity(v) ? "inf" : v.ToString("F2");
    }
}

internal sealed class ParityColumn : IColumn
{
    public string Id => nameof(ParityColumn);
    public string ColumnName => "vs Native";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Metric;
    public int PriorityInCategory => 1;
    public bool IsNumeric => false;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Managed vs native: % of blocks whose bytes are bit-identical. Native row is the baseline.";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, null!);
    public string GetValue(Summary summary, BenchmarkCase c, SummaryStyle style)
    {
        if (!ColMap.Managed(c)) return "baseline";
        var r = Quality.Get(ColMap.Format(c), ColMap.Artwork(c));
        return r.HasNative ? $"{r.BitIdentPct:F1}% id" : "n/a";
    }
}

internal sealed class QualityConfig : ManualConfig
{
    public QualityConfig()
    {
        AddColumn(new PsnrColumn());
        AddColumn(new ParityColumn());
    }
}
