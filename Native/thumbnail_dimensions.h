#ifndef HYPR_THUMBNAIL_DIMENSIONS_H
#define HYPR_THUMBNAIL_DIMENSIONS_H
#include <stdint.h>

enum
{
    THUMBNAIL_MAX_WIDTH = 640,
    THUMBNAIL_MAX_HEIGHT = 360
};

/* Positive source and request dimensions; nearest integer aspect fit, no
 * upscale. */
static inline void thumbnail_dimensions(int source_width, int source_height,
                                        int bound_width, int bound_height,
                                        int *width, int *height)
{
    if (bound_width > THUMBNAIL_MAX_WIDTH)
    {
        bound_width = THUMBNAIL_MAX_WIDTH;
    }

    if (bound_height > THUMBNAIL_MAX_HEIGHT)
    {
        bound_height = THUMBNAIL_MAX_HEIGHT;
    }

    *width = source_width;
    *height = source_height;
    if (source_width <= bound_width && source_height <= bound_height)
    {
        return;
    }

    if ((int64_t)source_width * bound_height >=
        (int64_t)source_height * bound_width)
    {
        *width = bound_width;
        *height = (int)(((int64_t)source_height * *width + source_width / 2) /
                        source_width);
    }

    else
    {
        *height = bound_height;
        *width = (int)(((int64_t)source_width * *height + source_height / 2) /
                       source_height);
    }

    if (*width < 1)
    {
        *width = 1;
    }

    if (*height < 1)
    {
        *height = 1;
    }
}

#endif
