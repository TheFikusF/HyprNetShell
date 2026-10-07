#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hypr_layer hypr_layer;

hypr_layer* hypr_layer_create(int reserved_height);
void hypr_layer_destroy(hypr_layer* layer);

int hypr_layer_poll_events(hypr_layer* layer);
int hypr_layer_should_close(const hypr_layer* layer);
int hypr_layer_has_error(const hypr_layer* layer);

uint64_t hypr_layer_get_topology_serial(const hypr_layer* layer);
int hypr_layer_get_bar_count(const hypr_layer* layer);
uint64_t hypr_layer_get_bar_id(const hypr_layer* layer, int index);
int hypr_layer_get_bar_width(const hypr_layer* layer, uint64_t id);
int hypr_layer_get_bar_height(const hypr_layer* layer, uint64_t id);
int hypr_layer_get_output_name(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);

int hypr_layer_make_current(hypr_layer* layer, uint64_t id);
int hypr_layer_swap_buffers(hypr_layer* layer, uint64_t id);
int hypr_layer_set_input_regions(
    hypr_layer* layer,
    uint64_t id,
    const int* rectangles,
    int rectangle_count);
int hypr_layer_set_keyboard_interactive_bar(hypr_layer* layer, uint64_t id);
int hypr_layer_set_screenshot_overlay(hypr_layer* layer, uint64_t id);
int hypr_layer_make_screenshot_current(hypr_layer* layer, uint64_t id);
int hypr_layer_swap_screenshot_buffers(hypr_layer* layer, uint64_t id);
int hypr_layer_capture_output(hypr_layer* layer, uint64_t id);
int hypr_layer_get_capture_width(const hypr_layer* layer);
int hypr_layer_get_capture_height(const hypr_layer* layer);
int hypr_layer_get_capture_stride(const hypr_layer* layer);
int hypr_layer_copy_capture(const hypr_layer* layer, unsigned char* buffer, int buffer_size);
/* Thumbnail APIs are single-threaded: call on the poll_events thread. IDs are
 * nonzero, never reused within a layer, and valid only while listed. Metadata
 * is committed on toplevel.done; addresses arrive independently via optional
 * hyprland-toplevel-mapping-v1 version 1. Address changes also bump the serial.
 * No title/app_id-based address inference. */
uint64_t hypr_layer_get_window_serial(const hypr_layer* layer);
int hypr_layer_get_window_count(const hypr_layer* layer);
uint64_t hypr_layer_get_window_id(const hypr_layer* layer, int index);
/* String getters return required bytes INCLUDING NUL; 0 means invalid ID.
 * A short/NULL buffer is not written. address is empty without exact mapping.
 * id=0 is allowed only for get_thumbnail_error (backend availability error). */
int hypr_layer_get_window_title(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);
int hypr_layer_get_window_app_id(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);
int hypr_layer_get_window_identifier(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);
/* Exact protocol-supplied 64-bit address as lowercase 0x-prefixed hex, without
 * leading zero padding. Empty while pending, absent, failed, or global removed.
 * Mapping is independent of thumbnail enable/visibility. */
int hypr_layer_get_window_address(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);
int hypr_layer_get_thumbnail_error(const hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);
int hypr_layer_thumbnails_available(const hypr_layer* layer);
/* enabled is a master switch; visible is caller-controlled per-window demand.
 * Turning either off immediately frees that window's capture and cached pixels.
 * Terminal failures require off/on to retry. No capture fallback is used.
 * At most one frame per window is in flight, with >=67ms after ready before
 * recapture (~15fps ceiling). Up to two session/frame starts per poll are
 * scheduled round-robin; heavy demand may lower individual capture rates. */
int hypr_layer_set_thumbnails_enabled(hypr_layer* layer, int enabled);
int hypr_layer_set_window_thumbnail_visible(hypr_layer* layer, uint64_t id, int visible);
/* info returns 1 only when pixels exist. All outputs are optional. RGBA8,
 * R,G,B,A byte order, tightly packed, top-left origin, transform normalized.
 * Complete window, aspect-preserved (integer rounding), at most 640x360;
 * never upscaled or cropped. The protocol SHM buffer remains full-size.
 * Bilinear filtering is in premultiplied space; published RGB is straight
 * (unpremultiplied) alpha. Zero-alpha RGB is zero; XRGB alpha is 255.
 * Native pixels are reused between frames; copy into caller-owned storage.
 * revision increases on successful frames, including across off/on cycles.
 * copy returns bytes copied, or 0 if absent, too small, or revision changed.
 * expected_revision=0 accepts the current frame. No API dispatches events. */
int hypr_layer_get_window_thumbnail_info(const hypr_layer* layer, uint64_t id,
    uint64_t* revision, int* width, int* height, int* stride);
int hypr_layer_copy_window_thumbnail(const hypr_layer* layer, uint64_t id,
    uint64_t expected_revision, unsigned char* buffer, int buffer_size);

int hypr_layer_set_clipboard(
    hypr_layer* layer,
    const unsigned char* data,
    int data_length,
    const char* mime_type);

double hypr_layer_get_pointer_x(const hypr_layer* layer, uint64_t id);
double hypr_layer_get_pointer_y(const hypr_layer* layer, uint64_t id);
int hypr_layer_pointer_inside(const hypr_layer* layer, uint64_t id);
int hypr_layer_pointer_button(const hypr_layer* layer, uint64_t id);
double hypr_layer_take_scroll(hypr_layer* layer, uint64_t id);
int hypr_layer_take_key(hypr_layer* layer, uint64_t id);
int hypr_layer_take_key_control(hypr_layer* layer, uint64_t id);
int hypr_layer_take_text(hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);

void* hypr_layer_get_proc_address(const char* name);

#ifdef __cplusplus
}
#endif
