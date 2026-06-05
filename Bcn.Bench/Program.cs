// Entry point for the encoder benchmark / verification tool.
//
//   dotnet run -c Release                         run the full BenchmarkDotNet timing suite (all formats)
//   dotnet run -c Release -- bench [BDN args]     same, forwarding BenchmarkDotNet CLI args (--filter, --job short, ...)
//   dotnet run -c Release -- verify               managed-vs-native PSNR parity report (CI quality gate)
//   dotnet run -c Release -- golden-write FILE    write per-architecture byte-identity digests
//   dotnet run -c Release -- golden-check FILE    re-encode and assert reproduction of FILE
//
// Artworks: drop image files into ../artworks (.png/.jpg/... for LDR; .hdr for BC6H). With none, procedural
// textures stand in. Override the folder with the BCN_BENCH_ARTWORKS environment variable. Each image is
// encoded whole, at its native resolution (cropped only to a multiple of 4) — there is no size parameter.
using BenchmarkDotNet.Running;
using Bcn.Bench;

if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "verify": return Verify.Run();
        case "golden-write": return Golden.Write(args.Length > 1 ? args[1] : "golden.bin");
        case "golden-check": return Golden.Check(args.Length > 1 ? args[1] : "golden.bin");
    }
}

// Resolve the artworks folder in this (host) process and hand it to the child benchmark processes that
// BenchmarkDotNet spawns — they inherit the environment but not the working directory.
Environment.SetEnvironmentVariable(Images.Env, Images.ResolvedArtworkDir);

var forwarded = args.Length > 0 && args[0].Equals("bench", StringComparison.OrdinalIgnoreCase) ? args[1..] : args;
// A bare BenchmarkSwitcher with no selector waits on stdin, which hangs a CI job with no console. Default a
// bare invocation to "run everything"; pass an explicit --filter / --job to narrow.
if (forwarded.Length == 0) forwarded = new[] { "--filter", "*" };

BenchmarkSwitcher.FromAssembly(typeof(Bc7Benchmarks).Assembly).Run(forwarded);
return 0;
