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
int hypr_layer_take_text(hypr_layer* layer, uint64_t id, char* buffer, int buffer_size);

void* hypr_layer_get_proc_address(const char* name);

#ifdef __cplusplus
}
#endif
