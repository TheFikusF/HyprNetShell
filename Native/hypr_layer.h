#pragma once

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hypr_layer_window hypr_layer_window;

hypr_layer_window* hypr_layer_create_top_bar(int reserved_height);
void hypr_layer_destroy(hypr_layer_window* window);

void hypr_layer_make_current(hypr_layer_window* window);
void hypr_layer_swap_buffers(hypr_layer_window* window);
void hypr_layer_poll_events(hypr_layer_window* window);
void hypr_layer_set_input_regions(hypr_layer_window* window, const int* rectangles, int rectangle_count);

int hypr_layer_get_width(hypr_layer_window* window);
int hypr_layer_get_height(hypr_layer_window* window);
double hypr_layer_get_pointer_x(hypr_layer_window* window);
double hypr_layer_get_pointer_y(hypr_layer_window* window);
int hypr_layer_pointer_inside(hypr_layer_window* window);
int hypr_layer_pointer_button_down(hypr_layer_window* window);
int hypr_layer_should_close(hypr_layer_window* window);
int hypr_layer_has_error(hypr_layer_window* window);

void* hypr_layer_get_proc_address(const char* name);

#ifdef __cplusplus
}
#endif
