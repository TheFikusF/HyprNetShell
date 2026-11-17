#ifndef HYPR_QR_H
#define HYPR_QR_H

#include <stdint.h>

/* Scan borrows width*height grayscale bytes for this call only. The returned
 * scanner is owned by the caller and must be destroyed, including on no
 * matches. NULL means invalid dimensions or allocation failure. */
void *hypr_qr_scan(const uint8_t *pixels, int width, int height);
void hypr_qr_destroy(void *scanner);
int hypr_qr_count(void *scanner);
/* Returns payload length, 0 for an undecodable/empty code, -1 for invalid
 * input. Output capacity must be at least 8896 bytes. No NUL terminator is
 * required. ECI and data_type describe the payload encoding; no content is
 * logged. */
int hypr_qr_decode(void *scanner, int index, uint8_t *output, int capacity,
                   uint32_t *eci, int *data_type);

#endif
