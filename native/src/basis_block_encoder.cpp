/*
 * basis_block_encoder shim implementation.
 *
 * Wraps the analytical real-time block encoders that ship inside basis_universal's single-file
 * transcoder module (transcoder/basisu_transcoder.cpp):
 *
 *   BC7  : basist::bc7f::fast_pack_bc7_auto_rgba   (declared in basisu_transcoder_internal.h)
 *   BC1  : basist::encode_bc1                      (defined in the .cpp; forward-declared below)
 *   BC4  : basist::encode_bc4                      (defined in the .cpp; forward-declared below)
 *   BC3  : BC4(alpha) + BC1(color)
 *   BC5  : BC4(ch0)   + BC4(ch1)
 *   BC6H : basist::astc_6x6_hdr::fast_encode_bc6h  (declared in basisu_astc_hdr_core.h)
 *
 * basist::basisu_transcoder_init() initializes the bc7f and BC6H tables (under the default
 * BASISD_SUPPORT_XUASTC / BASISD_SUPPORT_UASTC_HDR macros), so a single init call is all we need.
 */

#include <cstring>
#include <mutex>

#include "basisu_transcoder.h"
#include "basisu_transcoder_internal.h"
#include "basisu_astc_hdr_core.h"

#include "basis_block_encoder.h"

/*
 * encode_bc1 / encode_bc4 have external linkage in namespace basist but are not declared in any
 * public header, so we declare them here. Signatures verified against basisu_transcoder.cpp at the
 * pinned commit.
 */
namespace basist {
    void encode_bc1(void* pDst, const uint8_t* pPixels, uint32_t flags);
    void encode_bc4(void* pDst, const uint8_t* pPixels, uint32_t stride);
}

using namespace basist;

namespace {

std::once_flag g_init_once;
bool           g_init_ok = false;

void do_init() {
    basist::basisu_transcoder_init();
    g_init_ok = true;
}

inline int blocks_dim(int px) { return (px + 3) >> 2; }

/*
 * Gather a 4x4 RGBA8 block at block coordinates (bx, by) into out[16] (raster order). Source
 * coordinates are clamped to [0,width) x [0,valid_rows) so partial edge blocks replicate the
 * last valid texel. color_rgba is a 4-byte {r,g,b,a} union, so each texel is a straight 4-byte copy.
 */
inline void gather_rgba(color_rgba out[16], const uint8_t* src, int width, int valid_rows,
                        int stride, int bx, int by) {
    for (int ry = 0; ry < 4; ++ry) {
        int sy = by * 4 + ry;
        if (sy >= valid_rows) sy = valid_rows - 1;
        const uint8_t* row = src + (size_t)sy * (size_t)stride;
        for (int rx = 0; rx < 4; ++rx) {
            int sx = bx * 4 + rx;
            if (sx >= width) sx = width - 1;
            std::memcpy(&out[ry * 4 + rx], row + (size_t)sx * 4u, 4);
        }
    }
}

/* Gather a 4x4 RGB-FP16 block (48 halfs, raster order, R,G,B per texel) with the same clamping. */
inline void gather_rgb_half(uint16_t out[48], const uint16_t* src, int width, int valid_rows,
                            int stride_bytes, int bx, int by) {
    for (int ry = 0; ry < 4; ++ry) {
        int sy = by * 4 + ry;
        if (sy >= valid_rows) sy = valid_rows - 1;
        const uint16_t* row = (const uint16_t*)((const uint8_t*)src + (size_t)sy * (size_t)stride_bytes);
        for (int rx = 0; rx < 4; ++rx) {
            int sx = bx * 4 + rx;
            if (sx >= width) sx = width - 1;
            const uint16_t* p = row + (size_t)sx * 3u;
            uint16_t* o = &out[(ry * 4 + rx) * 3];
            o[0] = p[0];
            o[1] = p[1];
            o[2] = p[2];
        }
    }
}

/* Encode one already-gathered LDR block to dst (>= block_bytes). */
inline void encode_one_ldr(int format, const color_rgba* px, uint8_t* dst,
                           uint32_t flags, int ch0, int ch1) {
    switch (format) {
        case BBE_BC1:
            encode_bc1(dst, (const uint8_t*)px, flags);
            break;
        case BBE_BC3:
            /* DXT5/BC3 layout: 8 bytes alpha (BC4) followed by 8 bytes color (BC1). */
            encode_bc4(dst, &px[0].a, sizeof(color_rgba));
            encode_bc1(dst + 8, (const uint8_t*)px, flags);
            break;
        case BBE_BC4: {
            const int c = (ch0 < 0) ? 0 : ch0;
            encode_bc4(dst, &px[0].m_comps[c], sizeof(color_rgba));
            break;
        }
        case BBE_BC5: {
            const int c0 = (ch0 < 0) ? 0 : ch0;
            const int c1 = (ch1 < 0) ? 1 : ch1;
            encode_bc4(dst,     &px[0].m_comps[c0], sizeof(color_rgba));
            encode_bc4(dst + 8, &px[0].m_comps[c1], sizeof(color_rgba));
            break;
        }
        case BBE_BC7: {
            const uint32_t f = flags ? flags : (uint32_t)bc7f::cPackBC7FlagDefault;
            bc7f::fast_pack_bc7_auto_rgba(dst, px, f);
            break;
        }
        default:
            break;
    }
}

inline bool is_ldr_format(int format) {
    return format == BBE_BC1 || format == BBE_BC3 || format == BBE_BC4 ||
           format == BBE_BC5 || format == BBE_BC7;
}

int encode_ldr_rows(int format, const uint8_t* src, int width, int pixel_rows, int stride,
                    uint8_t* dst, size_t dst_size, uint32_t flags, int ch0, int ch1) {
    if (!g_init_ok)                     return BBE_ERR_NOT_INITIALIZED;
    if (!src || !dst)                   return BBE_ERR_NULL_POINTER;
    if (!is_ldr_format(format))         return BBE_ERR_INVALID_ARGS;
    if (width <= 0 || pixel_rows <= 0)  return BBE_ERR_INVALID_ARGS;
    if (stride < width * 4)             return BBE_ERR_INVALID_ARGS;

    const int    bb        = bbe_block_bytes(format);
    const int    bw        = blocks_dim(width);
    const int    bh        = blocks_dim(pixel_rows);
    const size_t row_bytes = (size_t)bw * (size_t)bb;
    const size_t need      = row_bytes * (size_t)bh;
    if (dst_size < need)                return BBE_ERR_DST_TOO_SMALL;

    color_rgba block[16];
    for (int by = 0; by < bh; ++by) {
        uint8_t* drow = dst + (size_t)by * row_bytes;
        for (int bx = 0; bx < bw; ++bx) {
            gather_rgba(block, src, width, pixel_rows, stride, bx, by);
            encode_one_ldr(format, block, drow + (size_t)bx * (size_t)bb, flags, ch0, ch1);
        }
    }
    return BBE_OK;
}

void bc6h_params_from_quality(astc_6x6_hdr::fast_bc6h_params& p, uint32_t quality) {
    p.init(); /* defaults: m_hq_ls=true, m_max_2subset_pats_to_try=1, m_brute_force=false */
    if (quality == 0) {
        p.m_hq_ls = false;
        p.m_max_2subset_pats_to_try = 1;
        p.m_brute_force_weight4_assignment = false;
    } else if (quality >= 2) {
        p.m_hq_ls = true;
        p.m_max_2subset_pats_to_try = 4;
        p.m_brute_force_weight4_assignment = true;
    }
    /* quality == 1 keeps init() defaults */
}

int encode_bc6h_rows_impl(const uint16_t* src, int width, int pixel_rows, int stride_bytes,
                          uint8_t* dst, size_t dst_size, uint32_t quality) {
    if (!g_init_ok)                     return BBE_ERR_NOT_INITIALIZED;
    if (!src || !dst)                   return BBE_ERR_NULL_POINTER;
    if (width <= 0 || pixel_rows <= 0)  return BBE_ERR_INVALID_ARGS;
    if (stride_bytes < width * 3 * 2)   return BBE_ERR_INVALID_ARGS;

    const int    bw        = blocks_dim(width);
    const int    bh        = blocks_dim(pixel_rows);
    const size_t row_bytes = (size_t)bw * 16u;
    const size_t need      = row_bytes * (size_t)bh;
    if (dst_size < need)                return BBE_ERR_DST_TOO_SMALL;

    astc_6x6_hdr::fast_bc6h_params params;
    bc6h_params_from_quality(params, quality);

    uint16_t block[48];
    for (int by = 0; by < bh; ++by) {
        uint8_t* drow = dst + (size_t)by * row_bytes;
        for (int bx = 0; bx < bw; ++bx) {
            gather_rgb_half(block, src, width, pixel_rows, stride_bytes, bx, by);
            fast_encode_bc6h(block, (bc6h_block*)(drow + (size_t)bx * 16u), params);
        }
    }
    return BBE_OK;
}

} /* anonymous namespace */

extern "C" {

BBE_API int bbe_init(void) {
    std::call_once(g_init_once, do_init);
    return g_init_ok ? BBE_OK : BBE_ERR_NOT_INITIALIZED;
}

BBE_API int bbe_block_bytes(int format) {
    switch (format) {
        case BBE_BC1:
        case BBE_BC4:  return 8;
        case BBE_BC3:
        case BBE_BC5:
        case BBE_BC6H:
        case BBE_BC7:  return 16;
        default:       return 0;
    }
}

BBE_API size_t bbe_encoded_size(int format, int width, int height) {
    const int bb = bbe_block_bytes(format);
    if (bb == 0 || width <= 0 || height <= 0) return 0;
    return (size_t)blocks_dim(width) * (size_t)blocks_dim(height) * (size_t)bb;
}

BBE_API int bbe_encode(int format,
                       const uint8_t* src_rgba, int width, int height, int src_stride_bytes,
                       uint8_t* dst, size_t dst_size,
                       uint32_t flags, int channel0, int channel1) {
    return encode_ldr_rows(format, src_rgba, width, height, src_stride_bytes,
                           dst, dst_size, flags, channel0, channel1);
}

BBE_API int bbe_encode_rows(int format,
                            const uint8_t* src_rgba, int width, int pixel_rows, int src_stride_bytes,
                            uint8_t* dst, size_t dst_size,
                            uint32_t flags, int channel0, int channel1) {
    return encode_ldr_rows(format, src_rgba, width, pixel_rows, src_stride_bytes,
                           dst, dst_size, flags, channel0, channel1);
}

BBE_API int bbe_encode_block(int format,
                             const uint8_t* block_rgba16, uint8_t* dst,
                             uint32_t flags, int channel0, int channel1) {
    if (!g_init_ok)             return BBE_ERR_NOT_INITIALIZED;
    if (!block_rgba16 || !dst)  return BBE_ERR_NULL_POINTER;
    if (!is_ldr_format(format)) return BBE_ERR_INVALID_ARGS;
    encode_one_ldr(format, (const color_rgba*)block_rgba16, dst, flags, channel0, channel1);
    return BBE_OK;
}

BBE_API int bbe_encode_bc6h(const uint16_t* src_rgb_half, int width, int height, int src_stride_bytes,
                            uint8_t* dst, size_t dst_size, uint32_t quality) {
    return encode_bc6h_rows_impl(src_rgb_half, width, height, src_stride_bytes, dst, dst_size, quality);
}

BBE_API int bbe_encode_bc6h_rows(const uint16_t* src_rgb_half, int width, int pixel_rows, int src_stride_bytes,
                                 uint8_t* dst, size_t dst_size, uint32_t quality) {
    return encode_bc6h_rows_impl(src_rgb_half, width, pixel_rows, src_stride_bytes, dst, dst_size, quality);
}

BBE_API int bbe_encode_bc6h_block(const uint16_t* block_rgb_half48, uint8_t* dst, uint32_t quality) {
    if (!g_init_ok)                 return BBE_ERR_NOT_INITIALIZED;
    if (!block_rgb_half48 || !dst)  return BBE_ERR_NULL_POINTER;
    astc_6x6_hdr::fast_bc6h_params params;
    bc6h_params_from_quality(params, quality);
    fast_encode_bc6h(block_rgb_half48, (bc6h_block*)dst, params);
    return BBE_OK;
}

} /* extern "C" */
