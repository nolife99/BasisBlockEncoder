using StbImageSharp;

namespace Bcn.Bench;

/// <summary>Whole RGBA8 surface, row-major, <see cref="StrideBytes"/> == W*4.</summary>
public readonly struct LdrImage
{
    public readonly string Name;
    public readonly int W, H, StrideBytes;
    public readonly byte[] Rgba;
    public readonly bool GateSafe; // input is bit-identical across architectures (safe for the byte gate)
    public LdrImage(string name, int w, int h, byte[] rgba, bool gateSafe)
    { Name = name; W = w; H = h; StrideBytes = w * 4; Rgba = rgba; GateSafe = gateSafe; }
    public int Blocks => ((W + 3) / 4) * ((H + 3) / 4);
}

/// <summary>Whole RGB FP16 surface (3 ushort bit patterns per pixel), <see cref="StrideUshorts"/> == W*3.</summary>
public readonly struct HdrImage
{
    public readonly string Name;
    public readonly int W, H, StrideUshorts;
    public readonly ushort[] Rgb;
    public readonly bool GateSafe;
    public HdrImage(string name, int w, int h, ushort[] rgb, bool gateSafe)
    { Name = name; W = w; H = h; StrideUshorts = w * 3; Rgb = rgb; GateSafe = gateSafe; }
    public int Blocks => ((W + 3) / 4) * ((H + 3) / 4);
}

internal static class Images
{
    static readonly string[] LdrExts = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".psd" };
    const string HdrExt = ".hdr";
    public const string Env = "BCN_BENCH_ARTWORKS";

    static string? _dir;
    static string Dir => _dir ??= Find();
    public static string ResolvedArtworkDir => Dir;

    static string Find()
    {
        var env = Environment.GetEnvironmentVariable(Env);
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++)
        {
            var cand = Path.Combine(d.FullName, "artworks");
            if (Directory.Exists(cand)) return cand;
            d = d.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artworks");
    }

    static List<string> FilesWith(string[] exts) => Directory.Exists(Dir)
        ? Directory.GetFiles(Dir).Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(x => x).ToList()
        : new List<string>();

    // ---- BenchmarkDotNet parameter sources (artwork identifiers) ----
    public static IEnumerable<string> LdrNames
    {
        get { var f = FilesWith(LdrExts); return f.Count > 0 ? f : SyntheticLdrNames; }
    }
    public static IEnumerable<string> HdrNames
    {
        get { var f = FilesWith(new[] { HdrExt }); return f.Count > 0 ? f : SyntheticHdrNames; }
    }

    static readonly string[] SyntheticLdrNames = { "synthetic:gradient", "synthetic:noise", "synthetic:edges", "synthetic:mixed" };
    static readonly string[] SyntheticHdrNames = { "synthetic:sky", "synthetic:noisy", "synthetic:sun" };

    // ---- load by name (cached) ----
    static readonly Dictionary<string, LdrImage> _ldrCache = new();
    static readonly Dictionary<string, HdrImage> _hdrCache = new();
    static readonly object _gate = new();

    public static LdrImage LoadLdr(string name)
    {
        lock (_gate)
        {
            if (_ldrCache.TryGetValue(name, out var c)) return c;
            LdrImage img = name.StartsWith("synthetic:", StringComparison.Ordinal) ? SynthLdr(name) : LoadLdrFile(name);
            _ldrCache[name] = img;
            return img;
        }
    }

    public static HdrImage LoadHdr(string name)
    {
        lock (_gate)
        {
            if (_hdrCache.TryGetValue(name, out var c)) return c;
            HdrImage img = name.StartsWith("synthetic:", StringComparison.Ordinal) ? SynthHdr(name) : LoadHdrFile(name);
            _hdrCache[name] = img;
            return img;
        }
    }

    public static IReadOnlyList<LdrImage> LdrCorpus() => LdrNames.Select(LoadLdr).ToList();
    public static IReadOnlyList<HdrImage> HdrCorpus() => HdrNames.Select(LoadHdr).ToList();

    static LdrImage LoadLdrFile(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, name));
        var img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha); // pure-managed integer decode
        int w = img.Width & ~3, h = img.Height & ~3;
        if (w < 4 || h < 4) throw new InvalidDataException($"'{name}' is smaller than 4x4 after cropping.");
        if (w == img.Width && h == img.Height)
            return new LdrImage(name, w, h, img.Data, gateSafe: true);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(img.Data, y * img.Width * 4, px, y * w * 4, w * 4); // crop right/bottom to a multiple of 4
        return new LdrImage(name, w, h, px, gateSafe: true);
    }

    static HdrImage LoadHdrFile(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, name));
        var img = ImageResultFloat.FromMemory(bytes, ColorComponents.RedGreenBlue); // Radiance HDR -> float RGB
        int w = img.Width & ~3, h = img.Height & ~3;
        if (w < 4 || h < 4) throw new InvalidDataException($"'{name}' is smaller than 4x4 after cropping.");
        var rgb = new ushort[w * h * 3];
        var src = img.Data; // float RGB, row-major, img.Width*3 stride
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w * 3; x++)
                rgb[y * w * 3 + x] = BitConverter.HalfToUInt16Bits((Half)src[y * img.Width * 3 + x]);
        return new HdrImage(name, w, h, rgb, gateSafe: true); // RGBE decode is deterministic across architectures
    }

    // ---- procedural fallbacks (whole images) ----
    private struct Lcg
    {
        private uint _s;
        public Lcg(uint seed) { _s = seed == 0 ? 1u : seed; }
        public uint Next() { _s = _s * 1664525u + 1013904223u; return _s; }
        public byte Byte() => (byte)(Next() >> 24);
        public float Unit() => (Next() >> 8) * (1.0f / 16777216.0f);
    }

    const int SynthSize = 256; // whole synthetic surface (multiple of 4)

    static LdrImage SynthLdr(string name) => name switch
    {
        "synthetic:gradient" => Gradient(),
        "synthetic:noise" => Noise(),
        "synthetic:edges" => Edges(),
        _ => Mixed(),
    };

    static LdrImage Gradient()
    {
        int w = SynthSize, h = SynthSize; var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)(x * 255 / (w - 1)); px[o + 1] = (byte)(y * 255 / (h - 1));
                px[o + 2] = (byte)((x + y) * 255 / (w + h - 2)); px[o + 3] = (byte)(255 - (x * 255 / (w - 1)) / 2);
            }
        return new LdrImage("synthetic:gradient", w, h, px, gateSafe: true);
    }

    static LdrImage Noise()
    {
        int w = SynthSize, h = SynthSize; var px = new byte[w * h * 4]; var r = new Lcg(0xC0FFEE11);
        for (int i = 0; i < px.Length; i++) px[i] = r.Byte();
        return new LdrImage("synthetic:noise", w, h, px, gateSafe: true);
    }

    static LdrImage Edges()
    {
        int w = SynthSize, h = SynthSize; var px = new byte[w * h * 4];
        var pal = new (byte r, byte g, byte b)[] { (20,20,24),(220,30,40),(30,200,90),(40,90,230),(240,220,60),(200,60,220),(250,250,250),(90,90,90) };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = pal[((x / 16) * 3 + (y / 16) * 7) & 7];
                bool t = ((x / 16) + (y / 16)) % 5 == 0; int o = (y * w + x) * 4;
                px[o] = c.r; px[o + 1] = c.g; px[o + 2] = c.b; px[o + 3] = (byte)(t ? 0 : 255);
            }
        return new LdrImage("synthetic:edges", w, h, px, gateSafe: true);
    }

    static LdrImage Mixed()
    {
        int w = SynthSize, h = SynthSize; var px = new byte[w * h * 4]; var r = new Lcg(0x5EED1234);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (x < w / 2) { px[o] = (byte)(x * 255 / (w - 1)); px[o + 1] = (byte)(y * 255 / (h - 1)); px[o + 2] = 128; }
                else { px[o] = r.Byte(); px[o + 1] = r.Byte(); px[o + 2] = r.Byte(); }
                px[o + 3] = (byte)(((x / 32 + y / 32) % 4 == 0) ? 255 : (y * 255 / (h - 1)));
            }
        return new LdrImage("synthetic:mixed", w, h, px, gateSafe: true);
    }

    static ushort H16(float v) => BitConverter.HalfToUInt16Bits((Half)v);

    static HdrImage SynthHdr(string name) => name switch
    {
        "synthetic:sky" => SkyRamp(),
        "synthetic:sun" => SkySun(),
        _ => NoisyRamp(),
    };

    // SkyRamp/NoisyRamp use only +,*,/ and a LCG -> gate-safe ((Half) of a non-transcendental value is
    // deterministic across architectures). SkySun uses Exp/Sqrt -> excluded from the byte gate.
    static HdrImage SkyRamp()
    {
        int w = SynthSize, h = SynthSize; var px = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            float t = y / (float)(h - 1), lum = 0.03f + t * t * 16f;
            for (int x = 0; x < w; x++)
            {
                float hx = x / (float)(w - 1); int o = (y * w + x) * 3;
                px[o] = H16(lum * (0.85f + 0.30f * hx)); px[o + 1] = H16(lum * 0.95f); px[o + 2] = H16(lum * (1.15f - 0.25f * hx));
            }
        }
        return new HdrImage("synthetic:sky", w, h, px, gateSafe: true);
    }

    static HdrImage NoisyRamp()
    {
        int w = SynthSize, h = SynthSize; var px = new ushort[w * h * 3]; var r = new Lcg(0xBADF00D5);
        for (int y = 0; y < h; y++)
        {
            float lum = 0.04f + (y / (float)(h - 1)) * 6f;
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = H16(lum * (0.6f + 0.8f * r.Unit())); px[o + 1] = H16(lum * (0.5f + 0.6f * r.Unit())); px[o + 2] = H16(lum * (0.5f + 0.6f * r.Unit()));
            }
        }
        return new HdrImage("synthetic:noisy", w, h, px, gateSafe: true);
    }

    static HdrImage SkySun()
    {
        int w = SynthSize, h = SynthSize; var px = new ushort[w * h * 3];
        float cx = w * 0.5f, cy = h * 0.32f, rad = MathF.Min(w, h) * 0.12f;
        for (int y = 0; y < h; y++)
        {
            float t = y / (float)(h - 1), lum = 0.05f + (1 - t) * 8f;
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx, dy = y - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                float sun = 240f * MathF.Exp(-(d * d) / (2 * rad * rad)); int o = (y * w + x) * 3;
                px[o] = H16(lum + sun); px[o + 1] = H16(lum * 0.9f + sun * 0.95f); px[o + 2] = H16(lum * 1.2f + sun * 0.8f);
            }
        }
        return new HdrImage("synthetic:sun", w, h, px, gateSafe: false); // transcendentals -> not for the byte gate
    }
}