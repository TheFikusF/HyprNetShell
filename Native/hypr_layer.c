#define _GNU_SOURCE

#include "hypr_layer.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <errno.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <xkbcommon/xkbcommon.h>

#include "wlr-layer-shell-unstable-v1-client-protocol.h"
#include "ext-image-capture-source-v1-client-protocol.h"
#include "ext-image-copy-capture-v1-client-protocol.h"
#include "ext-data-control-v1-client-protocol.h"

struct hypr_bar {
    struct hypr_layer* layer;
    struct hypr_bar* next;
    uint32_t registry_name;
    uint32_t output_version;
    uint64_t id;
    struct wl_output* output;
    char* output_name;
    char* output_description;
    char fallback_output_name[32];
    int mode_width;
    int mode_height;

    struct wl_surface* surface;
    struct zwlr_layer_surface_v1* layer_surface;
    struct wl_egl_window* egl_window;
    EGLSurface egl_surface;
    int width;
    int height;
    int configured;
    int active;
    int closed;
    int keyboard_interactive;

    struct wl_surface* screenshot_surface;
    struct zwlr_layer_surface_v1* screenshot_layer_surface;
    struct wl_egl_window* screenshot_egl_window;
    EGLSurface screenshot_egl_surface;
    int screenshot_width;
    int screenshot_height;
    int screenshot_configured;

    double pointer_x;
    double pointer_y;
    int pointer_inside;
    int pointer_button_down;
    double pending_scroll;
    int pending_key;
    char pending_text[128];
    int pending_text_length;
};

struct clipboard_source {
    struct hypr_layer* layer;
    struct clipboard_source* next;
    struct ext_data_control_source_v1* source;
    unsigned char* data;
    size_t length;
};

struct hypr_layer {
    struct wl_display* display;
    struct wl_registry* registry;
    struct wl_compositor* compositor;
    struct wl_shm* shm;
    struct wl_seat* seat;
    uint32_t seat_registry_name;
    uint32_t seat_version;
    struct wl_pointer* pointer;
    struct wl_keyboard* keyboard;
    struct zwlr_layer_shell_v1* layer_shell;
    uint32_t layer_shell_version;
    struct ext_output_image_capture_source_manager_v1* output_capture_manager;
    struct ext_image_copy_capture_manager_v1* image_capture_manager;
    struct ext_data_control_manager_v1* data_control_manager;
    struct ext_data_control_device_v1* data_control_device;
    struct ext_data_control_offer_v1* incoming_offer;
    struct clipboard_source* clipboard_sources;
    void* capture_data;
    size_t capture_size;
    int capture_width;
    int capture_height;
    int capture_stride;

    struct xkb_context* xkb_context;
    struct xkb_keymap* xkb_keymap;
    struct xkb_state* xkb_state;

    EGLDisplay egl_display;
    EGLConfig egl_config;
    EGLContext egl_context;
    EGLSurface fallback_surface;

    struct hypr_bar* bars;
    struct hypr_bar* pointer_focus;
    struct hypr_bar* keyboard_focus;
    struct hypr_bar* repeat_bar;
    uint64_t keyboard_interactive_id;
    uint64_t next_bar_id;
    uint64_t topology_serial;
    int reserved_height;
    int repeat_active;
    uint32_t repeat_key;
    int repeat_rate;
    int repeat_delay;
    int64_t repeat_next_ms;
    int should_close;
    int has_error;
};

static void create_bar_surface(struct hypr_bar* bar);
static void destroy_bar_surface(struct hypr_bar* bar);
static int create_screenshot_surface(struct hypr_bar* bar);
static void destroy_screenshot_surface(struct hypr_bar* bar);
static void ensure_data_control_device(struct hypr_layer* layer);
static const struct wl_seat_listener seat_listener;

static int64_t monotonic_milliseconds(void) {
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (int64_t)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}

static void fail(const char* message) {
    fprintf(stderr, "hypr_layer: %s\n", message);
}

static void fail_fatal(struct hypr_layer* layer, const char* message) {
    fail(message);
    if (layer != NULL) {
        layer->has_error = 1;
        layer->should_close = 1;
    }
}

static void fail_egl_fatal(struct hypr_layer* layer, const char* message) {
    EGLint error = eglGetError();
    fprintf(stderr, "hypr_layer: %s (EGL error 0x%04x)\n", message, error);
    if (layer != NULL) {
        layer->has_error = 1;
        layer->should_close = 1;
    }
}

static void fail_bar_egl(const struct hypr_bar* bar, const char* message) {
    EGLint error = eglGetError();
    fprintf(
        stderr,
        "hypr_layer: output %llu: %s (EGL error 0x%04x)\n",
        (unsigned long long)(bar != NULL ? bar->id : 0),
        message,
        error);
}

static struct hypr_bar* find_bar(const struct hypr_layer* layer, uint64_t id) {
    if (layer == NULL || id == 0) {
        return NULL;
    }
    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if (bar->id == id && bar->active && !bar->closed) {
            return bar;
        }
    }
    return NULL;
}

static struct hypr_bar* find_bar_by_surface(
    const struct hypr_layer* layer,
    const struct wl_surface* surface) {
    if (layer == NULL || surface == NULL) {
        return NULL;
    }
    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if ((bar->surface == surface || bar->screenshot_surface == surface) && !bar->closed) {
            return bar;
        }
    }
    return NULL;
}

static void clear_bar_focus(struct hypr_bar* bar) {
    struct hypr_layer* layer = bar->layer;
    if (layer->pointer_focus == bar) {
        layer->pointer_focus = NULL;
    }
    if (layer->keyboard_focus == bar) {
        layer->keyboard_focus = NULL;
    }
    if (layer->repeat_bar == bar) {
        layer->repeat_bar = NULL;
        layer->repeat_active = 0;
    }
    if (layer->keyboard_interactive_id == bar->id) {
        layer->keyboard_interactive_id = 0;
    }
    bar->pointer_inside = 0;
    bar->pointer_button_down = 0;
}

static void mark_bar_closed(struct hypr_bar* bar) {
    if (bar == NULL || bar->closed) {
        return;
    }
    bar->closed = 1;
    clear_bar_focus(bar);
    if (bar->active) {
        bar->active = 0;
        bar->layer->topology_serial++;
    }
}

static int make_fallback_current(struct hypr_layer* layer) {
    if (layer == NULL || layer->egl_display == EGL_NO_DISPLAY ||
        layer->egl_context == EGL_NO_CONTEXT || layer->fallback_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglMakeCurrent(
            layer->egl_display,
            layer->fallback_surface,
            layer->fallback_surface,
            layer->egl_context)) {
        fail_egl_fatal(layer, "eglMakeCurrent failed for the fallback surface");
        return 0;
    }
    return 1;
}

static void destroy_bar_egl_surface(struct hypr_bar* bar) {
    if (bar == NULL) {
        return;
    }
    struct hypr_layer* layer = bar->layer;
    if (bar->egl_surface != EGL_NO_SURFACE && layer->egl_display != EGL_NO_DISPLAY) {
        if (!make_fallback_current(layer)) {
            eglMakeCurrent(layer->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
            if (!layer->has_error) {
                fail_fatal(layer, "fallback EGL surface is unavailable during bar teardown");
            }
        }
        eglDestroySurface(layer->egl_display, bar->egl_surface);
        bar->egl_surface = EGL_NO_SURFACE;
    }
    if (bar->egl_window != NULL) {
        wl_egl_window_destroy(bar->egl_window);
        bar->egl_window = NULL;
    }
}

static int create_bar_egl_surface(struct hypr_bar* bar) {
    struct hypr_layer* layer = bar->layer;
    if (bar->surface == NULL || bar->width <= 0 || bar->height <= 0) {
        return 0;
    }

    bar->egl_window = wl_egl_window_create(bar->surface, bar->width, bar->height);
    if (bar->egl_window == NULL) {
        fprintf(stderr, "hypr_layer: output %llu: wl_egl_window_create failed\n", (unsigned long long)bar->id);
        return 0;
    }

    bar->egl_surface = eglCreateWindowSurface(
        layer->egl_display,
        layer->egl_config,
        (EGLNativeWindowType)bar->egl_window,
        NULL);
    if (bar->egl_surface == EGL_NO_SURFACE) {
        fail_bar_egl(bar, "eglCreateWindowSurface failed");
        wl_egl_window_destroy(bar->egl_window);
        bar->egl_window = NULL;
        return 0;
    }

    return 1;
}

static void apply_input_regions(
    struct hypr_bar* bar,
    const int* rectangles,
    int rectangle_count) {
    if (bar == NULL || bar->surface == NULL || bar->layer->compositor == NULL) {
        return;
    }

    struct wl_region* region = wl_compositor_create_region(bar->layer->compositor);
    if (region == NULL) {
        fprintf(stderr, "hypr_layer: output %llu: wl_compositor_create_region failed\n", (unsigned long long)bar->id);
        mark_bar_closed(bar);
        return;
    }

    for (int i = 0; rectangles != NULL && i < rectangle_count; i++) {
        const int* rect = rectangles + i * 4;
        if (rect[2] > 0 && rect[3] > 0) {
            wl_region_add(region, rect[0], rect[1], rect[2], rect[3]);
        }
    }
    wl_surface_set_input_region(bar->surface, region);
    wl_region_destroy(region);
}

static void output_geometry(
    void* data,
    struct wl_output* output,
    int32_t x,
    int32_t y,
    int32_t physical_width,
    int32_t physical_height,
    int32_t subpixel,
    const char* make,
    const char* model,
    int32_t transform) {
    (void)data;
    (void)output;
    (void)x;
    (void)y;
    (void)physical_width;
    (void)physical_height;
    (void)subpixel;
    (void)make;
    (void)model;
    (void)transform;
}

static void output_mode(
    void* data,
    struct wl_output* output,
    uint32_t flags,
    int32_t width,
    int32_t height,
    int32_t refresh) {
    (void)output;
    (void)refresh;
    struct hypr_bar* bar = data;
    if ((flags & WL_OUTPUT_MODE_CURRENT) != 0) {
        bar->mode_width = width;
        bar->mode_height = height;
    }
}

static void output_done(void* data, struct wl_output* output) {
    (void)data;
    (void)output;
}

static void output_scale(void* data, struct wl_output* output, int32_t factor) {
    (void)data;
    (void)output;
    (void)factor;
}

static int replace_string(char** destination, const char* value) {
    if ((*destination == NULL && value == NULL) ||
        (*destination != NULL && value != NULL && strcmp(*destination, value) == 0)) {
        return 0;
    }
    char* replacement = value != NULL ? strdup(value) : NULL;
    if (value != NULL && replacement == NULL) {
        return 0;
    }
    free(*destination);
    *destination = replacement;
    return 1;
}

static void output_name(void* data, struct wl_output* output, const char* name) {
    (void)output;
    struct hypr_bar* bar = data;
    if (replace_string(&bar->output_name, name) && bar->active) {
        bar->layer->topology_serial++;
    }
}

static void output_description(void* data, struct wl_output* output, const char* description) {
    (void)output;
    struct hypr_bar* bar = data;
    replace_string(&bar->output_description, description);
}

static const struct wl_output_listener output_listener = {
    .geometry = output_geometry,
    .mode = output_mode,
    .done = output_done,
    .scale = output_scale,
    .name = output_name,
    .description = output_description,
};

static void layer_surface_configure(
    void* data,
    struct zwlr_layer_surface_v1* layer_surface,
    uint32_t serial,
    uint32_t width,
    uint32_t height) {
    struct hypr_bar* bar = data;
    zwlr_layer_surface_v1_ack_configure(layer_surface, serial);
    if (bar->closed) {
        return;
    }

    int previous_width = bar->width;
    int previous_height = bar->height;
    if (width > 0) {
        bar->width = (int)width;
    } else if (bar->mode_width > 0) {
        bar->width = bar->mode_width;
    } else if (bar->width <= 0) {
        bar->width = 1920;
    }
    if (height > 0) {
        bar->height = (int)height;
    } else if (bar->mode_height > 0) {
        bar->height = bar->mode_height;
    } else if (bar->height <= 0) {
        bar->height = 1080;
    }
    if (bar->width <= 0 || bar->height <= 0) {
        fprintf(stderr, "hypr_layer: output %llu: compositor configured an invalid size\n", (unsigned long long)bar->id);
        mark_bar_closed(bar);
        return;
    }

    if (bar->egl_window != NULL) {
        wl_egl_window_resize(bar->egl_window, bar->width, bar->height, 0, 0);
    } else if (!create_bar_egl_surface(bar)) {
        mark_bar_closed(bar);
        return;
    }

    bar->configured = 1;
    if (!bar->active) {
        bar->active = 1;
        bar->layer->topology_serial++;
    } else if (bar->width != previous_width || bar->height != previous_height) {
        bar->layer->topology_serial++;
    }
}

static void layer_surface_closed(void* data, struct zwlr_layer_surface_v1* layer_surface) {
    (void)layer_surface;
    mark_bar_closed(data);
}

static const struct zwlr_layer_surface_v1_listener layer_surface_listener = {
    .configure = layer_surface_configure,
    .closed = layer_surface_closed,
};

static void screenshot_surface_configure(
    void* data,
    struct zwlr_layer_surface_v1* layer_surface,
    uint32_t serial,
    uint32_t width,
    uint32_t height) {
    struct hypr_bar* bar = data;
    zwlr_layer_surface_v1_ack_configure(layer_surface, serial);
    if (bar->screenshot_surface == NULL || width == 0 || height == 0 ||
        width > INT32_MAX || height > INT32_MAX) {
        destroy_screenshot_surface(bar);
        return;
    }

    int dimensions_changed = bar->screenshot_width != (int)width || bar->screenshot_height != (int)height;
    bar->screenshot_width = (int)width;
    bar->screenshot_height = (int)height;
    if (bar->screenshot_egl_window == NULL) {
        bar->screenshot_egl_window = wl_egl_window_create(
            bar->screenshot_surface,
            bar->screenshot_width,
            bar->screenshot_height);
        if (bar->screenshot_egl_window == NULL) {
            destroy_screenshot_surface(bar);
            return;
        }
        bar->screenshot_egl_surface = eglCreateWindowSurface(
            bar->layer->egl_display,
            bar->layer->egl_config,
            (EGLNativeWindowType)bar->screenshot_egl_window,
            NULL);
        if (bar->screenshot_egl_surface == EGL_NO_SURFACE) {
            fail_bar_egl(bar, "failed to create screenshot overlay EGL surface");
            destroy_screenshot_surface(bar);
            return;
        }
    } else if (dimensions_changed) {
        wl_egl_window_resize(
            bar->screenshot_egl_window,
            bar->screenshot_width,
            bar->screenshot_height,
            0,
            0);
    }
    bar->screenshot_configured = 1;
}

static void screenshot_surface_closed(
    void* data,
    struct zwlr_layer_surface_v1* layer_surface) {
    (void)layer_surface;
    destroy_screenshot_surface(data);
}

static const struct zwlr_layer_surface_v1_listener screenshot_surface_listener = {
    .configure = screenshot_surface_configure,
    .closed = screenshot_surface_closed,
};

static void destroy_screenshot_surface(struct hypr_bar* bar) {
    if (bar == NULL ||
        (bar->screenshot_surface == NULL &&
         bar->screenshot_layer_surface == NULL &&
         bar->screenshot_egl_window == NULL &&
         bar->screenshot_egl_surface == EGL_NO_SURFACE)) {
        return;
    }
    if (bar->layer->pointer_focus == bar || bar->layer->keyboard_focus == bar) {
        clear_bar_focus(bar);
    }
    if (bar->screenshot_egl_surface != EGL_NO_SURFACE) {
        make_fallback_current(bar->layer);
        eglDestroySurface(bar->layer->egl_display, bar->screenshot_egl_surface);
        bar->screenshot_egl_surface = EGL_NO_SURFACE;
    }
    if (bar->screenshot_egl_window != NULL) {
        wl_egl_window_destroy(bar->screenshot_egl_window);
        bar->screenshot_egl_window = NULL;
    }
    if (bar->screenshot_layer_surface != NULL) {
        zwlr_layer_surface_v1_destroy(bar->screenshot_layer_surface);
        bar->screenshot_layer_surface = NULL;
    }
    if (bar->screenshot_surface != NULL) {
        wl_surface_destroy(bar->screenshot_surface);
        bar->screenshot_surface = NULL;
    }
    bar->screenshot_configured = 0;
    bar->screenshot_width = 0;
    bar->screenshot_height = 0;
}

static int create_screenshot_surface(struct hypr_bar* bar) {
    if (bar == NULL || bar->closed || bar->screenshot_surface != NULL) {
        return bar != NULL && bar->screenshot_surface != NULL;
    }
    struct hypr_layer* layer = bar->layer;
    bar->screenshot_surface = wl_compositor_create_surface(layer->compositor);
    if (bar->screenshot_surface == NULL) {
        return 0;
    }
    bar->screenshot_layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer->layer_shell,
        bar->screenshot_surface,
        bar->output,
        ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY,
        "hyprnetshell-screenshot");
    if (bar->screenshot_layer_surface == NULL ||
        zwlr_layer_surface_v1_add_listener(
            bar->screenshot_layer_surface,
            &screenshot_surface_listener,
            bar) < 0) {
        destroy_screenshot_surface(bar);
        return 0;
    }
    zwlr_layer_surface_v1_set_anchor(
        bar->screenshot_layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_size(bar->screenshot_layer_surface, 0, 0);
    zwlr_layer_surface_v1_set_exclusive_zone(bar->screenshot_layer_surface, -1);
    zwlr_layer_surface_v1_set_keyboard_interactivity(bar->screenshot_layer_surface, 1);
    wl_surface_commit(bar->screenshot_surface);
    return 1;
}

static void destroy_bar_surface(struct hypr_bar* bar) {
    if (bar == NULL) {
        return;
    }
    destroy_screenshot_surface(bar);
    clear_bar_focus(bar);
    destroy_bar_egl_surface(bar);
    if (bar->layer_surface != NULL) {
        zwlr_layer_surface_v1_destroy(bar->layer_surface);
        bar->layer_surface = NULL;
    }
    if (bar->surface != NULL) {
        wl_surface_destroy(bar->surface);
        bar->surface = NULL;
    }
    bar->configured = 0;
    bar->active = 0;
}

static void create_bar_surface(struct hypr_bar* bar) {
    struct hypr_layer* layer = bar->layer;
    if (bar->surface != NULL || bar->closed || layer->compositor == NULL ||
        layer->layer_shell == NULL || layer->egl_context == EGL_NO_CONTEXT) {
        return;
    }

    bar->surface = wl_compositor_create_surface(layer->compositor);
    if (bar->surface == NULL) {
        fprintf(stderr, "hypr_layer: output %llu: wl_compositor_create_surface failed\n", (unsigned long long)bar->id);
        mark_bar_closed(bar);
        return;
    }

    bar->layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer->layer_shell,
        bar->surface,
        bar->output,
        ZWLR_LAYER_SHELL_V1_LAYER_TOP,
        "hyprnetshell");
    if (bar->layer_surface == NULL) {
        fprintf(stderr, "hypr_layer: output %llu: get_layer_surface failed\n", (unsigned long long)bar->id);
        wl_surface_destroy(bar->surface);
        bar->surface = NULL;
        mark_bar_closed(bar);
        return;
    }

    if (zwlr_layer_surface_v1_add_listener(bar->layer_surface, &layer_surface_listener, bar) < 0) {
        fprintf(stderr, "hypr_layer: output %llu: layer surface listener registration failed\n", (unsigned long long)bar->id);
        mark_bar_closed(bar);
        destroy_bar_surface(bar);
        return;
    }

    zwlr_layer_surface_v1_set_anchor(
        bar->layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_size(bar->layer_surface, 0, 0);
    zwlr_layer_surface_v1_set_exclusive_zone(bar->layer_surface, layer->reserved_height);
    if (layer->layer_shell_version >= ZWLR_LAYER_SURFACE_V1_SET_EXCLUSIVE_EDGE_SINCE_VERSION) {
        zwlr_layer_surface_v1_set_exclusive_edge(
            bar->layer_surface,
            ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP);
    }
    zwlr_layer_surface_v1_set_keyboard_interactivity(bar->layer_surface, 0);
    apply_input_regions(bar, NULL, 0);
    if (!bar->closed) {
        wl_surface_commit(bar->surface);
    }
}

static void destroy_output(struct hypr_bar* bar) {
    if (bar == NULL) {
        return;
    }
    mark_bar_closed(bar);
    destroy_bar_surface(bar);
    if (bar->output != NULL) {
        if (bar->output_version >= WL_OUTPUT_RELEASE_SINCE_VERSION) {
            wl_output_release(bar->output);
        } else {
            wl_output_destroy(bar->output);
        }
    }
    free(bar->output_name);
    free(bar->output_description);
    free(bar);
}

static void release_pointer(struct hypr_layer* layer) {
    if (layer->pointer == NULL) {
        return;
    }
    if (layer->seat_version >= WL_POINTER_RELEASE_SINCE_VERSION) {
        wl_pointer_release(layer->pointer);
    } else {
        wl_pointer_destroy(layer->pointer);
    }
    layer->pointer = NULL;
    if (layer->pointer_focus != NULL) {
        layer->pointer_focus->pointer_inside = 0;
        layer->pointer_focus->pointer_button_down = 0;
        layer->pointer_focus = NULL;
    }
}

static void release_keyboard(struct hypr_layer* layer) {
    if (layer->keyboard == NULL) {
        return;
    }
    if (layer->seat_version >= WL_KEYBOARD_RELEASE_SINCE_VERSION) {
        wl_keyboard_release(layer->keyboard);
    } else {
        wl_keyboard_destroy(layer->keyboard);
    }
    layer->keyboard = NULL;
    layer->keyboard_focus = NULL;
    layer->repeat_bar = NULL;
    layer->repeat_active = 0;
}

static void clipboard_source_send(
    void* data,
    struct ext_data_control_source_v1* source,
    const char* mime_type,
    int32_t fd) {
    (void)source;
    (void)mime_type;
    struct clipboard_source* clipboard = data;
    size_t offset = 0;
    while (offset < clipboard->length) {
        ssize_t written = write(fd, clipboard->data + offset, clipboard->length - offset);
        if (written > 0) {
            offset += (size_t)written;
        } else if (written < 0 && errno == EINTR) {
            continue;
        } else {
            break;
        }
    }
    close(fd);
}

static void clipboard_source_cancelled(
    void* data,
    struct ext_data_control_source_v1* source) {
    struct clipboard_source* clipboard = data;
    struct clipboard_source** link = &clipboard->layer->clipboard_sources;
    while (*link != NULL && *link != clipboard) {
        link = &(*link)->next;
    }
    if (*link == clipboard) {
        *link = clipboard->next;
    }
    ext_data_control_source_v1_destroy(source);
    free(clipboard->data);
    free(clipboard);
}

static const struct ext_data_control_source_v1_listener clipboard_source_listener = {
    .send = clipboard_source_send,
    .cancelled = clipboard_source_cancelled,
};

static void data_offer_mime(
    void* data,
    struct ext_data_control_offer_v1* offer,
    const char* mime_type) {
    (void)data;
    (void)offer;
    (void)mime_type;
}

static const struct ext_data_control_offer_v1_listener data_offer_listener = {
    .offer = data_offer_mime,
};

static void data_device_offer(
    void* data,
    struct ext_data_control_device_v1* device,
    struct ext_data_control_offer_v1* offer) {
    (void)device;
    struct hypr_layer* layer = data;
    if (layer->incoming_offer != NULL) {
        ext_data_control_offer_v1_destroy(layer->incoming_offer);
    }
    layer->incoming_offer = offer;
    ext_data_control_offer_v1_add_listener(offer, &data_offer_listener, layer);
}

static void discard_data_offer(struct hypr_layer* layer, struct ext_data_control_offer_v1* offer) {
    if (offer != NULL) {
        ext_data_control_offer_v1_destroy(offer);
    }
    if (layer->incoming_offer == offer) {
        layer->incoming_offer = NULL;
    }
}

static void data_device_selection(
    void* data,
    struct ext_data_control_device_v1* device,
    struct ext_data_control_offer_v1* offer) {
    (void)device;
    discard_data_offer(data, offer);
}

static void data_device_finished(void* data, struct ext_data_control_device_v1* device) {
    struct hypr_layer* layer = data;
    if (layer->data_control_device == device) {
        ext_data_control_device_v1_destroy(device);
        layer->data_control_device = NULL;
    }
}

static void data_device_primary_selection(
    void* data,
    struct ext_data_control_device_v1* device,
    struct ext_data_control_offer_v1* offer) {
    (void)device;
    discard_data_offer(data, offer);
}

static const struct ext_data_control_device_v1_listener data_device_listener = {
    .data_offer = data_device_offer,
    .selection = data_device_selection,
    .finished = data_device_finished,
    .primary_selection = data_device_primary_selection,
};

static void ensure_data_control_device(struct hypr_layer* layer) {
    if (layer->data_control_device != NULL || layer->data_control_manager == NULL || layer->seat == NULL) {
        return;
    }
    layer->data_control_device = ext_data_control_manager_v1_get_data_device(
        layer->data_control_manager,
        layer->seat);
    if (layer->data_control_device == NULL ||
        ext_data_control_device_v1_add_listener(layer->data_control_device, &data_device_listener, layer) < 0) {
        fail_fatal(layer, "failed to initialize native clipboard control");
    }
}

static void release_seat(struct hypr_layer* layer) {
    if (layer->seat == NULL) {
        return;
    }
    release_pointer(layer);
    release_keyboard(layer);
    if (layer->data_control_device != NULL) {
        ext_data_control_device_v1_destroy(layer->data_control_device);
        layer->data_control_device = NULL;
    }
    if (layer->seat_version >= WL_SEAT_RELEASE_SINCE_VERSION) {
        wl_seat_release(layer->seat);
    } else {
        wl_seat_destroy(layer->seat);
    }
    layer->seat = NULL;
    layer->seat_registry_name = 0;
    layer->seat_version = 0;
}

static void registry_global(
    void* data,
    struct wl_registry* registry,
    uint32_t name,
    const char* interface,
    uint32_t version) {
    struct hypr_layer* layer = data;
    if (strcmp(interface, wl_compositor_interface.name) == 0 && layer->compositor == NULL) {
        uint32_t bind_version = version < 4 ? version : 4;
        layer->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, bind_version);
    } else if (strcmp(interface, wl_seat_interface.name) == 0 && layer->seat == NULL) {
        uint32_t bind_version = version < 5 ? version : 5;
        layer->seat = wl_registry_bind(registry, name, &wl_seat_interface, bind_version);
        if (layer->seat == NULL) {
            fail_fatal(layer, "failed to bind a Wayland seat");
            return;
        }
        layer->seat_registry_name = name;
        layer->seat_version = bind_version;
        if (wl_seat_add_listener(layer->seat, &seat_listener, layer) < 0) {
            fail_fatal(layer, "failed to initialize the Wayland seat");
            release_seat(layer);
        } else {
            ensure_data_control_device(layer);
        }
    } else if (strcmp(interface, wl_shm_interface.name) == 0 && layer->shm == NULL) {
        layer->shm = wl_registry_bind(registry, name, &wl_shm_interface, 1);
    } else if (strcmp(interface, ext_output_image_capture_source_manager_v1_interface.name) == 0 &&
               layer->output_capture_manager == NULL) {
        layer->output_capture_manager = wl_registry_bind(
            registry, name, &ext_output_image_capture_source_manager_v1_interface, 1);
    } else if (strcmp(interface, ext_image_copy_capture_manager_v1_interface.name) == 0 &&
               layer->image_capture_manager == NULL) {
        layer->image_capture_manager = wl_registry_bind(
            registry, name, &ext_image_copy_capture_manager_v1_interface, 1);
    } else if (strcmp(interface, ext_data_control_manager_v1_interface.name) == 0 &&
               layer->data_control_manager == NULL) {
        layer->data_control_manager = wl_registry_bind(
            registry, name, &ext_data_control_manager_v1_interface, 1);
        ensure_data_control_device(layer);
    } else if (strcmp(interface, zwlr_layer_shell_v1_interface.name) == 0 && layer->layer_shell == NULL) {
        uint32_t bind_version = version < 5 ? version : 5;
        layer->layer_shell_version = bind_version;
        layer->layer_shell = wl_registry_bind(
            registry,
            name,
            &zwlr_layer_shell_v1_interface,
            bind_version);
    } else if (strcmp(interface, wl_output_interface.name) == 0) {
        struct hypr_bar* bar = calloc(1, sizeof(struct hypr_bar));
        if (bar == NULL) {
            fail_fatal(layer, "out of memory while tracking an output");
            return;
        }
        uint32_t bind_version = version < 4 ? version : 4;
        bar->layer = layer;
        bar->registry_name = name;
        bar->output_version = bind_version;
        bar->id = layer->next_bar_id++;
        bar->pending_key = -1;
        bar->egl_surface = EGL_NO_SURFACE;
        bar->screenshot_egl_surface = EGL_NO_SURFACE;
        snprintf(bar->fallback_output_name, sizeof(bar->fallback_output_name), "wl-output-%u", name);
        bar->output = wl_registry_bind(registry, name, &wl_output_interface, bind_version);
        if (bar->output == NULL || wl_output_add_listener(bar->output, &output_listener, bar) < 0) {
            fail_fatal(layer, "failed to bind a wl_output");
            if (bar->output != NULL) {
                wl_output_destroy(bar->output);
            }
            free(bar);
            return;
        }
        bar->next = layer->bars;
        layer->bars = bar;
        create_bar_surface(bar);
    }
}

static void registry_global_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)registry;
    struct hypr_layer* layer = data;
    if (layer->seat != NULL && layer->seat_registry_name == name) {
        release_seat(layer);
        return;
    }
    struct hypr_bar** link = &layer->bars;
    while (*link != NULL) {
        struct hypr_bar* bar = *link;
        if (bar->registry_name == name) {
            *link = bar->next;
            destroy_output(bar);
            return;
        }
        link = &bar->next;
    }
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

static void pointer_enter(
    void* data,
    struct wl_pointer* pointer,
    uint32_t serial,
    struct wl_surface* surface,
    wl_fixed_t surface_x,
    wl_fixed_t surface_y) {
    (void)pointer;
    (void)serial;
    struct hypr_layer* layer = data;
    struct hypr_bar* bar = find_bar_by_surface(layer, surface);
    if (layer->pointer_focus != NULL && layer->pointer_focus != bar) {
        layer->pointer_focus->pointer_inside = 0;
        layer->pointer_focus->pointer_button_down = 0;
    }
    layer->pointer_focus = bar;
    if (bar != NULL) {
        bar->pointer_inside = 1;
        bar->pointer_x = wl_fixed_to_double(surface_x);
        bar->pointer_y = wl_fixed_to_double(surface_y);
    }
}

static void pointer_leave(
    void* data,
    struct wl_pointer* pointer,
    uint32_t serial,
    struct wl_surface* surface) {
    (void)pointer;
    (void)serial;
    struct hypr_layer* layer = data;
    struct hypr_bar* bar = find_bar_by_surface(layer, surface);
    if (bar != NULL) {
        bar->pointer_inside = 0;
        bar->pointer_button_down = 0;
    }
    if (layer->pointer_focus == bar) {
        layer->pointer_focus = NULL;
    }
}

static void pointer_motion(
    void* data,
    struct wl_pointer* pointer,
    uint32_t time,
    wl_fixed_t surface_x,
    wl_fixed_t surface_y) {
    (void)pointer;
    (void)time;
    struct hypr_layer* layer = data;
    if (layer->pointer_focus != NULL) {
        layer->pointer_focus->pointer_x = wl_fixed_to_double(surface_x);
        layer->pointer_focus->pointer_y = wl_fixed_to_double(surface_y);
    }
}

static void pointer_button(
    void* data,
    struct wl_pointer* pointer,
    uint32_t serial,
    uint32_t time,
    uint32_t button,
    uint32_t state) {
    (void)pointer;
    (void)serial;
    (void)time;
    struct hypr_layer* layer = data;
    if (button == 0x110 && layer->pointer_focus != NULL) {
        layer->pointer_focus->pointer_button_down = state == WL_POINTER_BUTTON_STATE_PRESSED;
    }
}

static void pointer_axis(
    void* data,
    struct wl_pointer* pointer,
    uint32_t time,
    uint32_t axis,
    wl_fixed_t value) {
    (void)pointer;
    (void)time;
    struct hypr_layer* layer = data;
    if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL && layer->pointer_focus != NULL) {
        layer->pointer_focus->pending_scroll += wl_fixed_to_double(value);
    }
}

static void pointer_frame(void* data, struct wl_pointer* pointer) {
    (void)data;
    (void)pointer;
}

static void pointer_axis_source(void* data, struct wl_pointer* pointer, uint32_t axis_source) {
    (void)data;
    (void)pointer;
    (void)axis_source;
}

static void pointer_axis_stop(void* data, struct wl_pointer* pointer, uint32_t time, uint32_t axis) {
    (void)data;
    (void)pointer;
    (void)time;
    (void)axis;
}

static void pointer_axis_discrete(void* data, struct wl_pointer* pointer, uint32_t axis, int32_t discrete) {
    (void)data;
    (void)pointer;
    (void)axis;
    (void)discrete;
}

static void pointer_axis_value120(void* data, struct wl_pointer* pointer, uint32_t axis, int32_t value120) {
    (void)data;
    (void)pointer;
    (void)axis;
    (void)value120;
}

static void pointer_axis_relative_direction(
    void* data,
    struct wl_pointer* pointer,
    uint32_t axis,
    uint32_t direction) {
    (void)data;
    (void)pointer;
    (void)axis;
    (void)direction;
}

static const struct wl_pointer_listener pointer_listener = {
    .enter = pointer_enter,
    .leave = pointer_leave,
    .motion = pointer_motion,
    .button = pointer_button,
    .axis = pointer_axis,
    .frame = pointer_frame,
    .axis_source = pointer_axis_source,
    .axis_stop = pointer_axis_stop,
    .axis_discrete = pointer_axis_discrete,
    .axis_value120 = pointer_axis_value120,
    .axis_relative_direction = pointer_axis_relative_direction,
};

static void keyboard_keymap(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t format,
    int32_t fd,
    uint32_t size) {
    (void)keyboard;
    struct hypr_layer* layer = data;
    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1 || layer->xkb_context == NULL) {
        close(fd);
        return;
    }

    char* keymap_text = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (keymap_text == MAP_FAILED) {
        fail("mmap failed for the keyboard keymap");
        return;
    }
    struct xkb_keymap* keymap = xkb_keymap_new_from_string(
        layer->xkb_context,
        keymap_text,
        XKB_KEYMAP_FORMAT_TEXT_V1,
        XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(keymap_text, size);
    if (keymap == NULL) {
        fail("xkb keymap compilation failed");
        return;
    }
    struct xkb_state* state = xkb_state_new(keymap);
    if (state == NULL) {
        xkb_keymap_unref(keymap);
        fail("xkb state creation failed");
        return;
    }
    if (layer->xkb_state != NULL) {
        xkb_state_unref(layer->xkb_state);
    }
    if (layer->xkb_keymap != NULL) {
        xkb_keymap_unref(layer->xkb_keymap);
    }
    layer->xkb_keymap = keymap;
    layer->xkb_state = state;
}

static void keyboard_enter(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    struct wl_surface* surface,
    struct wl_array* keys) {
    (void)keyboard;
    (void)serial;
    (void)keys;
    struct hypr_layer* layer = data;
    layer->keyboard_focus = find_bar_by_surface(layer, surface);
}

static void keyboard_leave(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    struct wl_surface* surface) {
    (void)keyboard;
    (void)serial;
    struct hypr_layer* layer = data;
    struct hypr_bar* bar = find_bar_by_surface(layer, surface);
    if (layer->keyboard_focus == bar) {
        layer->keyboard_focus = NULL;
    }
    layer->repeat_bar = NULL;
    layer->repeat_active = 0;
}

static void queue_keyboard_input(struct hypr_layer* layer, struct hypr_bar* bar, uint32_t key) {
    if (bar == NULL || bar->closed) {
        return;
    }
    bar->pending_key = (int)key;
    if (layer->xkb_state == NULL) {
        return;
    }

    char text[64];
    int length = xkb_state_key_get_utf8(layer->xkb_state, key + 8, text, sizeof(text));
    int remaining = (int)sizeof(bar->pending_text) - bar->pending_text_length - 1;
    if (length > 0 && length <= remaining && (unsigned char)text[0] >= 0x20 && text[0] != 0x7f) {
        memcpy(bar->pending_text + bar->pending_text_length, text, (size_t)length);
        bar->pending_text_length += length;
        bar->pending_text[bar->pending_text_length] = '\0';
    }
}

static void keyboard_key(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    uint32_t time,
    uint32_t key,
    uint32_t state) {
    (void)keyboard;
    (void)serial;
    (void)time;
    struct hypr_layer* layer = data;
    if (state == WL_KEYBOARD_KEY_STATE_PRESSED) {
        struct hypr_bar* bar = layer->keyboard_focus;
        queue_keyboard_input(layer, bar, key);
        if (bar != NULL && layer->repeat_rate > 0 && layer->xkb_keymap != NULL &&
            xkb_keymap_key_repeats(layer->xkb_keymap, key + 8)) {
            layer->repeat_bar = bar;
            layer->repeat_key = key;
            layer->repeat_active = 1;
            layer->repeat_next_ms = monotonic_milliseconds() + layer->repeat_delay;
        }
    } else if (layer->repeat_active && layer->repeat_key == key) {
        layer->repeat_bar = NULL;
        layer->repeat_active = 0;
    }
}

static void keyboard_modifiers(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    uint32_t mods_depressed,
    uint32_t mods_latched,
    uint32_t mods_locked,
    uint32_t group) {
    (void)keyboard;
    (void)serial;
    struct hypr_layer* layer = data;
    if (layer->xkb_state != NULL) {
        xkb_state_update_mask(
            layer->xkb_state,
            mods_depressed,
            mods_latched,
            mods_locked,
            0,
            0,
            group);
    }
}

static void keyboard_repeat_info(void* data, struct wl_keyboard* keyboard, int32_t rate, int32_t delay) {
    (void)keyboard;
    struct hypr_layer* layer = data;
    layer->repeat_rate = rate;
    layer->repeat_delay = delay;
    if (rate <= 0) {
        layer->repeat_bar = NULL;
        layer->repeat_active = 0;
    }
}

static const struct wl_keyboard_listener keyboard_listener = {
    .keymap = keyboard_keymap,
    .enter = keyboard_enter,
    .leave = keyboard_leave,
    .key = keyboard_key,
    .modifiers = keyboard_modifiers,
    .repeat_info = keyboard_repeat_info,
};

static void seat_capabilities(void* data, struct wl_seat* seat, uint32_t capabilities) {
    struct hypr_layer* layer = data;
    int has_pointer = (capabilities & WL_SEAT_CAPABILITY_POINTER) != 0;
    int has_keyboard = (capabilities & WL_SEAT_CAPABILITY_KEYBOARD) != 0;

    if (has_pointer && layer->pointer == NULL) {
        layer->pointer = wl_seat_get_pointer(seat);
        if (layer->pointer == NULL || wl_pointer_add_listener(layer->pointer, &pointer_listener, layer) < 0) {
            fail_fatal(layer, "failed to initialize the Wayland pointer");
        }
    } else if (!has_pointer && layer->pointer != NULL) {
        release_pointer(layer);
    }

    if (has_keyboard && layer->keyboard == NULL) {
        layer->keyboard = wl_seat_get_keyboard(seat);
        if (layer->keyboard == NULL || wl_keyboard_add_listener(layer->keyboard, &keyboard_listener, layer) < 0) {
            fail_fatal(layer, "failed to initialize the Wayland keyboard");
        }
    } else if (!has_keyboard && layer->keyboard != NULL) {
        release_keyboard(layer);
    }
}

static void seat_name(void* data, struct wl_seat* seat, const char* name) {
    (void)data;
    (void)seat;
    (void)name;
}

static const struct wl_seat_listener seat_listener = {
    .capabilities = seat_capabilities,
    .name = seat_name,
};

static int init_egl(struct hypr_layer* layer) {
    PFNEGLGETPLATFORMDISPLAYEXTPROC get_platform_display =
        (PFNEGLGETPLATFORMDISPLAYEXTPROC)eglGetProcAddress("eglGetPlatformDisplayEXT");
    if (get_platform_display != NULL) {
        layer->egl_display = get_platform_display(EGL_PLATFORM_WAYLAND_EXT, layer->display, NULL);
    } else {
        layer->egl_display = eglGetDisplay((EGLNativeDisplayType)layer->display);
    }
    if (layer->egl_display == EGL_NO_DISPLAY) {
        fail_egl_fatal(layer, "failed to get an EGL display for Wayland");
        return 0;
    }
    if (!eglInitialize(layer->egl_display, NULL, NULL)) {
        fail_egl_fatal(layer, "eglInitialize failed");
        return 0;
    }
    if (!eglBindAPI(EGL_OPENGL_API)) {
        fail_egl_fatal(layer, "eglBindAPI(EGL_OPENGL_API) failed");
        return 0;
    }

    const EGLint config_attribs[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT | EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_NONE,
    };
    EGLint config_count = 0;
    if (!eglChooseConfig(layer->egl_display, config_attribs, &layer->egl_config, 1, &config_count) ||
        config_count == 0) {
        fail_egl_fatal(layer, "no RGBA OpenGL window/pbuffer EGL config is available");
        return 0;
    }

    const EGLint context_attribs[] = {
        EGL_CONTEXT_MAJOR_VERSION, 3,
        EGL_CONTEXT_MINOR_VERSION, 3,
        EGL_NONE,
    };
    layer->egl_context = eglCreateContext(
        layer->egl_display,
        layer->egl_config,
        EGL_NO_CONTEXT,
        context_attribs);
    if (layer->egl_context == EGL_NO_CONTEXT) {
        fail_egl_fatal(layer, "eglCreateContext failed");
        return 0;
    }

    const EGLint pbuffer_attribs[] = {
        EGL_WIDTH, 1,
        EGL_HEIGHT, 1,
        EGL_NONE,
    };
    layer->fallback_surface = eglCreatePbufferSurface(
        layer->egl_display,
        layer->egl_config,
        pbuffer_attribs);
    if (layer->fallback_surface == EGL_NO_SURFACE) {
        fail_egl_fatal(layer, "eglCreatePbufferSurface failed");
        return 0;
    }
    return make_fallback_current(layer);
}

hypr_layer* hypr_layer_create(int reserved_height) {
    if (reserved_height <= 0) {
        fail("reserved height must be positive");
        return NULL;
    }

    struct hypr_layer* layer = calloc(1, sizeof(struct hypr_layer));
    if (layer == NULL) {
        fail("out of memory");
        return NULL;
    }
    layer->reserved_height = reserved_height;
    layer->next_bar_id = 1;
    layer->topology_serial = 1;
    layer->egl_display = EGL_NO_DISPLAY;
    layer->egl_context = EGL_NO_CONTEXT;
    layer->fallback_surface = EGL_NO_SURFACE;
    layer->xkb_context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    if (layer->xkb_context == NULL) {
        fail("xkb_context_new failed");
        hypr_layer_destroy(layer);
        return NULL;
    }

    layer->display = wl_display_connect(NULL);
    if (layer->display == NULL) {
        fail("wl_display_connect failed. Are WAYLAND_DISPLAY and a compositor available?");
        hypr_layer_destroy(layer);
        return NULL;
    }
    layer->registry = wl_display_get_registry(layer->display);
    if (layer->registry == NULL || wl_registry_add_listener(layer->registry, &registry_listener, layer) < 0) {
        fail("failed to initialize the Wayland registry");
        hypr_layer_destroy(layer);
        return NULL;
    }
    if (wl_display_roundtrip(layer->display) < 0) {
        fail("Wayland registry roundtrip failed");
        hypr_layer_destroy(layer);
        return NULL;
    }
    if (layer->compositor == NULL) {
        fail("wl_compositor is not available");
        hypr_layer_destroy(layer);
        return NULL;
    }
    if (layer->layer_shell == NULL) {
        fail("zwlr_layer_shell_v1 is not available; run under a layer-shell compositor");
        hypr_layer_destroy(layer);
        return NULL;
    }
    if (!init_egl(layer)) {
        hypr_layer_destroy(layer);
        return NULL;
    }

    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        create_bar_surface(bar);
    }
    if (wl_display_roundtrip(layer->display) < 0) {
        fail("Wayland roundtrip failed while configuring output bars");
        hypr_layer_destroy(layer);
        return NULL;
    }
    return layer;
}

struct capture_state {
    uint32_t width;
    uint32_t height;
    uint32_t format;
    int has_format;
    int constraints_done;
    int stopped;
    int frame_done;
    int frame_ready;
};

static void capture_buffer_size(void* data, struct ext_image_copy_capture_session_v1* session, uint32_t width, uint32_t height) {
    (void)session;
    struct capture_state* state = data;
    state->width = width;
    state->height = height;
}

static void capture_shm_format(void* data, struct ext_image_copy_capture_session_v1* session, uint32_t format) {
    (void)session;
    struct capture_state* state = data;
    if (!state->has_format && (format == WL_SHM_FORMAT_ARGB8888 || format == WL_SHM_FORMAT_XRGB8888)) {
        state->format = format;
        state->has_format = 1;
    }
}

static void capture_dmabuf_device(void* data, struct ext_image_copy_capture_session_v1* session, struct wl_array* device) {
    (void)data; (void)session; (void)device;
}

static void capture_dmabuf_format(void* data, struct ext_image_copy_capture_session_v1* session, uint32_t format, struct wl_array* modifiers) {
    (void)data; (void)session; (void)format; (void)modifiers;
}

static void capture_constraints_done(void* data, struct ext_image_copy_capture_session_v1* session) {
    (void)session;
    ((struct capture_state*)data)->constraints_done = 1;
}

static void capture_stopped(void* data, struct ext_image_copy_capture_session_v1* session) {
    (void)session;
    ((struct capture_state*)data)->stopped = 1;
}

static const struct ext_image_copy_capture_session_v1_listener capture_session_listener = {
    .buffer_size = capture_buffer_size,
    .shm_format = capture_shm_format,
    .dmabuf_device = capture_dmabuf_device,
    .dmabuf_format = capture_dmabuf_format,
    .done = capture_constraints_done,
    .stopped = capture_stopped,
};

static void capture_transform(void* data, struct ext_image_copy_capture_frame_v1* frame, uint32_t transform) {
    (void)data; (void)frame; (void)transform;
}
static void capture_damage(void* data, struct ext_image_copy_capture_frame_v1* frame, int32_t x, int32_t y, int32_t width, int32_t height) {
    (void)data; (void)frame; (void)x; (void)y; (void)width; (void)height;
}
static void capture_presentation(void* data, struct ext_image_copy_capture_frame_v1* frame, uint32_t tv_sec_hi, uint32_t tv_sec_lo, uint32_t tv_nsec) {
    (void)data; (void)frame; (void)tv_sec_hi; (void)tv_sec_lo; (void)tv_nsec;
}
static void capture_ready(void* data, struct ext_image_copy_capture_frame_v1* frame) {
    (void)frame;
    struct capture_state* state = data;
    state->frame_ready = 1;
    state->frame_done = 1;
}
static void capture_failed(void* data, struct ext_image_copy_capture_frame_v1* frame, uint32_t reason) {
    (void)frame; (void)reason;
    ((struct capture_state*)data)->frame_done = 1;
}

static const struct ext_image_copy_capture_frame_v1_listener capture_frame_listener = {
    .transform = capture_transform,
    .damage = capture_damage,
    .presentation_time = capture_presentation,
    .ready = capture_ready,
    .failed = capture_failed,
};

int hypr_layer_capture_output(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL || layer->shm == NULL || layer->output_capture_manager == NULL ||
        layer->image_capture_manager == NULL) {
        return 0;
    }

    if (layer->capture_data != NULL) {
        munmap(layer->capture_data, layer->capture_size);
        layer->capture_data = NULL;
        layer->capture_size = 0;
    }

    struct capture_state state = {0};
    struct ext_image_capture_source_v1* source =
        ext_output_image_capture_source_manager_v1_create_source(layer->output_capture_manager, bar->output);
    struct ext_image_copy_capture_session_v1* session = source != NULL
        ? ext_image_copy_capture_manager_v1_create_session(layer->image_capture_manager, source, 0)
        : NULL;
    if (session == NULL ||
        ext_image_copy_capture_session_v1_add_listener(session, &capture_session_listener, &state) < 0) {
        if (session != NULL) ext_image_copy_capture_session_v1_destroy(session);
        if (source != NULL) ext_image_capture_source_v1_destroy(source);
        return 0;
    }

    while (!state.constraints_done && !state.stopped) {
        if (wl_display_roundtrip(layer->display) < 0) {
            state.stopped = 1;
        }
    }
    if (state.stopped || !state.has_format || state.width == 0 || state.height == 0 ||
        state.width > INT32_MAX / 4 || state.height > SIZE_MAX / ((size_t)state.width * 4)) {
        ext_image_copy_capture_session_v1_destroy(session);
        ext_image_capture_source_v1_destroy(source);
        return 0;
    }

    int stride = (int)state.width * 4;
    size_t size = (size_t)stride * state.height;
    if (size > INT32_MAX) {
        ext_image_copy_capture_session_v1_destroy(session);
        ext_image_capture_source_v1_destroy(source);
        return 0;
    }
    int fd = memfd_create("hyprnetshell-capture", MFD_CLOEXEC);
    if (fd < 0 || ftruncate(fd, (off_t)size) < 0) {
        if (fd >= 0) close(fd);
        ext_image_copy_capture_session_v1_destroy(session);
        ext_image_capture_source_v1_destroy(source);
        return 0;
    }
    void* pixels = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    struct wl_shm_pool* pool = pixels != MAP_FAILED ? wl_shm_create_pool(layer->shm, fd, (int)size) : NULL;
    struct wl_buffer* buffer = pool != NULL
        ? wl_shm_pool_create_buffer(pool, 0, (int)state.width, (int)state.height, stride, state.format)
        : NULL;
    struct ext_image_copy_capture_frame_v1* frame = buffer != NULL
        ? ext_image_copy_capture_session_v1_create_frame(session)
        : NULL;
    if (frame == NULL ||
        ext_image_copy_capture_frame_v1_add_listener(frame, &capture_frame_listener, &state) < 0) {
        if (frame != NULL) ext_image_copy_capture_frame_v1_destroy(frame);
        if (buffer != NULL) wl_buffer_destroy(buffer);
        if (pool != NULL) wl_shm_pool_destroy(pool);
        if (pixels != MAP_FAILED) munmap(pixels, size);
        close(fd);
        ext_image_copy_capture_session_v1_destroy(session);
        ext_image_capture_source_v1_destroy(source);
        return 0;
    }

    ext_image_copy_capture_frame_v1_attach_buffer(frame, buffer);
    ext_image_copy_capture_frame_v1_damage_buffer(frame, 0, 0, (int)state.width, (int)state.height);
    ext_image_copy_capture_frame_v1_capture(frame);
    while (!state.frame_done && !state.stopped) {
        if (wl_display_roundtrip(layer->display) < 0) {
            state.stopped = 1;
        }
    }

    ext_image_copy_capture_frame_v1_destroy(frame);
    wl_buffer_destroy(buffer);
    wl_shm_pool_destroy(pool);
    close(fd);
    ext_image_copy_capture_session_v1_destroy(session);
    ext_image_capture_source_v1_destroy(source);
    if (!state.frame_ready) {
        munmap(pixels, size);
        return 0;
    }

    layer->capture_data = pixels;
    layer->capture_size = size;
    layer->capture_width = (int)state.width;
    layer->capture_height = (int)state.height;
    layer->capture_stride = stride;
    return 1;
}

int hypr_layer_get_capture_width(const hypr_layer* layer) {
    return layer != NULL ? layer->capture_width : 0;
}

int hypr_layer_get_capture_height(const hypr_layer* layer) {
    return layer != NULL ? layer->capture_height : 0;
}

int hypr_layer_get_capture_stride(const hypr_layer* layer) {
    return layer != NULL ? layer->capture_stride : 0;
}

int hypr_layer_copy_capture(const hypr_layer* layer, unsigned char* buffer, int buffer_size) {
    if (layer == NULL || layer->capture_data == NULL || buffer == NULL || buffer_size < 0 ||
        (size_t)buffer_size < layer->capture_size || layer->capture_size > INT32_MAX) {
        return 0;
    }
    memcpy(buffer, layer->capture_data, layer->capture_size);
    return (int)layer->capture_size;
}

int hypr_layer_set_clipboard(
    hypr_layer* layer,
    const unsigned char* data,
    int data_length,
    const char* mime_type) {
    if (layer == NULL || data == NULL || data_length <= 0 || mime_type == NULL || mime_type[0] == '\0' ||
        layer->data_control_manager == NULL || layer->data_control_device == NULL) {
        return 0;
    }
    struct clipboard_source* clipboard = calloc(1, sizeof(struct clipboard_source));
    if (clipboard == NULL) {
        return 0;
    }
    clipboard->data = malloc((size_t)data_length);
    clipboard->source = ext_data_control_manager_v1_create_data_source(layer->data_control_manager);
    if (clipboard->data == NULL || clipboard->source == NULL ||
        ext_data_control_source_v1_add_listener(clipboard->source, &clipboard_source_listener, clipboard) < 0) {
        if (clipboard->source != NULL) ext_data_control_source_v1_destroy(clipboard->source);
        free(clipboard->data);
        free(clipboard);
        return 0;
    }
    memcpy(clipboard->data, data, (size_t)data_length);
    clipboard->length = (size_t)data_length;
    clipboard->layer = layer;
    clipboard->next = layer->clipboard_sources;
    layer->clipboard_sources = clipboard;
    ext_data_control_source_v1_offer(clipboard->source, mime_type);
    ext_data_control_device_v1_set_selection(layer->data_control_device, clipboard->source);
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        return 0;
    }
    return 1;
}

void hypr_layer_destroy(hypr_layer* layer) {
    if (layer == NULL) {
        return;
    }

    if (layer->capture_data != NULL) {
        munmap(layer->capture_data, layer->capture_size);
        layer->capture_data = NULL;
    }
    if (layer->egl_display != EGL_NO_DISPLAY) {
        make_fallback_current(layer);
    }
    while (layer->bars != NULL) {
        struct hypr_bar* bar = layer->bars;
        layer->bars = bar->next;
        destroy_output(bar);
    }

    if (layer->egl_display != EGL_NO_DISPLAY) {
        eglMakeCurrent(layer->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (layer->fallback_surface != EGL_NO_SURFACE) {
            eglDestroySurface(layer->egl_display, layer->fallback_surface);
        }
        if (layer->egl_context != EGL_NO_CONTEXT) {
            eglDestroyContext(layer->egl_display, layer->egl_context);
        }
        eglTerminate(layer->egl_display);
    }
    release_seat(layer);
    while (layer->clipboard_sources != NULL) {
        struct clipboard_source* clipboard = layer->clipboard_sources;
        layer->clipboard_sources = clipboard->next;
        if (clipboard->source != NULL) {
            ext_data_control_source_v1_destroy(clipboard->source);
        }
        free(clipboard->data);
        free(clipboard);
    }
    if (layer->data_control_manager != NULL) {
        ext_data_control_manager_v1_destroy(layer->data_control_manager);
    }
    if (layer->image_capture_manager != NULL) {
        ext_image_copy_capture_manager_v1_destroy(layer->image_capture_manager);
    }
    if (layer->output_capture_manager != NULL) {
        ext_output_image_capture_source_manager_v1_destroy(layer->output_capture_manager);
    }
    if (layer->shm != NULL) {
        wl_shm_destroy(layer->shm);
    }
    if (layer->layer_shell != NULL) {
        zwlr_layer_shell_v1_destroy(layer->layer_shell);
    }
    if (layer->xkb_state != NULL) {
        xkb_state_unref(layer->xkb_state);
    }
    if (layer->xkb_keymap != NULL) {
        xkb_keymap_unref(layer->xkb_keymap);
    }
    if (layer->xkb_context != NULL) {
        xkb_context_unref(layer->xkb_context);
    }
    if (layer->compositor != NULL) {
        wl_compositor_destroy(layer->compositor);
    }
    if (layer->registry != NULL) {
        wl_registry_destroy(layer->registry);
    }
    if (layer->display != NULL) {
        wl_display_disconnect(layer->display);
    }
    free(layer);
}

int hypr_layer_poll_events(hypr_layer* layer) {
    if (layer == NULL || layer->display == NULL || layer->should_close) {
        return 0;
    }
    if (wl_display_dispatch_pending(layer->display) < 0) {
        fail_fatal(layer, "wl_display_dispatch_pending failed");
        return 0;
    }
    while (wl_display_prepare_read(layer->display) != 0) {
        if (wl_display_dispatch_pending(layer->display) < 0) {
            fail_fatal(layer, "wl_display_dispatch_pending failed while preparing a read");
            return 0;
        }
    }

    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        wl_display_cancel_read(layer->display);
        fail_fatal(layer, "wl_display_flush failed");
        return 0;
    }
    struct pollfd pfd = {
        .fd = wl_display_get_fd(layer->display),
        .events = POLLIN,
        .revents = 0,
    };
    int ready = poll(&pfd, 1, 0);
    if (ready > 0 && (pfd.revents & POLLIN) != 0) {
        if (wl_display_read_events(layer->display) < 0 ||
            wl_display_dispatch_pending(layer->display) < 0) {
            fail_fatal(layer, "failed to read Wayland events");
            return 0;
        }
    } else {
        wl_display_cancel_read(layer->display);
        if (ready < 0 && errno != EINTR) {
            fail_fatal(layer, "poll failed for the Wayland display");
            return 0;
        }
        if (ready > 0 && (pfd.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            fail_fatal(layer, "the Wayland display connection closed");
            return 0;
        }
    }

    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if (!bar->closed && bar->surface == NULL) {
            create_bar_surface(bar);
        } else if (bar->closed && bar->surface != NULL) {
            destroy_bar_surface(bar);
        }
    }
    if (layer->repeat_active && layer->repeat_rate > 0 && layer->repeat_bar != NULL) {
        int64_t now = monotonic_milliseconds();
        if (now >= layer->repeat_next_ms) {
            queue_keyboard_input(layer, layer->repeat_bar, layer->repeat_key);
            int64_t interval = 1000 / layer->repeat_rate;
            layer->repeat_next_ms = now + (interval > 0 ? interval : 1);
        }
    }
    return 1;
}

int hypr_layer_should_close(const hypr_layer* layer) {
    return layer == NULL || layer->should_close;
}

int hypr_layer_has_error(const hypr_layer* layer) {
    return layer != NULL && layer->has_error;
}

uint64_t hypr_layer_get_topology_serial(const hypr_layer* layer) {
    return layer != NULL ? layer->topology_serial : 0;
}

int hypr_layer_get_bar_count(const hypr_layer* layer) {
    int count = 0;
    if (layer != NULL) {
        for (const struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
            if (bar->active && !bar->closed) {
                count++;
            }
        }
    }
    return count;
}

uint64_t hypr_layer_get_bar_id(const hypr_layer* layer, int index) {
    if (layer == NULL || index < 0) {
        return 0;
    }
    for (const struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if (bar->active && !bar->closed) {
            if (index == 0) {
                return bar->id;
            }
            index--;
        }
    }
    return 0;
}

int hypr_layer_get_bar_width(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL ? bar->width : 0;
}

int hypr_layer_get_bar_height(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL ? bar->height : 0;
}

int hypr_layer_get_output_name(
    const hypr_layer* layer,
    uint64_t id,
    char* buffer,
    int buffer_size) {
    if (buffer == NULL || buffer_size <= 0) {
        return 0;
    }
    buffer[0] = '\0';
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL) {
        return 0;
    }
    const char* value = bar->output_name != NULL && bar->output_name[0] != '\0'
        ? bar->output_name
        : bar->fallback_output_name;
    size_t required_length = strlen(value);
    size_t copied_length = required_length;
    if (copied_length >= (size_t)buffer_size) {
        copied_length = (size_t)buffer_size - 1;
    }
    memcpy(buffer, value, copied_length);
    buffer[copied_length] = '\0';
    return required_length <= INT32_MAX ? (int)required_length : INT32_MAX;
}

int hypr_layer_make_current(hypr_layer* layer, uint64_t id) {
    if (layer == NULL) {
        return 0;
    }
    if (id == 0) {
        return make_fallback_current(layer);
    }
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL || bar->egl_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglMakeCurrent(
            layer->egl_display,
            bar->egl_surface,
            bar->egl_surface,
            layer->egl_context)) {
        fail_bar_egl(bar, "eglMakeCurrent failed");
        mark_bar_closed(bar);
        make_fallback_current(layer);
        return 0;
    }
    return 1;
}

int hypr_layer_swap_buffers(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL || bar->egl_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglSwapBuffers(layer->egl_display, bar->egl_surface)) {
        EGLint error = eglGetError();
        fprintf(stderr, "hypr_layer: output %llu: eglSwapBuffers failed (EGL error 0x%04x)\n", (unsigned long long)id, error);
        if (error == EGL_BAD_SURFACE) {
            destroy_bar_egl_surface(bar);
            if (create_bar_egl_surface(bar) &&
                eglMakeCurrent(layer->egl_display, bar->egl_surface, bar->egl_surface, layer->egl_context) &&
                eglSwapBuffers(layer->egl_display, bar->egl_surface)) {
                wl_display_flush(layer->display);
                return 1;
            }
        }
        mark_bar_closed(bar);
        return 0;
    }
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        fail_fatal(layer, "wl_display_flush failed after swapping buffers");
        return 0;
    }
    return 1;
}

int hypr_layer_set_input_regions(
    hypr_layer* layer,
    uint64_t id,
    const int* rectangles,
    int rectangle_count) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL) {
        return 0;
    }
    apply_input_regions(bar, rectangles, rectangle_count > 0 ? rectangle_count : 0);
    if (bar->closed) {
        return 0;
    }
    wl_surface_commit(bar->surface);
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        fail_fatal(layer, "wl_display_flush failed after setting input regions");
        return 0;
    }
    return 1;
}

int hypr_layer_set_screenshot_overlay(hypr_layer* layer, uint64_t id) {
    if (layer == NULL) {
        return 0;
    }
    struct hypr_bar* target = id != 0 ? find_bar(layer, id) : NULL;
    if (id != 0 && (target == NULL || !target->active || target->closed)) {
        return 0;
    }

    int succeeded = 1;
    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if (bar == target) {
            succeeded = create_screenshot_surface(bar);
        } else {
            destroy_screenshot_surface(bar);
        }
    }
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        fail_fatal(layer, "failed to update the screenshot overlay surface");
        return 0;
    }
    return succeeded;
}

int hypr_layer_make_screenshot_current(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL || !bar->screenshot_configured ||
        bar->screenshot_egl_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglMakeCurrent(
            layer->egl_display,
            bar->screenshot_egl_surface,
            bar->screenshot_egl_surface,
            layer->egl_context)) {
        fail_bar_egl(bar, "eglMakeCurrent failed for screenshot overlay");
        destroy_screenshot_surface(bar);
        return 0;
    }
    return 1;
}

int hypr_layer_swap_screenshot_buffers(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL || !bar->screenshot_configured ||
        bar->screenshot_egl_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglSwapBuffers(layer->egl_display, bar->screenshot_egl_surface)) {
        fail_bar_egl(bar, "eglSwapBuffers failed for screenshot overlay");
        destroy_screenshot_surface(bar);
        return 0;
    }
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        fail_fatal(layer, "wl_display_flush failed after swapping screenshot overlay buffers");
        return 0;
    }
    return 1;
}

int hypr_layer_set_keyboard_interactive_bar(hypr_layer* layer, uint64_t id) {
    if (layer == NULL) {
        return 0;
    }
    if (id != 0 && find_bar(layer, id) == NULL) {
        return 0;
    }
    layer->keyboard_interactive_id = id;
    for (struct hypr_bar* bar = layer->bars; bar != NULL; bar = bar->next) {
        if (!bar->active || bar->closed || bar->layer_surface == NULL) {
            continue;
        }
        int enabled = bar->id == id;
        if (bar->keyboard_interactive == enabled) {
            continue;
        }
        bar->keyboard_interactive = enabled;
        zwlr_layer_surface_v1_set_keyboard_interactivity(bar->layer_surface, enabled ? 1 : 0);
        wl_surface_commit(bar->surface);
    }
    if (id == 0) {
        layer->repeat_bar = NULL;
        layer->repeat_active = 0;
    }
    if (wl_display_flush(layer->display) < 0 && errno != EAGAIN) {
        fail_fatal(layer, "wl_display_flush failed after changing keyboard interactivity");
        return 0;
    }
    return 1;
}

double hypr_layer_get_pointer_x(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL ? bar->pointer_x : 0.0;
}

double hypr_layer_get_pointer_y(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL ? bar->pointer_y : 0.0;
}

int hypr_layer_pointer_inside(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL && bar->pointer_inside;
}

int hypr_layer_pointer_button(const hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    return bar != NULL && bar->pointer_button_down;
}

double hypr_layer_take_scroll(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL) {
        return 0.0;
    }
    double value = bar->pending_scroll;
    bar->pending_scroll = 0.0;
    return value;
}

int hypr_layer_take_key(hypr_layer* layer, uint64_t id) {
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL) {
        return -1;
    }
    int key = bar->pending_key;
    bar->pending_key = -1;
    return key;
}

int hypr_layer_take_text(
    hypr_layer* layer,
    uint64_t id,
    char* buffer,
    int buffer_size) {
    if (buffer == NULL || buffer_size <= 0) {
        return 0;
    }
    buffer[0] = '\0';
    struct hypr_bar* bar = find_bar(layer, id);
    if (bar == NULL) {
        return 0;
    }
    int length = bar->pending_text_length;
    if (length >= buffer_size) {
        length = buffer_size - 1;
    }
    memcpy(buffer, bar->pending_text, (size_t)length);
    buffer[length] = '\0';
    bar->pending_text_length = 0;
    bar->pending_text[0] = '\0';
    return length;
}

void* hypr_layer_get_proc_address(const char* name) {
    return name != NULL ? (void*)eglGetProcAddress(name) : NULL;
}
