#include "hypr_layer.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <xkbcommon/xkbcommon.h>

#include "wlr-layer-shell-unstable-v1-client-protocol.h"

struct hypr_layer_window {
    struct wl_display* display;
    struct wl_registry* registry;
    struct wl_compositor* compositor;
    struct wl_seat* seat;
    struct wl_pointer* pointer;
    struct wl_keyboard* keyboard;
    struct xkb_context* xkb_context;
    struct xkb_keymap* xkb_keymap;
    struct xkb_state* xkb_state;
    struct zwlr_layer_shell_v1* layer_shell;
    struct wl_surface* surface;
    struct zwlr_layer_surface_v1* layer_surface;
    struct wl_egl_window* egl_window;

    EGLDisplay egl_display;
    EGLConfig egl_config;
    EGLContext egl_context;
    EGLSurface egl_surface;

    int width;
    int height;
    int reserved_height;
    uint32_t layer_shell_version;
    int configured;
    int should_close;
    int has_error;
    double pointer_x;
    double pointer_y;
    int pointer_inside;
    int pointer_button_down;
    int pending_key;
    char pending_text[128];
    int pending_text_length;
    double pending_scroll;
};

static void fail(const char* message) {
    fprintf(stderr, "hypr_layer: %s\n", message);
}

static void fail_egl(struct hypr_layer_window* window, const char* message) {
    EGLint error = eglGetError();
    fprintf(stderr, "hypr_layer: %s (EGL error 0x%04x)\n", message, error);
    if (window != NULL) {
        window->has_error = 1;
        window->should_close = 1;
    }
}

static void destroy_egl_surface(struct hypr_layer_window* window) {
    if (window == NULL || window->egl_display == EGL_NO_DISPLAY || window->egl_display == NULL) {
        return;
    }
    if (window->egl_surface != EGL_NO_SURFACE && window->egl_surface != NULL) {
        eglMakeCurrent(window->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        eglDestroySurface(window->egl_display, window->egl_surface);
        window->egl_surface = EGL_NO_SURFACE;
    }
    if (window->egl_window != NULL) {
        wl_egl_window_destroy(window->egl_window);
        window->egl_window = NULL;
    }
}

static int create_egl_surface(struct hypr_layer_window* window) {
    if (window == NULL || window->surface == NULL) {
        return 0;
    }

    if (window->width <= 0) {
        window->width = 1920;
    }
    if (window->height <= 0) {
        window->height = 1080;
    }

    window->egl_window = wl_egl_window_create(window->surface, window->width, window->height);
    if (window->egl_window == NULL) {
        fail("wl_egl_window_create failed");
        return 0;
    }

    window->egl_surface =
        eglCreateWindowSurface(
            window->egl_display,
            window->egl_config,
            (EGLNativeWindowType)window->egl_window,
            NULL);
    if (window->egl_surface == EGL_NO_SURFACE) {
        fail_egl(window, "eglCreateWindowSurface failed");
        return 0;
    }

    EGLint surface_width = 0;
    EGLint surface_height = 0;
    if (!eglQuerySurface(window->egl_display, window->egl_surface, EGL_WIDTH, &surface_width) ||
        !eglQuerySurface(window->egl_display, window->egl_surface, EGL_HEIGHT, &surface_height)) {
        fail_egl(window, "eglQuerySurface failed after surface creation");
        return 0;
    }

    fprintf(stderr, "hypr_layer: EGL surface created %dx%d (configured %dx%d)\n",
        surface_width,
        surface_height,
        window->width,
        window->height);

    eglSwapInterval(window->egl_display, 1);

    return 1;
}

static void apply_input_regions(struct hypr_layer_window* window, const int* rectangles, int rectangle_count) {
    if (window == NULL || window->compositor == NULL || window->surface == NULL) {
        return;
    }

    struct wl_region* region = wl_compositor_create_region(window->compositor);
    if (region == NULL) {
        fail("wl_compositor_create_region failed");
        window->has_error = 1;
        window->should_close = 1;
        return;
    }

    for (int i = 0; rectangles != NULL && i < rectangle_count; i++) {
        const int* rect = rectangles + i * 4;
        if (rect[2] > 0 && rect[3] > 0) {
            wl_region_add(region, rect[0], rect[1], rect[2], rect[3]);
        }
    }

    wl_surface_set_input_region(window->surface, region);
    wl_region_destroy(region);
}

static void registry_global(
    void* data,
    struct wl_registry* registry,
    uint32_t name,
    const char* interface,
    uint32_t version) {
    struct hypr_layer_window* window = data;

    /*
     * Wayland exposes globals through wl_registry. We bind only the two globals
     * this minimal bar needs: wl_compositor for wl_surface creation and
     * zwlr_layer_shell_v1 for the top-panel role.
     */
    if (strcmp(interface, wl_compositor_interface.name) == 0) {
        uint32_t bind_version = version < 4 ? version : 4;
        window->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, bind_version);
    } else if (strcmp(interface, wl_seat_interface.name) == 0) {
        uint32_t bind_version = version < 5 ? version : 5;
        window->seat = wl_registry_bind(registry, name, &wl_seat_interface, bind_version);
    } else if (strcmp(interface, zwlr_layer_shell_v1_interface.name) == 0) {
        uint32_t bind_version = version < 5 ? version : 5;
        window->layer_shell_version = bind_version;
        window->layer_shell = wl_registry_bind(registry, name, &zwlr_layer_shell_v1_interface, bind_version);
    }
}

static void registry_global_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)data;
    (void)registry;
    (void)name;
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
    (void)surface;
    struct hypr_layer_window* window = data;
    window->pointer_inside = 1;
    window->pointer_x = wl_fixed_to_double(surface_x);
    window->pointer_y = wl_fixed_to_double(surface_y);
}

static void pointer_leave(
    void* data,
    struct wl_pointer* pointer,
    uint32_t serial,
    struct wl_surface* surface) {
    (void)pointer;
    (void)serial;
    (void)surface;
    struct hypr_layer_window* window = data;
    window->pointer_inside = 0;
    window->pointer_button_down = 0;
}

static void pointer_motion(
    void* data,
    struct wl_pointer* pointer,
    uint32_t time,
    wl_fixed_t surface_x,
    wl_fixed_t surface_y) {
    (void)pointer;
    (void)time;
    struct hypr_layer_window* window = data;
    window->pointer_x = wl_fixed_to_double(surface_x);
    window->pointer_y = wl_fixed_to_double(surface_y);
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
    (void)button;
    struct hypr_layer_window* window = data;
    window->pointer_button_down = state == WL_POINTER_BUTTON_STATE_PRESSED;
}

static void pointer_axis(
    void* data,
    struct wl_pointer* pointer,
    uint32_t time,
    uint32_t axis,
    wl_fixed_t value) {
    (void)pointer;
    (void)time;
    struct hypr_layer_window* window = data;
    if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL) {
        window->pending_scroll += wl_fixed_to_double(value);
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
    struct hypr_layer_window* window = data;
    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1 || window->xkb_context == NULL) {
        close(fd);
        return;
    }

    char* keymap_text = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (keymap_text == MAP_FAILED) {
        return;
    }

    struct xkb_keymap* keymap = xkb_keymap_new_from_string(
        window->xkb_context,
        keymap_text,
        XKB_KEYMAP_FORMAT_TEXT_V1,
        XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(keymap_text, size);
    if (keymap == NULL) {
        return;
    }

    struct xkb_state* state = xkb_state_new(keymap);
    if (state == NULL) {
        xkb_keymap_unref(keymap);
        return;
    }

    if (window->xkb_state != NULL) {
        xkb_state_unref(window->xkb_state);
    }
    if (window->xkb_keymap != NULL) {
        xkb_keymap_unref(window->xkb_keymap);
    }
    window->xkb_keymap = keymap;
    window->xkb_state = state;
}

static void keyboard_enter(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    struct wl_surface* surface,
    struct wl_array* keys) {
    (void)data;
    (void)keyboard;
    (void)serial;
    (void)surface;
    (void)keys;
}

static void keyboard_leave(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    struct wl_surface* surface) {
    (void)data;
    (void)keyboard;
    (void)serial;
    (void)surface;
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
    if (state == WL_KEYBOARD_KEY_STATE_PRESSED) {
        struct hypr_layer_window* window = data;
        window->pending_key = (int)key;
        if (window->xkb_state != NULL) {
            char text[64];
            int length = xkb_state_key_get_utf8(window->xkb_state, key + 8, text, sizeof(text));
            int remaining = (int)sizeof(window->pending_text) - window->pending_text_length - 1;
            if (length > 0 && length <= remaining && (unsigned char)text[0] >= 0x20 && text[0] != 0x7f) {
                memcpy(window->pending_text + window->pending_text_length, text, (size_t)length);
                window->pending_text_length += length;
                window->pending_text[window->pending_text_length] = '\0';
            }
        }
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
    struct hypr_layer_window* window = data;
    if (window->xkb_state != NULL) {
        xkb_state_update_mask(
            window->xkb_state,
            mods_depressed,
            mods_latched,
            mods_locked,
            0,
            0,
            group);
    }
}

static void keyboard_repeat_info(void* data, struct wl_keyboard* keyboard, int32_t rate, int32_t delay) {
    (void)data;
    (void)keyboard;
    (void)rate;
    (void)delay;
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
    struct hypr_layer_window* window = data;
    int has_pointer = (capabilities & WL_SEAT_CAPABILITY_POINTER) != 0;
    int has_keyboard = (capabilities & WL_SEAT_CAPABILITY_KEYBOARD) != 0;

    if (has_pointer && window->pointer == NULL) {
        window->pointer = wl_seat_get_pointer(seat);
        wl_pointer_add_listener(window->pointer, &pointer_listener, window);
    } else if (!has_pointer && window->pointer != NULL) {
        wl_pointer_release(window->pointer);
        window->pointer = NULL;
        window->pointer_inside = 0;
        window->pointer_button_down = 0;
    }

    if (has_keyboard && window->keyboard == NULL) {
        window->keyboard = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(window->keyboard, &keyboard_listener, window);
    } else if (!has_keyboard && window->keyboard != NULL) {
        wl_keyboard_release(window->keyboard);
        window->keyboard = NULL;
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

static void layer_surface_configure(
    void* data,
    struct zwlr_layer_surface_v1* surface,
    uint32_t serial,
    uint32_t width,
    uint32_t height) {
    struct hypr_layer_window* window = data;

    zwlr_layer_surface_v1_ack_configure(surface, serial);

    /*
     * Layer-shell surfaces must wait for configure before attaching a buffer.
     * With all edges anchored and size 0x0, the compositor chooses the real
     * output dimensions and reports them here.
     */
    window->width = width > 0 ? (int)width : window->width;
    if (window->width <= 0) {
        window->width = 1920;
    }
    window->height = height > 0 ? (int)height : window->height;

    if (window->egl_window != NULL) {
        wl_egl_window_resize(window->egl_window, window->width, window->height, 0, 0);
    }
    window->configured = 1;
}

static void layer_surface_closed(void* data, struct zwlr_layer_surface_v1* surface) {
    (void)surface;
    ((struct hypr_layer_window*)data)->should_close = 1;
}

static const struct zwlr_layer_surface_v1_listener layer_surface_listener = {
    .configure = layer_surface_configure,
    .closed = layer_surface_closed,
};

static int init_egl(struct hypr_layer_window* window) {
    /*
     * EGL owns the OpenGL context, but the native window still comes from
     * Wayland. wl_egl_window wraps the wl_surface so eglCreateWindowSurface can
     * render directly into the layer-shell surface.
     */
    PFNEGLGETPLATFORMDISPLAYEXTPROC get_platform_display =
        (PFNEGLGETPLATFORMDISPLAYEXTPROC)eglGetProcAddress("eglGetPlatformDisplayEXT");

    if (get_platform_display != NULL) {
        window->egl_display = get_platform_display(EGL_PLATFORM_WAYLAND_EXT, window->display, NULL);
    } else {
        window->egl_display = eglGetDisplay((EGLNativeDisplayType)window->display);
    }

    if (window->egl_display == EGL_NO_DISPLAY) {
        fail("eglGetDisplay failed for the Wayland display");
        return 0;
    }

    if (!eglInitialize(window->egl_display, NULL, NULL)) {
        fail("eglInitialize failed");
        return 0;
    }

    if (!eglBindAPI(EGL_OPENGL_API)) {
        fail("eglBindAPI(EGL_OPENGL_API) failed");
        return 0;
    }

    const EGLint config_attribs[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_NONE
    };

    EGLint config_count = 0;
    if (!eglChooseConfig(window->egl_display, config_attribs, &window->egl_config, 1, &config_count) ||
        config_count == 0) {
        fail("eglChooseConfig failed: no RGBA OpenGL window config available");
        return 0;
    }

    const EGLint context_attribs[] = {
        EGL_CONTEXT_MAJOR_VERSION, 3,
        EGL_CONTEXT_MINOR_VERSION, 3,
        EGL_NONE
    };

    window->egl_context =
        eglCreateContext(window->egl_display, window->egl_config, EGL_NO_CONTEXT, context_attribs);
    if (window->egl_context == EGL_NO_CONTEXT) {
        fail("eglCreateContext failed");
        return 0;
    }

    return create_egl_surface(window);
}

hypr_layer_window* hypr_layer_create_top_bar(int reserved_height) {
    if (reserved_height <= 0) {
        fail("reserved height must be positive");
        return NULL;
    }

    struct hypr_layer_window* window = calloc(1, sizeof(struct hypr_layer_window));
    if (window == NULL) {
        fail("out of memory");
        return NULL;
    }

    window->height = 1080;
    window->width = 1920;
    window->reserved_height = reserved_height;
    window->pending_key = -1;
    window->xkb_context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);

    window->display = wl_display_connect(NULL);
    if (window->display == NULL) {
        fail("wl_display_connect failed. Are WAYLAND_DISPLAY and a compositor available?");
        hypr_layer_destroy(window);
        return NULL;
    }

    window->registry = wl_display_get_registry(window->display);
    wl_registry_add_listener(window->registry, &registry_listener, window);
    wl_display_roundtrip(window->display);
    if (window->seat != NULL) {
        wl_seat_add_listener(window->seat, &seat_listener, window);
        wl_display_roundtrip(window->display);
    }

    if (window->compositor == NULL) {
        fail("wl_compositor is not available");
        hypr_layer_destroy(window);
        return NULL;
    }

    if (window->layer_shell == NULL) {
        fail("zwlr_layer_shell_v1 is not available. This must run under a compositor with wlr-layer-shell, such as Hyprland.");
        hypr_layer_destroy(window);
        return NULL;
    }

    window->surface = wl_compositor_create_surface(window->compositor);
    if (window->surface == NULL) {
        fail("wl_compositor_create_surface failed");
        hypr_layer_destroy(window);
        return NULL;
    }

    window->layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        window->layer_shell,
        window->surface,
        NULL,
        ZWLR_LAYER_SHELL_V1_LAYER_TOP,
        "hyprnetshell");
    if (window->layer_surface == NULL) {
        fail("zwlr_layer_shell_v1_get_layer_surface failed");
        hypr_layer_destroy(window);
        return NULL;
    }

    zwlr_layer_surface_v1_add_listener(window->layer_surface, &layer_surface_listener, window);

    /*
     * Anchor all edges and request compositor-chosen dimensions so the EGL
     * surface can draw across the whole output. Keep the exclusive zone at the
     * bar height so tiled windows reserve only the top strip.
     */
    zwlr_layer_surface_v1_set_anchor(
        window->layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_size(window->layer_surface, 0, 0);
    zwlr_layer_surface_v1_set_exclusive_zone(window->layer_surface, reserved_height);
    if (window->layer_shell_version >= ZWLR_LAYER_SURFACE_V1_SET_EXCLUSIVE_EDGE_SINCE_VERSION) {
        zwlr_layer_surface_v1_set_exclusive_edge(
            window->layer_surface,
            ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP);
    }
    zwlr_layer_surface_v1_set_keyboard_interactivity(window->layer_surface, 0);
    apply_input_regions(window, NULL, 0);

    wl_surface_commit(window->surface);

    while (!window->configured && !window->should_close) {
        if (wl_display_dispatch(window->display) == -1) {
            fail("wl_display_dispatch failed while waiting for the initial configure");
            hypr_layer_destroy(window);
            return NULL;
        }
    }

    if (!init_egl(window)) {
        hypr_layer_destroy(window);
        return NULL;
    }

    return window;
}

void hypr_layer_destroy(hypr_layer_window* window) {
    if (window == NULL) {
        return;
    }

    if (window->egl_display != EGL_NO_DISPLAY && window->egl_display != NULL) {
        eglMakeCurrent(window->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        destroy_egl_surface(window);
        if (window->egl_context != EGL_NO_CONTEXT && window->egl_context != NULL) {
            eglDestroyContext(window->egl_display, window->egl_context);
        }
        eglTerminate(window->egl_display);
    }

    if (window->layer_surface != NULL) {
        zwlr_layer_surface_v1_destroy(window->layer_surface);
    }
    if (window->surface != NULL) {
        wl_surface_destroy(window->surface);
    }
    if (window->layer_shell != NULL) {
        zwlr_layer_shell_v1_destroy(window->layer_shell);
    }
    if (window->pointer != NULL) {
        wl_pointer_release(window->pointer);
    }
    if (window->keyboard != NULL) {
        wl_keyboard_release(window->keyboard);
    }
    if (window->seat != NULL) {
        wl_seat_release(window->seat);
    }
    if (window->xkb_state != NULL) {
        xkb_state_unref(window->xkb_state);
    }
    if (window->xkb_keymap != NULL) {
        xkb_keymap_unref(window->xkb_keymap);
    }
    if (window->xkb_context != NULL) {
        xkb_context_unref(window->xkb_context);
    }
    if (window->compositor != NULL) {
        wl_compositor_destroy(window->compositor);
    }
    if (window->registry != NULL) {
        wl_registry_destroy(window->registry);
    }
    if (window->display != NULL) {
        wl_display_disconnect(window->display);
    }

    free(window);
}

void hypr_layer_make_current(hypr_layer_window* window) {
    if (window == NULL) {
        return;
    }
    if (window->egl_surface == EGL_NO_SURFACE || window->egl_surface == NULL) {
        fail("eglMakeCurrent skipped: no EGL surface");
        window->has_error = 1;
        window->should_close = 1;
        return;
    }
    if (!eglMakeCurrent(window->egl_display, window->egl_surface, window->egl_surface, window->egl_context)) {
        fail_egl(window, "eglMakeCurrent failed");
    }
}

void hypr_layer_swap_buffers(hypr_layer_window* window) {
    if (window == NULL) {
        return;
    }
    EGLint surface_width = 0;
    EGLint surface_height = 0;
    if (!eglQuerySurface(window->egl_display, window->egl_surface, EGL_WIDTH, &surface_width) ||
        !eglQuerySurface(window->egl_display, window->egl_surface, EGL_HEIGHT, &surface_height)) {
        fail_egl(window, "eglQuerySurface failed before swap");
        return;
    }
    if (!eglSwapBuffers(window->egl_display, window->egl_surface)) {
        EGLint error = eglGetError();
        fprintf(stderr, "hypr_layer: swap surface was %dx%d, window is %dx%d\n",
            surface_width,
            surface_height,
            window->width,
            window->height);
        fprintf(stderr, "hypr_layer: eglSwapBuffers failed (EGL error 0x%04x)\n", error);

        if (error == EGL_BAD_SURFACE) {
            fprintf(stderr, "hypr_layer: recreating EGL surface after EGL_BAD_SURFACE\n");
            destroy_egl_surface(window);
            if (create_egl_surface(window) &&
                eglMakeCurrent(window->egl_display, window->egl_surface, window->egl_surface, window->egl_context) &&
                eglSwapBuffers(window->egl_display, window->egl_surface)) {
                wl_display_flush(window->display);
                return;
            }

            fprintf(stderr, "hypr_layer: EGL surface recreation did not recover swap\n");
        }

        window->has_error = 1;
        window->should_close = 1;
        return;
    }

    wl_display_flush(window->display);
}

void hypr_layer_poll_events(hypr_layer_window* window) {
    if (window == NULL || window->display == NULL) {
        return;
    }

    wl_display_dispatch_pending(window->display);

    /*
     * Non-blocking event pump: drain pending events, prepare a read, poll the
     * display fd with a zero timeout, then either read available events or
     * cancel the prepared read. This keeps the C# render loop in control.
     */
    while (wl_display_prepare_read(window->display) != 0) {
        wl_display_dispatch_pending(window->display);
    }

    wl_display_flush(window->display);

    struct pollfd pfd = {
        .fd = wl_display_get_fd(window->display),
        .events = POLLIN,
        .revents = 0,
    };

    int ready = poll(&pfd, 1, 0);
    if (ready > 0 && (pfd.revents & POLLIN) != 0) {
        wl_display_read_events(window->display);
        wl_display_dispatch_pending(window->display);
    } else {
        wl_display_cancel_read(window->display);
        if (ready < 0) {
            window->should_close = 1;
        }
    }
}

void hypr_layer_set_input_regions(hypr_layer_window* window, const int* rectangles, int rectangle_count) {
    if (rectangle_count < 0) {
        rectangle_count = 0;
    }

    apply_input_regions(window, rectangles, rectangle_count);
    if (window != NULL && window->surface != NULL) {
        wl_surface_commit(window->surface);
        wl_display_flush(window->display);
    }
}

void hypr_layer_set_keyboard_interactivity(hypr_layer_window* window, int enabled) {
    if (window == NULL || window->layer_surface == NULL) {
        return;
    }

    zwlr_layer_surface_v1_set_keyboard_interactivity(window->layer_surface, enabled ? 1 : 0);
    wl_surface_commit(window->surface);
    wl_display_flush(window->display);
}

int hypr_layer_get_width(hypr_layer_window* window) {
    return window != NULL ? window->width : 0;
}

int hypr_layer_get_height(hypr_layer_window* window) {
    return window != NULL ? window->height : 0;
}

double hypr_layer_get_pointer_x(hypr_layer_window* window) {
    return window != NULL ? window->pointer_x : 0.0;
}

double hypr_layer_get_pointer_y(hypr_layer_window* window) {
    return window != NULL ? window->pointer_y : 0.0;
}

int hypr_layer_pointer_inside(hypr_layer_window* window) {
    return window != NULL && window->pointer_inside;
}

int hypr_layer_pointer_button_down(hypr_layer_window* window) {
    return window != NULL && window->pointer_button_down;
}

int hypr_layer_take_key(hypr_layer_window* window) {
    if (window == NULL) {
        return -1;
    }

    int key = window->pending_key;
    window->pending_key = -1;
    return key;
}

int hypr_layer_take_text(hypr_layer_window* window, char* buffer, int buffer_size) {
    if (window == NULL || buffer == NULL || buffer_size <= 0) {
        return 0;
    }

    int length = window->pending_text_length;
    if (length >= buffer_size) {
        length = buffer_size - 1;
    }
    memcpy(buffer, window->pending_text, (size_t)length);
    buffer[length] = '\0';
    window->pending_text_length = 0;
    window->pending_text[0] = '\0';
    return length;
}

double hypr_layer_take_scroll(hypr_layer_window* window) {
    if (window == NULL) {
        return 0.0;
    }

    double scroll = window->pending_scroll;
    window->pending_scroll = 0.0;
    return scroll;
}

int hypr_layer_should_close(hypr_layer_window* window) {
    return window == NULL || window->should_close;
}

int hypr_layer_has_error(hypr_layer_window* window) {
    return window != NULL && window->has_error;
}

void* hypr_layer_get_proc_address(const char* name) {
    return (void*)eglGetProcAddress(name);
}
