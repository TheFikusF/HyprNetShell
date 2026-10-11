/* Intentionally included for each kernel: shared coordinate/cache traversal.
 * Transform values are the stable wl_output transform enum (0 through 7).
 * Caller supplies positive source/output dimensions, at most 640x360 output. */
THUMBNAIL_ATTRIBUTE static void THUMBNAIL_KERNEL(const uint32_t *mapping, int bw, int bh,
                                                 unsigned transform, int opaque, unsigned char *rgba,
                                                 int width, int height)
{
#if defined(__clang__)
#pragma clang fp contract(off)
#endif
    int source_width = (transform & 1) ? bh : bw;
    int source_height = (transform & 1) ? bw : bh;
    ptrdiff_t origin = 0, x_step = 1, y_step = bw;
    /* Upright coordinates map to origin + x*x_step + y*y_step. Resolve the
     * transform once, not four times per output pixel, including mirrored axes. */
    switch (transform)
    {
    case 1:
        origin = (ptrdiff_t)(bh - 1) * bw;
        x_step = -bw;
        y_step = 1;
        break;
    case 2:
        origin = (ptrdiff_t)bh * bw - 1;
        x_step = -1;
        y_step = -bw;
        break;
    case 3:
        origin = bw - 1;
        x_step = bw;
        y_step = -1;
        break;
    case 4:
        origin = bw - 1;
        x_step = -1;
        break;
    case 5:
        x_step = bw;
        y_step = 1;
        break;
    case 6:
        origin = (ptrdiff_t)(bh - 1) * bw;
        y_step = -bw;
        break;
    case 7:
        origin = (ptrdiff_t)bh * bw - 1;
        x_step = -bw;
        y_step = -1;
        break;
    default:
        break;
    }

    const uint32_t *pixels = mapping + origin;
    struct
    {
        ptrdiff_t offset0, offset1;
        double tx;
    } columns[640];
    for (int x = 0; x < width; x++)
    {
        double fx = (x + 0.5) * source_width / width - 0.5;
        if (fx < 0)
        {
            fx = 0;
        }

        int x0 = (int)fx, x1 = x0 + 1 < source_width ? x0 + 1 : x0;
        columns[x].offset0 = x0 * x_step;
        columns[x].offset1 = x1 * x_step;
        columns[x].tx = fx - x0;
    }

    struct
    {
        const uint32_t *row0, *row1;
        double ty;
    } rows[360];
    for (int y = 0; y < height; y++)
    {
        double fy = (y + 0.5) * source_height / height - 0.5;
        if (fy < 0)
        {
            fy = 0;
        }

        int y0 = (int)fy, y1 = y0 + 1 < source_height ? y0 + 1 : y0;
        rows[y].row0 = pixels + y0 * y_step;
        rows[y].row1 = pixels + y1 * y_step;
        rows[y].ty = fy - y0;
    }

    /* Rotated rows jump across SHM pages. Narrow output stripes reuse nearby
     * input cache lines/pages without making output writes a full-image transpose. */
    int stripe_width = (transform & 1) ? 16 : width;
    for (int start_x = 0; start_x < width; start_x += stripe_width)
    {
        int end_x = start_x + stripe_width < width ? start_x + stripe_width : width;
        for (int y = 0; y < height; y++)
        {
            double ty = rows[y].ty;
            const uint32_t *row0 = rows[y].row0;
            const uint32_t *row1 = rows[y].row1;
            unsigned char *out = rgba + ((size_t)y * width + start_x) * 4;
            int x = start_x;
#if defined(THUMBNAIL_OPAQUE_BATCH)
            for (; x + 4 <= end_x; x += 4, out += 16)
            {
                uint32_t samples[4][4];
                double weights[4][4];
                for (int lane = 0; lane < 4; lane++)
                {
                    ptrdiff_t offset0 = columns[x + lane].offset0;
                    ptrdiff_t offset1 = columns[x + lane].offset1;
                    samples[0][lane] = row0[offset0];
                    samples[1][lane] = row0[offset1];
                    samples[2][lane] = row1[offset0];
                    samples[3][lane] = row1[offset1];
                    double tx = columns[x + lane].tx;
                    weights[0][lane] = (1 - tx) * (1 - ty);
                    weights[1][lane] = tx * (1 - ty);
                    weights[2][lane] = (1 - tx) * ty;
                    weights[3][lane] = tx * ty;
                }

                thumbnail_pixels_opaque_avx2(samples, weights, out);
            }

#endif
            for (; x < end_x; x++, out += 4)
            {
                ptrdiff_t offset0 = columns[x].offset0, offset1 = columns[x].offset1;
                uint32_t p0 = row0[offset0], p1 = row0[offset1], p2 = row1[offset0], p3 = row1[offset1];
                double tx = columns[x].tx;
                double w0 = (1 - tx) * (1 - ty), w1 = tx * (1 - ty), w2 = (1 - tx) * ty, w3 = tx * ty;
                THUMBNAIL_PIXEL(p0, p1, p2, p3, w0, w1, w2, w3, opaque, out);
            }
        }
    }
}
