using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bcn;
#if IMAGESHARP
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
#endif

namespace Bcn.Ldr;

// Recursive image -> RGBA block-pool loader. All ImageSharp use is gated behind IMAGESHARP so the harness
// stays compilable without the package; build the test project with -p:EnableImageSharp=true to enable it.
// Decodes every supported image under a directory, crops each to whole 4x4 blocks, and accumulates one
// in-memory ColorRgba[] pool (nb*16 pixels, block-major: block b occupies [b*16 .. b*16+15], row-major 4x4).
internal static class ArtworkLoader
{
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".tif", ".tiff", ".webp", ".qoi", ".pbm", ".ppm", ".pgm" };

    public static bool Available =>
#if IMAGESHARP
        true;
#else
        false;
#endif

    public static ColorRgba[] LoadDir(string dir, out int nb, out int images, out long pixels)
    {
        nb = 0; images = 0; pixels = 0;
#if IMAGESHARP
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                             .Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .OrderBy(f => f).ToList();
        if (files.Count == 0) throw new FileNotFoundException($"no supported images found under '{dir}'");

        var perImage = new List<ColorRgba[]>(files.Count);
        long totalBlocks = 0, totalPixels = 0;
        foreach (var f in files)
        {
            ColorRgba[] blk;
            try
            {
                using var img = Image.Load<Rgba32>(f);
                int bw = img.Width / 4, bh = img.Height / 4;       // whole 4x4 blocks; partial right/bottom edge dropped
                if (bw == 0 || bh == 0) continue;
                int w = img.Width;
                blk = new ColorRgba[bw * bh * 16];
                img.ProcessPixelRows(acc =>
                {
                    for (int by = 0; by < bh; by++)
                        for (int r = 0; r < 4; r++)
                        {
                            var row = acc.GetRowSpan(by * 4 + r);
                            for (int bx = 0; bx < bw; bx++)
                            {
                                int bbase = (by * bw + bx) * 16 + r * 4;
                                int px = bx * 4;
                                for (int c = 0; c < 4; c++)
                                {
                                    Rgba32 p = row[px + c];
                                    blk[bbase + c] = new ColorRgba { R = p.R, G = p.G, B = p.B, A = p.A };
                                }
                            }
                        }
                });
                totalPixels += (long)w * img.Height;
            }
            catch { continue; }   // skip anything that won't decode
            perImage.Add(blk);
            totalBlocks += blk.Length / 16;
            images++;
        }
        if (totalBlocks == 0) throw new InvalidOperationException("no decodable image yielded a full 4x4 block");

        var pool = new ColorRgba[totalBlocks * 16];
        int off = 0;
        for (int i = 0; i < perImage.Count; i++)
        {
            Array.Copy(perImage[i], 0, pool, off, perImage[i].Length);
            off += perImage[i].Length;
            perImage[i] = null!;   // let each source buffer be collected as we go (keeps peak near 1x)
        }
        perImage.Clear();
        nb = (int)totalBlocks; pixels = totalPixels;
        return pool;
#else
        throw new InvalidOperationException(
            "Image loading is disabled. Rebuild the test project with -p:EnableImageSharp=true " +
            "(adds the SixLabors.ImageSharp 2.x package) to use --artwork-dir.");
#endif
    }
}
