/*
 * basis_block_encoder - a thin C ABI over Binomial's basis_universal analytical
 * real-time GPU texture block encoders (bc7f / encode_bc1 / encode_bc4 / fast_encode_bc6h).
 *
 * All encoders are CPU, portable (no SIMD required), and operate on independent 4x4 blocks,
 * so the whole-surface, banded (streaming) and single-block entry points below all share the
 * same per-block core.
 *
 * Pixel formats:
 *   LDR formats (BC1/BC3/BC4/BC5/BC7) take 8-bit RGBA, 4 bytes/pixel, R,G,B,A byte order.
 *   BC6H takes RGB half-float (FP16), 3 halfs/pixel, R,G,B order, unsigned variant.
 *
 * Edge handling: surfaces whose width/height are not multiples of 4 are fine - partial edge
 * blocks are filled by replicating the last valid row/column (no caller-side padding required).
 *
 * Thread-safety: bbe_init() is idempotent and safe to call from multiple threads. After init,
 * the encode entry points are re-entrant and may be called concurrently on disjoint outputs.
 *
 * Licensing: this shim is MIT; basis_universal is Apache-2.0. The encoder functions wrapped
 * here are *internal* basis_universal APIs and may change between commits - the build pins a
 * specific commit for that reason.
 */
#ifndef BASIS_BLOCK_ENCODER_H
#define BASIS_BLOCK_ENCODER_H

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
  #define BBE_API __declspec(dllexport)
#else
  #define BBE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Return codes. */
typedef enum bbe_result {
    BBE_OK                  = 0,
    BBE_ERR_NOT_INITIALIZED = 1, /* bbe_init() not called / failed */
    BBE_ERR_NULL_POINTER    = 2, /* src or dst was NULL */
    BBE_ERR_INVALID_ARGS    = 3, /* bad format, dimensions, or stride */
    BBE_ERR_DST_TOO_SMALL   = 4  /* dst_size smaller than the encoded size */
} bbe_result;

/* Block formats. Values are stable and mirrored by the C# BcFormat enum. */
typedef enum bbe_format {
    BBE_BC1  = 1, /*  8 bytes/block, RGB (1-bit or no alpha)        */
    BBE_BC3  = 3, /* 16 bytes/block, RGBA (BC4 alpha + BC1 color)   */
    BBE_BC4  = 4, /*  8 bytes/block, single channel                */
    BBE_BC5  = 5, /* 16 bytes/block, two channels                  */
    BBE_BC6H = 6, /* 16 bytes/block, RGB FP16 (unsigned)           */
    BBE_BC7  = 7  /* 16 bytes/block, RGBA                          */
} bbe_format;

/*
 * One-time initialization of the underlying transcoder tables. Idempotent and thread-safe.
 * Must succeed before any encode call. Returns BBE_OK on success.
 */
BBE_API int bbe_init(void);

/* Bytes per 4x4 block for a format (8 or 16); 0 for an unknown format. */
BBE_API int bbe_block_bytes(int format);

/* Total bytes for a tightly-packed encode of width x height (rounds up to whole 4x4 blocks). */
BBE_API size_t bbe_encoded_size(int format, int width, int height);

/* ----------------------------------------------------------------------------------------
 * LDR (8-bit RGBA) encoders.
 *
 *   src_rgba         : pointer to the top-left pixel, R,G,B,A byte order.
 *   width, height    : surface size in pixels (any value >= 1; edges are clamped).
 *   src_stride_bytes : bytes between consecutive pixel rows (>= width*4).
 *   dst              : tightly-packed block output; row stride = ceil(width/4)*block_bytes.
 *   dst_size         : capacity of dst in bytes; must be >= bbe_encoded_size(...).
 *   flags            : BC1/BC3 -> BC1 quality flags (see C# Bc1Quality; HighQuality == 1).
 *                      BC7      -> bc7f flags (see C# Bc7Flags; 0 selects a safe default).
 *                      BC4/BC5  -> ignored.
 *   channel0/channel1: BC4 uses channel0 (default 0 = R when < 0).
 *                      BC5 uses channel0,channel1 (defaults 0,1 = R,G when < 0).
 *                      Other formats ignore these.
 * ---------------------------------------------------------------------------------------- */

/* Encode the whole surface. */
BBE_API int bbe_encode(int format,
                       const uint8_t* src_rgba, int width, int height, int src_stride_bytes,
                       uint8_t* dst, size_t dst_size,
                       uint32_t flags, int channel0, int channel1);

/*
 * Streaming primitive: encode a horizontal band of `pixel_rows` rows into `dst`.
 * Produces ceil(pixel_rows/4) block-rows. Feed the surface top-to-bottom in bands whose
 * height is a multiple of 4 (the final band may be any remaining row count); advance src by
 * pixel_rows rows and dst by ceil(pixel_rows/4)*ceil(width/4)*block_bytes each call.
 */
BBE_API int bbe_encode_rows(int format,
                            const uint8_t* src_rgba, int width, int pixel_rows, int src_stride_bytes,
                            uint8_t* dst, size_t dst_size,
                            uint32_t flags, int channel0, int channel1);

/*
 * Encode a single tight 4x4 block. block_rgba16 is 16 RGBA8 pixels (64 bytes) in raster order;
 * dst receives block_bytes(format) bytes.
 */
BBE_API int bbe_encode_block(int format,
                             const uint8_t* block_rgba16, uint8_t* dst,
                             uint32_t flags, int channel0, int channel1);

/* ----------------------------------------------------------------------------------------
 * BC6H (unsigned) encoders. Input is RGB half-float (FP16), 3 halfs/pixel, R,G,B order.
 *
 *   src_rgb_half     : pointer to the top-left pixel as uint16_t FP16 bit patterns.
 *   src_stride_bytes : bytes between rows (>= width*3*sizeof(uint16_t) == width*6).
 *   quality          : 0 = fast, 1 = default, 2 = high quality (mirrors C# Bc6hQuality).
 * ---------------------------------------------------------------------------------------- */

BBE_API int bbe_encode_bc6h(const uint16_t* src_rgb_half, int width, int height, int src_stride_bytes,
                            uint8_t* dst, size_t dst_size, uint32_t quality);

BBE_API int bbe_encode_bc6h_rows(const uint16_t* src_rgb_half, int width, int pixel_rows, int src_stride_bytes,
                                 uint8_t* dst, size_t dst_size, uint32_t quality);

/* block_rgb_half48 is 16 pixels x 3 halfs (48 uint16_t) in raster order; dst receives 16 bytes. */
BBE_API int bbe_encode_bc6h_block(const uint16_t* block_rgb_half48, uint8_t* dst, uint32_t quality);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* BASIS_BLOCK_ENCODER_H */
