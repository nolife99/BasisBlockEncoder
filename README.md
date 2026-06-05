# BasisBlockEncoder — managed BCn texture compression
A fully-managed, hardware-accelerated, zero-allocation CPU block compressor for BC1, BC3, BC4, BC5, BC6H, and BC7 based on BinomialLLC's [Transcoder Internals Analytical Real Time Encoders](https://github.com/BinomialLLC/basis_universal/wiki/Transcoder-Internals-Analytical-Real-Time-Encoders).
## Parity with [transcoder/basisu_transcoder.cpp](https://github.com/BinomialLLC/basis_universal/blob/master/transcoder/basisu_transcoder.cpp)
- **Bit-for-bit deterministic across architectures.** The same input yields the same output bytes on x86 and ARM, regardless of SIMD width
- **Reference-grade quality.** BC4/BC5 are byte-exact against the C++ reference; BC6H meets or exceeds its PSNR; BC7 reproduces the reference's mode decisions with PSNR within a rounding step
## Install
```
dotnet add package BasisBlockEncoder
```
Targets **net7.0** through **net10.0**, no third-party runtime dependencies.
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
If you prefer to drive it generically, `Encode` dispatches by format:
```csharp
BlockEncoder.Encode(BcFormat.Bc7, rgba, w, h, w * 4, dst, flags: (uint)Bc7Flags.Default);
```
## Supported formats
| Format | Input | Block size | Notes |
|---|---|---|---|
| **BC1** | RGBA8 | 8 B | RGB + 1-bit alpha; always 4-color (GPU-safe) — never 3-color/punch-through |
| **BC3** | RGBA8 | 16 B | BC4 alpha + BC1 color |
| **BC4** | 1 channel | 8 B | Byte-exact vs the reference |
| **BC5** | 2 channels | 16 B | Two BC4 halves, ideal for tangent-space normal maps |
| **BC6H** | RGB FP16 | 16 B | Unsigned HDR |
| **BC7** | RGBA8 | 16 B | Highest-quality LDR; analytical mode search |

BC2 is intentionally omitted, matching the C++ implementation.
## Performance
This is a real-time *analytical* encoder: it computes each block's best fit directly rather than brute-forcing the mode space, and it vectorizes that math with a width-adaptive path — AVX-512 → AVX2 → NEON → scalar — selected at JIT time behind hardware-acceleration guards. The per-block path is allocation-free.

Each block is encoded on a single thread by design, but blocks are independent: fan the per-block calls across cores (for example, `Parallel.For` over block rows) and throughput scales with them.
## Quality knobs
- **`Bc7Flags`** — `Fastest`, `Faster`, `Fast`, `Default` (recommended), `HighQuality`. Higher presets enable more mode families (two-/three-subset, dual-plane) and p-bit optimization; `HighQuality` runs the full analytical search. The optional non-analytical brute-force passes are accepted for source compatibility but not performed — the analytical search is what delivers the quality.
- **`Bc1Quality`** — `Fast` or `HighQuality`, for the BC1/BC3 color block.
- **`Bc6hQuality`** — `Fast`, `Default`, `HighQuality`.
## Streaming
Encode in horizontal bands instead of materializing the whole surface: `EncodeRows` / `EncodeBc6hRows` (bands must be a multiple of 4 rows, except the last), or the `BlockRowStreamWriter` / `Bc6hBlockRowStreamWriter` helpers.
## License
This package is **MIT**. The encoder is a managed port of [Basis Universal](https://github.com/BinomialLLC/basis_universal)'s block encoders, so retain the **Apache-2.0** Basis Universal attribution/NOTICE when  redistributing.