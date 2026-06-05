namespace Bcn.Bench;

internal static class Verify
{
    const double ToleranceDb = 0.75;
    static readonly string[] LdrFormats = { "Bc1", "Bc2", "Bc3", "Bc4", "Bc5", "Bc7" };

    public static int Run()
    {
        Console.WriteLine("Quality parity — managed (Bcn) vs native (BasisBlockEncoder). Whole images; PSNR decoded by the managed decoder.\n");
        Console.WriteLine($"{"artwork",-22} {"format",-6} {"bit-ident",10} {"PSNR mgd",10} {"PSNR nat",10} {"dPSNR",8}");
        Console.WriteLine(new string('-', 74));

        bool fail = false;
        foreach (var art in Images.LdrNames)
            foreach (var fmt in LdrFormats)
                fail |= Print(fmt, art);

        foreach (var art in Images.HdrNames)
            fail |= Print("Bc6h", art);

        Console.WriteLine("\nbit-ident = % of blocks whose bytes match native exactly (not required; the bar is PSNR parity).");
        Console.WriteLine("dPSNR = managed - native (>= 0 means managed matches or beats native on that case).");
        Console.WriteLine(fail
            ? $"\nFAIL: at least one comparable format trailed native by > {ToleranceDb} dB."
            : $"\nPASS: every comparable format is within {ToleranceDb} dB of native (or better).");
        return fail ? 1 : 0;
    }

    static bool Print(string fmt, string artwork)
    {
        var r = Quality.Get(fmt, artwork);
        string bit = r.HasNative ? $"{r.BitIdentPct,8:F1}%" : "     n/a";
        string nat = r.HasNative ? Fmt(r.PsnrNative) : "   n/a";
        string d = r.HasNative ? Delta(r.PsnrManaged, r.PsnrNative) : "   n/a";
        Console.WriteLine($"{Trunc(artwork, 22),-22} {fmt,-6} {bit,10} {Fmt(r.PsnrManaged),10} {nat,10} {d,8}");
        return r.HasNative && !double.IsInfinity(r.PsnrManaged) && !double.IsInfinity(r.PsnrNative)
            && r.PsnrManaged < r.PsnrNative - ToleranceDb;
    }

    static string Fmt(double v) => double.IsInfinity(v) ? "inf" : v.ToString("F2");
    static string Delta(double m, double n)
        => double.IsInfinity(m) || double.IsInfinity(n) ? "n/a" : (m - n >= 0 ? "+" : "") + (m - n).ToString("F2");
    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";
}