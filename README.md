# BasisBlockEncoder — managed BCn texture compression

**A pure-C# CPU block compressor for BC1, BC3, BC4, BC5, BC6H, and BC7 — no native dependency, the same bytes on every CPU architecture, and the same API as the native-backed `BasisBlockEncoder` package.**

![License: MIT](https://img.shields.io/badge/license-MIT-blue) ![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4) ![native deps](https://img.shields.io/badge/native%20deps-none-success) ![unsafe](https://img.shields.io/badge/unsafe-none-success)

Swap one package reference and your texture pipeline stops shipping a native `.dll` / `.so` / `.dylib` for every platform you support. Everything runs in managed code: span-based, allocation-free on the hot path, and SIMD-accelerated through `System.Runtime.Intrinsics` (AVX-512, AVX2, ARM NEON) with a scalar fallback everywhere else.

## Why this package

- **One assembly, every platform.** No P/Invoke, no native build step, no runtime-identifier matrix — a single `BasisBlockEncoder.dll` that trimming and Native AOT can see straight through.
- **Bit-for-bit deterministic across architectures.** The same input yields the same output bytes on x86 and ARM, regardless of SIMD width — and CI proves it, re-encoding a fixed corpus on both and asserting the bytes match exactly. Reproducible builds, stable content hashes, and golden-image tests simply hold. Most SIMD encoders can't promise this; they drift by a rounding bit between vector widths or instruction sets.
- **Reference-grade quality.** BC4/BC5 are byte-exact against the C++ reference and provably optimal; BC6H meets or exceeds its PSNR; BC7 reproduces the reference's mode decisions with PSNR within a rounding step. A `verify` tool substantiates parity on *your* assets, not a synthetic benchmark.
- **Drop-in API.** Same type names, same enum values, same method shapes as the native-backed package. For most callers, migration is a one-line change.
- **Allocation-free and span-first.** The per-block path allocates nothing (0 B), so it won't churn the GC inside a tight encode loop.

## Install

```
dotnet add package BasisBlockEncoder
```

Targets **net8.0** and **net10.0**. No third-party runtime dependencies.

## Quick start

```csharp
using BasisBlockEncoder;

// RGBA8 image, tightly packed (strideBytes = width * 4) -> BC7.
int width = 1024, height = 1024;
byte[] rgba = LoadRgba8(width, height);

byte[] dst = new byte[BlockEncoder.EncodedSize(BcFormat.Bc7, width, height)];
BlockEncoder.EncodeBc7(rgba, width, height, width * 4, dst);   // defaults to Bc7Flags.Default
```

Choose a preset, or any other format:

```csharp
BlockEncoder.EncodeBc7(rgba, w, h, w * 4, dst, Bc7Flags.Fast);            // faster, slightly lower quality
BlockEncoder.EncodeBc1(rgba, w, h, w * 4, dst, Bc1Quality.HighQuality);   // RGB (+1-bit alpha)
BlockEncoder.EncodeBc3(rgba, w, h, w * 4, dst);                           // RGBA: BC4 alpha + BC1 color
BlockEncoder.EncodeBc4(rgba, w, h, w * 4, dst, channel: 0);              // single channel (R)
BlockEncoder.EncodeBc5(rgba, w, h, w * 4, dst, channel0: 0, channel1: 1); // two channels (RG) — normal maps
```

HDR (BC6H) takes RGB half-floats:

```csharp
ReadOnlySpan<Half> rgbHalf = /* width * height * 3 Half values */;
byte[] dst = new byte[BlockEncoder.EncodedSize(BcFormat.Bc6h, w, h)];
BlockEncoder.EncodeBc6h(rgbHalf, w, h, w * 3 * sizeof(ushort), dst, Bc6hQuality.Default);
```

Prefer to drive it generically? `Encode` dispatches by format:

```csharp
BlockEncoder.Encode(BcFormat.Bc7, rgba, w, h, w * 4, dst, flags: (uint)Bc7Flags.Default);
```

## Supported formats

| Format | Input | Block size | Notes |
|---|---|---|---|
| **BC1** | RGBA8 | 8 B | RGB + 1-bit alpha; always 4-color (GPU-safe) — never 3-color/punch-through |
| **BC3** | RGBA8 | 16 B | BC4 alpha + BC1 color |
| **BC4** | 1 channel | 8 B | Byte-exact vs the reference; provably optimal |
| **BC5** | 2 channels | 16 B | Two BC4 halves — ideal for tangent-space normal maps |
| **BC6H** | RGB FP16 | 16 B | Unsigned HDR |
| **BC7** | RGBA8 | 16 B | Highest-quality LDR; analytical mode search |

BC2 is intentionally omitted, matching the native package.

## Quality and determinism

Two guarantees, and how to check them yourself:

- **Cross-architecture byte-identity.** Output is independent of CPU and SIMD width: scalar, AVX2, AVX-512, and NEON all produce the same bytes. The integer/associative math is identical by construction, and a CI golden gate re-encodes a fixed corpus on x86 and ARM and asserts an exact match.
- **Reference-grade quality.** The encoder doesn't chase byte-identity with the native encoder at the higher BC7 presets — it shouldn't need to. It tracks *quality* instead: BC4/BC5 bit-exact and optimal, BC6H at or above the reference's PSNR, BC7 matching the reference's mode choices with PSNR within a rounding step. The `verify` tool reports per-format PSNR (managed vs reference) on the textures you actually ship.

## Performance

This is a real-time *analytical* encoder: it computes each block's best fit directly rather than brute-forcing the mode space, and it vectorizes that math with a width-adaptive path — AVX-512 → AVX2 → NEON → scalar — selected at JIT time behind hardware-acceleration guards. The per-block path is allocation-free.

Each block is encoded on a single thread by design, but blocks are independent: fan the per-block calls across cores (for example, `Parallel.For` over block rows) and throughput scales with them. The source repository ships a BenchmarkDotNet suite so you can measure managed-vs-native on your own hardware and assets — the only numbers worth trusting.

## Quality knobs

- **`Bc7Flags`** — `Fastest`, `Faster`, `Fast`, `Default` (recommended), `HighQuality`. Higher presets enable more mode families (two-/three-subset, dual-plane) and p-bit optimization; `HighQuality` runs the full analytical search. The optional non-analytical brute-force passes are accepted for source compatibility but not performed — the analytical search is what delivers the quality.
- **`Bc1Quality`** — `Fast` or `HighQuality`, for the BC1/BC3 color block.
- **`Bc6hQuality`** — `Fast`, `Default`, `HighQuality`.

## Streaming

Encode in horizontal bands instead of materializing the whole surface: `EncodeRows` / `EncodeBc6hRows` (bands must be a multiple of 4 rows, except the last), or the `BlockRowStreamWriter` / `Bc6hBlockRowStreamWriter` helpers.

## Migrating from the native-backed package

The public surface mirrors the original 1:1 — the same `BlockEncoder` methods and the same `BcFormat` / `Bc1Quality` / `Bc6hQuality` / `Bc7Flags` names and numeric values. Replace the package reference; `BlockEncoder.Initialize()` stays as a no-op for compatibility. Output is quality-equivalent rather than byte-identical at the higher BC7 presets, so if you pin golden *bytes* against the native encoder, regenerate them — or pin against PSNR instead.

## Requirements

.NET 8 or .NET 10. No native dependencies, and no `unsafe` code (`AllowUnsafeBlocks` is off).

## License

This package is **MIT**. The encoder is a managed port of [Basis Universal](https://github.com/BinomialLLC/basis_universal)'s block encoders — retain the **Apache-2.0** Basis Universal attribution/NOTICE when you redistribute.
