#ifndef HYPR_THUMBNAIL_RESAMPLE_H
#define HYPR_THUMBNAIL_RESAMPLE_H

#include <stddef.h>
#include <stdint.h>

/* Keep the bounded kernel optimized in debug builds and forbid contraction. */
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC push_options
#pragma GCC optimize("O2", "fp-contract=off")
#endif

static unsigned char thumbnail_byte(double value)
{
    return value >= 255.0 ? 255 : (unsigned char)(value + 0.5);
}

static double thumbnail_channel(uint32_t p0, uint32_t p1, uint32_t p2,
                                uint32_t p3, int shift, double w0, double w1,
                                double w2, double w3)
{
#if defined(__clang__)
#pragma clang fp contract(off)
#endif
    return ((p0 >> shift) & 255) * w0 + ((p1 >> shift) & 255) * w1 +
           ((p2 >> shift) & 255) * w2 + ((p3 >> shift) & 255) * w3;
}

static inline void thumbnail_pixel_scalar(uint32_t p0, uint32_t p1, uint32_t p2,
                                          uint32_t p3, double w0, double w1,
                                          double w2, double w3, int opaque,
                                          unsigned char *out)
{
#if defined(__clang__)
#pragma clang fp contract(off)
#endif
    double alpha = opaque
                       ? 255 * w0 + 255 * w1 + 255 * w2 + 255 * w3
                       : thumbnail_channel(p0, p1, p2, p3, 24, w0, w1, w2, w3);
    out[3] = thumbnail_byte(alpha);
    /* Preserve premultiplied filtering and summation order, then
     * unpremultiply only the bounded output for the renderer. */
    double scale = out[3] == 0 ? 0 : 255.0 / alpha;
    out[0] = thumbnail_byte(
        thumbnail_channel(p0, p1, p2, p3, 16, w0, w1, w2, w3) * scale);
    out[1] = thumbnail_byte(
        thumbnail_channel(p0, p1, p2, p3, 8, w0, w1, w2, w3) * scale);
    out[2] = thumbnail_byte(
        thumbnail_channel(p0, p1, p2, p3, 0, w0, w1, w2, w3) * scale);
}

#define THUMBNAIL_ATTRIBUTE
#define THUMBNAIL_KERNEL thumbnail_resample_scalar
#define THUMBNAIL_PIXEL thumbnail_pixel_scalar
#include "thumbnail_resample_loop.h"
#undef THUMBNAIL_ATTRIBUTE
#undef THUMBNAIL_KERNEL
#undef THUMBNAIL_PIXEL

#if !defined(THUMBNAIL_FORCE_SCALAR) &&                                        \
    (defined(__x86_64__) || defined(__i386__)) &&                              \
    (defined(__GNUC__) || defined(__clang__))
#define THUMBNAIL_HAS_AVX2 1
#include <immintrin.h>

__attribute__((target("avx2,no-fma"))) static inline __m256d
thumbnail_unpack(uint32_t p, int opaque)
{
    return _mm256_cvtepi32_pd(_mm_setr_epi32((p >> 16) & 255, (p >> 8) & 255,
                                             p & 255, opaque ? 255 : p >> 24));
}

__attribute__((target("avx2,no-fma"))) static inline void
thumbnail_pixel_avx2(uint32_t p0, uint32_t p1, uint32_t p2, uint32_t p3,
                     double w0, double w1, double w2, double w3, int opaque,
                     unsigned char *out)
{
    /* Explicit multiply/add sequence matches scalar's left-associative sum. */
    __m256d channels =
        _mm256_mul_pd(thumbnail_unpack(p0, opaque), _mm256_set1_pd(w0));
    channels =
        _mm256_add_pd(channels, _mm256_mul_pd(thumbnail_unpack(p1, opaque),
                                              _mm256_set1_pd(w1)));
    channels =
        _mm256_add_pd(channels, _mm256_mul_pd(thumbnail_unpack(p2, opaque),
                                              _mm256_set1_pd(w2)));
    channels =
        _mm256_add_pd(channels, _mm256_mul_pd(thumbnail_unpack(p3, opaque),
                                              _mm256_set1_pd(w3)));
    /* The high half contains B,A, so extract its second lane. */
    double alpha =
        _mm_cvtsd_f64(_mm_unpackhi_pd(_mm256_extractf128_pd(channels, 1),
                                      _mm256_extractf128_pd(channels, 1)));
    out[3] = thumbnail_byte(alpha);
    double scale = out[3] == 0 ? 0 : 255.0 / alpha;
    channels = _mm256_mul_pd(channels, _mm256_set1_pd(scale));
    channels = _mm256_min_pd(channels, _mm256_set1_pd(255.0));
    __m128i rounded =
        _mm256_cvttpd_epi32(_mm256_add_pd(channels, _mm256_set1_pd(0.5)));
    rounded = _mm_packus_epi32(rounded, rounded);
    rounded = _mm_packus_epi16(rounded, rounded);
    uint32_t rgb = (uint32_t)_mm_cvtsi128_si32(rounded);
    out[0] = rgb;
    out[1] = rgb >> 8;
    out[2] = rgb >> 16;
}

/* Four lanes are independent output pixels, not four channels of one pixel.
 * Keep alpha normalization: its rounding error can change half-byte ties. */
__attribute__((target("avx2,no-fma"))) static inline void
thumbnail_pixels_opaque_avx2(const uint32_t samples[4][4],
                             const double weights[4][4], unsigned char *out)
{
    __m256d w0 = _mm256_loadu_pd(weights[0]);
    __m256d w1 = _mm256_loadu_pd(weights[1]);
    __m256d w2 = _mm256_loadu_pd(weights[2]);
    __m256d w3 = _mm256_loadu_pd(weights[3]);
    __m256d constant = _mm256_set1_pd(255.0);
    __m256d alpha = _mm256_mul_pd(constant, w0);
    alpha = _mm256_add_pd(alpha, _mm256_mul_pd(constant, w1));
    alpha = _mm256_add_pd(alpha, _mm256_mul_pd(constant, w2));
    alpha = _mm256_add_pd(alpha, _mm256_mul_pd(constant, w3));
    __m256d scale = _mm256_div_pd(constant, alpha);
    __m128i p0 = _mm_loadu_si128((const __m128i *)samples[0]);
    __m128i p1 = _mm_loadu_si128((const __m128i *)samples[1]);
    __m128i p2 = _mm_loadu_si128((const __m128i *)samples[2]);
    __m128i p3 = _mm_loadu_si128((const __m128i *)samples[3]);
    __m128i mask = _mm_set1_epi32(255);
    __m128i packed = _mm_set1_epi32((int)0xff000000u);
    for (int channel = 0; channel < 3; channel++)
    {
        __m128i shift = _mm_cvtsi32_si128(16 - channel * 8);
        __m256d value = _mm256_mul_pd(
            _mm256_cvtepi32_pd(_mm_and_si128(_mm_srl_epi32(p0, shift), mask)),
            w0);
        value = _mm256_add_pd(
            value, _mm256_mul_pd(_mm256_cvtepi32_pd(_mm_and_si128(
                                     _mm_srl_epi32(p1, shift), mask)),
                                 w1));
        value = _mm256_add_pd(
            value, _mm256_mul_pd(_mm256_cvtepi32_pd(_mm_and_si128(
                                     _mm_srl_epi32(p2, shift), mask)),
                                 w2));
        value = _mm256_add_pd(
            value, _mm256_mul_pd(_mm256_cvtepi32_pd(_mm_and_si128(
                                     _mm_srl_epi32(p3, shift), mask)),
                                 w3));
        value = _mm256_min_pd(_mm256_mul_pd(value, scale), constant);
        __m128i rounded =
            _mm256_cvttpd_epi32(_mm256_add_pd(value, _mm256_set1_pd(0.5)));
        packed = _mm_or_si128(
            packed, _mm_sll_epi32(rounded, _mm_cvtsi32_si128(channel * 8)));
    }

    /* Valid bilinear weights round opaque alpha to 255; no alpha stores or
     * conversions per lane are needed, but normalization above is retained. */
    _mm_storeu_si128((__m128i *)out, packed);
}

#define THUMBNAIL_OPAQUE_BATCH
#define THUMBNAIL_ATTRIBUTE __attribute__((target("avx2,no-fma")))
#define THUMBNAIL_KERNEL thumbnail_resample_opaque_avx2
#define THUMBNAIL_PIXEL thumbnail_pixel_scalar
#include "thumbnail_resample_loop.h"
#undef THUMBNAIL_ATTRIBUTE
#undef THUMBNAIL_KERNEL
#undef THUMBNAIL_PIXEL
#undef THUMBNAIL_OPAQUE_BATCH

#define THUMBNAIL_ATTRIBUTE __attribute__((target("avx2,no-fma")))
#define THUMBNAIL_KERNEL thumbnail_resample_avx2
#define THUMBNAIL_PIXEL thumbnail_pixel_avx2
#include "thumbnail_resample_loop.h"
#undef THUMBNAIL_ATTRIBUTE
#undef THUMBNAIL_KERNEL
#undef THUMBNAIL_PIXEL
#endif

static inline int thumbnail_avx2_available(void)
{
#if defined(THUMBNAIL_HAS_AVX2)
    return __builtin_cpu_supports("avx2") != 0;
#else
    return 0;
#endif
}

static void thumbnail_resample_dispatch(const uint32_t *mapping, int bw, int bh,
                                        unsigned transform, int opaque,
                                        unsigned char *rgba, int width,
                                        int height)
{
#if defined(THUMBNAIL_HAS_AVX2)
    if (thumbnail_avx2_available())
    {
        if (opaque)
        {
            thumbnail_resample_opaque_avx2(mapping, bw, bh, transform, opaque,
                                           rgba, width, height);
        }

        else
        {
            thumbnail_resample_avx2(mapping, bw, bh, transform, opaque, rgba,
                                    width, height);
        }

        return;
    }

#endif
    thumbnail_resample_scalar(mapping, bw, bh, transform, opaque, rgba, width,
                              height);
}

#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC pop_options
#endif
#endif
