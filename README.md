# BasisBlockEncoder

Fast **CPU** block compressor for GPU texture formats (**BC1, BC3, BC4, BC5, BC6H, BC7**) exposed
to .NET through a safe, allocation-free, span-based API with single-block and **streaming (banded)**
entry points.

It wraps the analytical real-time block encoders that ship inside
[Binomial's basis_universal](https://github.com/BinomialLLC/basis_universal) transcoder
(`bc7f` / `encode_bc1` / `encode_bc4` / `fast_encode_bc6h`).

## Formats

| Format | Bytes / 4×4 block | Input            | Notes                                   |
|--------|-------------------|------------------|-----------------------------------------|
| BC1    | 8                 | RGBA8            | RGB (+1-bit alpha)                      |
| BC3    | 16                | RGBA8            | BC4 alpha block + BC1 color block       |
| BC4    | 8                 | RGBA8 (1 chan)   | Single channel (default R)              |
| BC5    | 16                | RGBA8 (2 chan)   | Two channels (default R,G)              |
| BC6H   | 16                | RGB FP16         | Unsigned HDR                            |
| BC7    | 16                | RGBA8            | High quality RGBA                       |

Surfaces whose width/height are not multiples of 4 are fine — partial edge blocks replicate the last
valid row/column, so you never have to pad the source yourself.

## Install

```
dotnet add package BasisBlockEncoder
```

The package contains the managed assembly plus native runtimes for `win-x64`, `win-x86`,
`linux-x64`, `linux-arm`, `linux-arm64`, `osx-x64`, and `osx-arm64` under `runtimes/<rid>/native/`;

## Usage

```csharp
using BasisBlockEncoder;

int width = 256, height = 256;
ReadOnlySpan<byte> rgba = /* width*height*4 bytes, R,G,B,A */;

// --- whole surface ---
var bc7 = new byte[BlockEncoder.EncodedSize(BcFormat.Bc7, width, height)];
BlockEncoder.EncodeBc7(rgba, width, height, strideBytes: width * 4, bc7, Bc7Flags.Default);

// other formats
BlockEncoder.EncodeBc1(rgba, width, height, width * 4, bc1Dst);
BlockEncoder.EncodeBc3(rgba, width, height, width * 4, bc3Dst);
BlockEncoder.EncodeBc4(rgba, width, height, width * 4, bc4Dst, channel: 0);
BlockEncoder.EncodeBc5(rgba, width, height, width * 4, bc5Dst, channel0: 0, channel1: 1);
```

### Streaming a surface in bands

Encode without ever holding the whole image. Feed rows as they become available (each band a
multiple of 4 rows; the final band may be any remaining count):

```csharp
var dst = new byte[BlockEncoder.EncodedSize(BcFormat.Bc7, width, height)];
var writer = new BlockRowStreamWriter(BcFormat.Bc7, width, height, dst, (uint)Bc7Flags.Default);

while (!writer.IsComplete)
{
    int rows = Math.Min(64, writer.PixelRowsRemaining); // 64 == 16 block-rows
    ReadOnlySpan<byte> band = ProduceRows(rows);        // your row source
    writer.WriteRows(band, rows, strideBytes: width * 4);
}
```

You can also drive the band encoder directly with `BlockEncoder.EncodeRows(...)`, or encode one
tight 4×4 block at a time with `BlockEncoder.EncodeBlock(...)`.

### BC6H (HDR)

BC6H takes RGB half-float. Pass `Half` (reinterpreted to FP16 bits) or raw `ushort` bits:

```csharp
ReadOnlySpan<Half> rgbHalf = /* width*height*3 Half values, R,G,B */;
var bc6h = new byte[BlockEncoder.EncodedSize(BcFormat.Bc6h, width, height)];
BlockEncoder.EncodeBc6h(rgbHalf, width, height, strideBytes: width * 3 * sizeof(ushort), bc6h,
                        Bc6hQuality.Default);

// streaming HDR
var hdrWriter = new Bc6hBlockRowStreamWriter(width, height, bc6h);
hdrWriter.WriteRows(bandHalf, rows, width * 3 * sizeof(ushort));
```

### Quality knobs

- **BC7**: `Bc7Flags` — `Fastest`, `Faster`, `Fast`, `Default`, `HighQuality` (or compose individual
  mode flags). Passing `0`/`None` selects `Default`.
- **BC1 / BC3**: `Bc1Quality` — `Fast` or `HighQuality`.
- **BC6H**: `Bc6hQuality` — `Fast`, `Default`, `HighQuality`.

## Building the native library

The CMake project fetches basis_universal at a **pinned commit** and compiles one translation unit
from its transcoder together with the shim.

```
cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --config Release --parallel
```

Output: `basis_block_encoder.dll` / `libbasis_block_encoder.so` / `libbasis_block_encoder.dylib`.

The default build is fully optimized: the `Release` configuration applies `-O3` / `/O2` and
`-DNDEBUG`,
link-time optimization (LTO/IPO) is enabled across the shim and transcoder, and 32-bit x86 builds
get SSE2 codegen. (basis_universal also has an experimental SSE4.1 bc7f path, but it is hard-disabled
upstream and not toggleable from the build, so the tested scalar path is used.)

Useful CMake options:

- `-DBBE_NATIVE_ARCH=ON` — tune for the building machine's CPU (`-march=native`). Faster, but the
  resulting binary is **not** portable.
- `-DBASISU_GIT_TAG=<commit>` — build against a different basis_universal revision.
- `-DBASISU_SOURCE_DIR=/path/to/basis_universal` — use a local checkout / submodule instead of fetching.

For local end-to-end testing, point the loader at the freshly built library, e.g. on Linux:

```
LD_LIBRARY_PATH="$PWD/native/build" dotnet test tests/BasisBlockEncoder.Tests/BasisBlockEncoder.Tests.csproj
```

## A note on the pinned dependency

The encoder functions this library calls (`bc7f::fast_pack_bc7_auto_rgba`, `encode_bc1`,
`encode_bc4`, `astc_6x6_hdr::fast_encode_bc6h`) are **internal** basis_universal APIs and may change
between revisions. Bump `BASISU_GIT_TAG` deliberately and re-test when
updating.

## License

This wrapper is licensed under the [MIT License](LICENSE). It builds against basis_universal, which
is Apache-2.0; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).