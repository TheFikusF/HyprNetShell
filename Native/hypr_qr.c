#include "hypr_qr.h"
#include "quirc.h"
#include <limits.h>
#include <string.h>

void *hypr_qr_scan(const uint8_t *pixels, int width, int height)
{
    if (!pixels || width <= 0 || height <= 0 || width > INT_MAX / height)
    {
        return NULL;
    }

    struct quirc *scanner = quirc_new();
    if (!scanner)
    {
        return NULL;
    }

    if (quirc_resize(scanner, width, height) < 0)
    {
        quirc_destroy(scanner);
        return NULL;
    }

    memcpy(quirc_begin(scanner, NULL, NULL), pixels, (size_t)width * height);
    quirc_end(scanner);
    return scanner;
}

void hypr_qr_destroy(void *scanner)
{
    if (scanner)
    {
        quirc_destroy(scanner);
    }
}

int hypr_qr_count(void *scanner)
{
    return scanner ? quirc_count(scanner) : 0;
}

int hypr_qr_decode(void *scanner, int index, uint8_t *output, int capacity,
                   uint32_t *eci, int *data_type)
{
    if (!scanner || !output || !eci || !data_type ||
        capacity < QUIRC_MAX_PAYLOAD || index < 0 ||
        index >= quirc_count(scanner))
    {
        return -1;
    }

    struct quirc_code code;
    struct quirc_data data;
    quirc_extract(scanner, index, &code);
    quirc_decode_error_t error = quirc_decode(&code, &data);
    if (error != QUIRC_SUCCESS)
    {
        quirc_flip(&code);
        error = quirc_decode(&code, &data);
    }

    if (error != QUIRC_SUCCESS)
    {
        return 0;
    }

    memcpy(output, data.payload, (size_t)data.payload_len);
    *eci = data.eci;
    *data_type = data.data_type;
    return data.payload_len;
}
