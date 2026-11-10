#define _GNU_SOURCE
#include "hypr_wallpaper.h"
#include "wlr-layer-shell-unstable-v1-client-protocol.h"

#include <errno.h>
#include <limits.h>
#include <poll.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>

#define STARTUP_TIMEOUT_MS 2000
#define MAX_BUFFERS 2
#define TRANSITION_MS 2000
#define FRAME_INTERVAL_MS 33

struct wallpaper_output;

struct wallpaper_buffer
{
    struct wallpaper_buffer *next;
    struct wallpaper_output *output;
    struct wl_buffer *proxy;
    void *pixels;
    size_t size;
};

struct wallpaper_output
{
    struct wallpaper_output *next;
    struct hypr_wallpaper *owner;
    struct wl_output *proxy;
    struct wl_surface *surface;
    struct zwlr_layer_surface_v1 *layer;
    struct wallpaper_buffer *buffers;
    uint32_t *source;
    uint32_t *target;
    int cache_width;
    int cache_height;
    unsigned int displayed_weight;
    int64_t transition_start;
    int64_t next_frame;
    bool transitioning;
    uint32_t name;
    uint32_t width;
    uint32_t height;
    int scale;
    int buffer_count;
    bool configured;
    bool dirty;
    bool closed;
};

struct hypr_wallpaper
{
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_compositor *compositor;
    struct wl_shm *shm;
    struct zwlr_layer_shell_v1 *shell;
    struct wallpaper_output *outputs;
    unsigned char *image;
    int image_width;
    int image_height;
    uint32_t compositor_name;
    uint32_t shm_name;
    uint32_t shell_name;
    bool failed;
    char error[512];
};

static _Thread_local char create_error[512];

static int fail(struct hypr_wallpaper *handle, const char *format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(handle->error, sizeof(handle->error), format, args);
    va_end(args);
    handle->failed = true;
    return 0;
}

static int connection_error(struct hypr_wallpaper *handle, const char *operation)
{
    int error = wl_display_get_error(handle->display);
    if (error == EPROTO)
    {
        const struct wl_interface *interface = NULL;
        uint32_t id = 0;
        uint32_t code = wl_display_get_protocol_error(handle->display, &interface, &id);
        return fail(handle, "%s: Wayland protocol error %u on %s@%u", operation, code,
                    interface ? interface->name : "unknown", id);
    }

    return fail(handle, "%s: %s", operation, strerror(error ? error : errno));
}

static void destroy_buffer(struct wallpaper_buffer *buffer)
{
    wl_buffer_destroy(buffer->proxy);
    munmap(buffer->pixels, buffer->size);
    free(buffer);
}

static void buffer_release(void *data, struct wl_buffer *proxy)
{
    (void)proxy;
    struct wallpaper_buffer *buffer = data;
    struct wallpaper_output *output = buffer->output;
    struct wallpaper_buffer **link = &output->buffers;
    while (*link != buffer)
    {
        link = &(*link)->next;
    }

    *link = buffer->next;
    output->buffer_count--;
    destroy_buffer(buffer);
}

static const struct wl_buffer_listener buffer_listener = {.release = buffer_release};

static void destroy_output(struct wallpaper_output *output)
{
    if (output->layer)
    {
        zwlr_layer_surface_v1_destroy(output->layer);
    }

    if (output->surface)
    {
        wl_surface_destroy(output->surface);
    }

    while (output->buffers)
    {
        struct wallpaper_buffer *buffer = output->buffers;
        output->buffers = buffer->next;
        /* Destroying a busy proxy and unmapping is safe: the compositor owns
         * its own reference to the shm file, never this process's mapping. */
        destroy_buffer(buffer);
    }

    if (wl_output_get_version(output->proxy) >= WL_OUTPUT_RELEASE_SINCE_VERSION)
    {
        wl_output_release(output->proxy);
    }

    else
    {
        wl_output_destroy(output->proxy);
    }

    free(output->source);
    free(output->target);
    free(output);
}

static void layer_configure(void *data, struct zwlr_layer_surface_v1 *layer, uint32_t serial, uint32_t width,
                            uint32_t height)
{
    struct wallpaper_output *output = data;
    zwlr_layer_surface_v1_ack_configure(layer, serial);
    if (!width || !height)
    {
        fail(output->owner, "Output %u: compositor supplied an unspecified wallpaper size", output->name);
        return;
    }

    if (!output->configured || output->width != width || output->height != height)
    {
        output->dirty = true;
    }

    output->width = width;
    output->height = height;
    output->configured = true;
}

static void layer_closed(void *data, struct zwlr_layer_surface_v1 *layer)
{
    (void)layer;
    struct wallpaper_output *output = data;
    output->closed = true;
}

static const struct zwlr_layer_surface_v1_listener layer_listener = {
    .configure = layer_configure,
    .closed = layer_closed,
};

static void output_geometry(void *data, struct wl_output *proxy, int32_t x, int32_t y, int32_t physical_width,
                            int32_t physical_height, int32_t subpixel, const char *make, const char *model,
                            int32_t transform)
{
    (void)data;
    (void)proxy;
    (void)x;
    (void)y;
    (void)physical_width;
    (void)physical_height;
    (void)subpixel;
    (void)make;
    (void)model;
    (void)transform;
}

static void output_mode(void *data, struct wl_output *proxy, uint32_t flags, int32_t width, int32_t height,
                        int32_t refresh)
{
    (void)data;
    (void)proxy;
    (void)flags;
    (void)width;
    (void)height;
    (void)refresh;
}

static void output_done(void *data, struct wl_output *proxy)
{
    (void)data;
    (void)proxy;
}

static void output_scale(void *data, struct wl_output *proxy, int32_t scale)
{
    (void)proxy;
    struct wallpaper_output *output = data;
    if (scale > 0 && output->scale != scale)
    {
        output->scale = scale;
        output->dirty = true;
    }
}

static const struct wl_output_listener output_listener = {
    .geometry = output_geometry,
    .mode = output_mode,
    .done = output_done,
    .scale = output_scale,
};

static int ensure_surfaces(struct hypr_wallpaper *handle)
{
    if (!handle->compositor || !handle->shm || !handle->shell)
    {
        return 1;
    }

    for (struct wallpaper_output *output = handle->outputs; output; output = output->next)
    {
        if (output->surface || output->closed)
        {
            continue;
        }

        output->surface = wl_compositor_create_surface(handle->compositor);
        if (!output->surface)
        {
            return fail(handle, "Creating wallpaper surface: %s", strerror(errno));
        }

        struct wl_region *region = wl_compositor_create_region(handle->compositor);
        if (!region)
        {
            return fail(handle, "Creating empty input region: %s", strerror(errno));
        }

        wl_surface_set_input_region(output->surface, region);
        wl_region_destroy(region);
        output->layer =
            zwlr_layer_shell_v1_get_layer_surface(handle->shell, output->surface, output->proxy,
                                                  ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND, "hyprnetshell-wallpaper");
        if (!output->layer)
        {
            return fail(handle, "Creating wallpaper layer surface: %s", strerror(errno));
        }

        zwlr_layer_surface_v1_add_listener(output->layer, &layer_listener, output);
        zwlr_layer_surface_v1_set_anchor(output->layer,
                                         ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
                                             ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
        zwlr_layer_surface_v1_set_size(output->layer, 0, 0);
        zwlr_layer_surface_v1_set_exclusive_zone(output->layer, -1);
        zwlr_layer_surface_v1_set_keyboard_interactivity(output->layer,
                                                         ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);
        wl_surface_commit(output->surface);
    }

    return 1;
}

static void registry_global(void *data, struct wl_registry *registry, uint32_t name, const char *interface,
                            uint32_t version)
{
    struct hypr_wallpaper *handle = data;
    if (strcmp(interface, wl_compositor_interface.name) == 0 && !handle->compositor && version >= 3)
    {
        handle->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, 3);
        handle->compositor_name = name;
        if (!handle->compositor)
        {
            fail(handle, "Binding wallpaper compositor: %s", strerror(errno));
        }
    }

    else if (strcmp(interface, wl_shm_interface.name) == 0 && !handle->shm)
    {
        handle->shm = wl_registry_bind(registry, name, &wl_shm_interface, 1);
        handle->shm_name = name;
        if (!handle->shm)
        {
            fail(handle, "Binding wallpaper shm: %s", strerror(errno));
        }
    }

    else if (strcmp(interface, zwlr_layer_shell_v1_interface.name) == 0 && !handle->shell)
    {
        handle->shell = wl_registry_bind(registry, name, &zwlr_layer_shell_v1_interface, 1);
        handle->shell_name = name;
        if (!handle->shell)
        {
            fail(handle, "Binding wallpaper layer shell: %s", strerror(errno));
        }
    }

    else if (strcmp(interface, wl_output_interface.name) == 0 && version >= 2)
    {
        struct wallpaper_output *output = calloc(1, sizeof(*output));
        if (!output)
        {
            fail(handle, "Allocating wallpaper output: %s", strerror(errno));
            return;
        }

        output->owner = handle;
        output->name = name;
        output->scale = 1;
        output->proxy = wl_registry_bind(registry, name, &wl_output_interface, version >= 3 ? 3 : 2);
        if (!output->proxy)
        {
            free(output);
            fail(handle, "Binding wallpaper output: %s", strerror(errno));
            return;
        }

        wl_output_add_listener(output->proxy, &output_listener, output);
        output->next = handle->outputs;
        handle->outputs = output;
    }
}

static void registry_remove(void *data, struct wl_registry *registry, uint32_t name)
{
    (void)registry;
    struct hypr_wallpaper *handle = data;
    if (name == handle->compositor_name || name == handle->shm_name || name == handle->shell_name)
    {
        fail(handle, "Required Wayland global %u was removed", name);
        return;
    }

    struct wallpaper_output **link = &handle->outputs;
    while (*link)
    {
        struct wallpaper_output *output = *link;
        if (output->name == name)
        {
            *link = output->next;
            destroy_output(output);
            return;
        }

        link = &output->next;
    }
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_remove,
};

static int64_t monotonic_ms(void);

static void blend_pixels(uint32_t *destination, const uint32_t *source, const uint32_t *target, size_t count,
                         unsigned int weight)
{
    for (size_t i = 0; i < count; i++)
    {
        uint32_t a = source[i];
        uint32_t b = target[i];
        uint32_t rb = (((a & 0x00ff00ffu) * (256 - weight) + (b & 0x00ff00ffu) * weight) >> 8) & 0x00ff00ffu;
        uint32_t g = (((a & 0x0000ff00u) * (256 - weight) + (b & 0x0000ff00u) * weight) >> 8) & 0x0000ff00u;
        destination[i] = 0xff000000u | rb | g;
    }
}

static void resample(uint32_t *destination, int width, int height, const void *pixels, int source_width,
                     int source_height, bool rgba)
{
    double step_x = (double)source_width / width;
    double step_y = (double)source_height / height;
    double step = step_x < step_y ? step_x : step_y;
    double left = (source_width - width * step) * 0.5;
    double top = (source_height - height * step) * 0.5;
    for (int y = 0; y < height; y++)
    {
        int sy = (int)(top + (y + 0.5) * step);
        if (sy >= source_height)
        {
            sy = source_height - 1;
        }

        for (int x = 0; x < width; x++)
        {
            int sx = (int)(left + (x + 0.5) * step);
            if (sx >= source_width)
            {
                sx = source_width - 1;
            }

            size_t index = (size_t)sy * source_width + sx;
            uint32_t pixel;
            if (rgba)
            {
                const unsigned char *rgb = (const unsigned char *)pixels + index * 4;
                pixel = 0xff000000u | ((uint32_t)rgb[0] << 16) | ((uint32_t)rgb[1] << 8) | rgb[2];
            }

            else
            {
                pixel = ((const uint32_t *)pixels)[index];
            }

            destination[(size_t)y * width + x] = pixel;
        }
    }
}

static int prepare_cache(struct wallpaper_output *output, int width, int height, size_t size)
{
    bool resized = width != output->cache_width || height != output->cache_height;
    if (!resized && output->target)
    {
        return 1;
    }

    uint32_t *target = malloc(size);
    uint32_t *source = resized && output->source ? malloc(size) : NULL;
    if (!target || (resized && output->source && !source))
    {
        free(target);
        free(source);
        return fail(output->owner, "Allocating wallpaper transition cache: %s", strerror(errno));
    }

    struct hypr_wallpaper *handle = output->owner;
    resample(target, width, height, handle->image, handle->image_width, handle->image_height, true);
    if (source)
    {
        resample(source, width, height, output->source, output->cache_width, output->cache_height, false);
        free(output->source);
        output->source = source;
    }

    free(output->target);
    output->target = target;
    output->cache_width = width;
    output->cache_height = height;
    return 1;
}

static unsigned int transition_weight(const struct wallpaper_output *output, int64_t now)
{
    if (!output->transitioning)
    {
        return 256;
    }

    int64_t elapsed = now - output->transition_start;
    if (elapsed <= 0)
    {
        return 0;
    }

    return elapsed >= TRANSITION_MS ? 256 : (unsigned int)(elapsed * 256 / TRANSITION_MS);
}

static int draw_output(struct wallpaper_output *output)
{
    struct hypr_wallpaper *handle = output->owner;
    int64_t now = monotonic_ms();
    if ((!output->dirty && (!output->transitioning || now < output->next_frame)) || !output->configured ||
        !handle->image || output->closed || output->buffer_count >= MAX_BUFFERS)
    {
        return 1;
    }

    uint64_t width64 = (uint64_t)output->width * (unsigned int)output->scale;
    uint64_t height64 = (uint64_t)output->height * (unsigned int)output->scale;
    if (!width64 || !height64 || width64 > INT_MAX / 4 || height64 > INT_MAX || width64 * 4 > INT_MAX / height64)
    {
        return fail(handle, "Output %u: wallpaper buffer dimensions exceed shm limits", output->name);
    }

    int width = (int)width64;
    int height = (int)height64;
    size_t size = (size_t)width * 4 * height;
    if (!prepare_cache(output, width, height, size))
    {
        return 0;
    }

    int fd = memfd_create("hyprnetshell-wallpaper", MFD_CLOEXEC);
    if (fd < 0)
    {
        return fail(handle, "Creating wallpaper shm file: %s", strerror(errno));
    }

    if (ftruncate(fd, (off_t)size) < 0)
    {
        int error = errno;
        close(fd);
        return fail(handle, "Sizing wallpaper shm file: %s", strerror(error));
    }

    void *pixels = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (pixels == MAP_FAILED)
    {
        int error = errno;
        close(fd);
        return fail(handle, "Mapping wallpaper shm file: %s", strerror(error));
    }

    struct wallpaper_buffer *buffer = calloc(1, sizeof(*buffer));
    struct wl_shm_pool *pool = wl_shm_create_pool(handle->shm, fd, (int)size);
    close(fd);
    if (!buffer || !pool)
    {
        free(buffer);
        if (pool)
        {
            wl_shm_pool_destroy(pool);
        }

        munmap(pixels, size);
        return fail(handle, "Allocating wallpaper shm pool/buffer: %s", strerror(errno));
    }

    buffer->proxy = wl_shm_pool_create_buffer(pool, 0, width, height, width * 4, WL_SHM_FORMAT_XRGB8888);
    wl_shm_pool_destroy(pool);
    if (!buffer->proxy)
    {
        free(buffer);
        munmap(pixels, size);
        return fail(handle, "Creating wallpaper wl_buffer: %s", strerror(errno));
    }

    unsigned int weight = transition_weight(output, monotonic_ms());

    if (output->source && weight != 256)
    {
        blend_pixels(pixels, output->source, output->target, size / 4, weight);
    }

    else
    {
        memcpy(pixels, output->target, size);
    }

    buffer->output = output;
    buffer->pixels = pixels;
    buffer->size = size;
    buffer->next = output->buffers;
    output->buffers = buffer;
    output->buffer_count++;
    wl_buffer_add_listener(buffer->proxy, &buffer_listener, buffer);
    wl_surface_set_buffer_scale(output->surface, output->scale);
    wl_surface_attach(output->surface, buffer->proxy, 0, 0);
    wl_surface_damage(output->surface, 0, 0, INT_MAX, INT_MAX);
    wl_surface_commit(output->surface);
    output->dirty = false;
    output->displayed_weight = weight;
    output->next_frame = monotonic_ms() + FRAME_INTERVAL_MS;
    if (weight == 256)
    {
        output->transitioning = false;
        free(output->source);
        output->source = NULL;
    }

    return 1;
}

/* One read preparation per call; EAGAIN on flush is serviced with POLLOUT,
 * never by spinning. Every successful prepare has exactly one read/cancel. */
static int pump(struct hypr_wallpaper *handle, int timeout)
{
    if (wl_display_dispatch_pending(handle->display) < 0)
    {
        return connection_error(handle, "Dispatching wallpaper events");
    }

    if (handle->failed)
    {
        return 0;
    }

    if (wl_display_prepare_read(handle->display) < 0)
    {
        if (errno == EAGAIN)
        {
            return 1;
        }

        return connection_error(handle, "Preparing wallpaper event read");
    }

    struct pollfd descriptor = {.fd = wl_display_get_fd(handle->display), .events = POLLIN};
    if (wl_display_flush(handle->display) < 0)
    {
        if (errno != EAGAIN)
        {
            wl_display_cancel_read(handle->display);
            return connection_error(handle, "Flushing wallpaper requests");
        }

        descriptor.events |= POLLOUT;
    }

    int result = poll(&descriptor, 1, timeout);
    if (result < 0)
    {
        int error = errno;
        wl_display_cancel_read(handle->display);
        if (error == EINTR)
        {
            return 1;
        }

        return fail(handle, "Polling wallpaper connection: %s", strerror(error));
    }

    if (descriptor.revents & POLLIN)
    {
        if (wl_display_read_events(handle->display) < 0)
        {
            return connection_error(handle, "Reading wallpaper events");
        }
    }

    else
    {
        wl_display_cancel_read(handle->display);
    }

    if (descriptor.revents & (POLLERR | POLLHUP | POLLNVAL))
    {
        return fail(handle, "Wallpaper Wayland connection closed (poll flags 0x%x)", descriptor.revents);
    }

    if ((descriptor.revents & POLLOUT) && wl_display_flush(handle->display) < 0 && errno != EAGAIN)
    {
        return connection_error(handle, "Flushing writable wallpaper connection");
    }

    if (wl_display_dispatch_pending(handle->display) < 0)
    {
        return connection_error(handle, "Dispatching wallpaper events");
    }

    return !handle->failed;
}

static void sync_done(void *data, struct wl_callback *callback, uint32_t serial)
{
    (void)callback;
    (void)serial;
    bool *done = data;
    *done = true;
}

static const struct wl_callback_listener sync_listener = {.done = sync_done};

static int64_t monotonic_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int startup_sync(struct hypr_wallpaper *handle, int64_t deadline)
{
    bool done = false;
    struct wl_callback *callback = wl_display_sync(handle->display);
    if (!callback)
    {
        return fail(handle, "Creating wallpaper startup sync: %s", strerror(errno));
    }

    wl_callback_add_listener(callback, &sync_listener, &done);
    while (!done && !handle->failed)
    {
        if (wl_display_dispatch_pending(handle->display) < 0)
        {
            connection_error(handle, "Dispatching wallpaper startup events");
            break;
        }

        if (done || handle->failed)
        {
            break;
        }

        int64_t remaining = deadline - monotonic_ms();
        if (remaining <= 0)
        {
            fail(handle, "Wallpaper Wayland startup exceeded %d ms", STARTUP_TIMEOUT_MS);
            break;
        }

        if (!pump(handle, (int)remaining))
        {
            break;
        }
    }

    wl_callback_destroy(callback);
    return done && !handle->failed;
}

hypr_wallpaper *hypr_wallpaper_create(void)
{
    create_error[0] = '\0';
    struct hypr_wallpaper *handle = calloc(1, sizeof(*handle));
    if (!handle)
    {
        snprintf(create_error, sizeof(create_error), "Allocating wallpaper backend: %s", strerror(errno));
        return NULL;
    }

    handle->display = wl_display_connect(NULL);
    if (!handle->display)
    {
        fail(handle, "Connecting wallpaper Wayland display: %s", strerror(errno));
        goto failure;
    }

    int64_t deadline = monotonic_ms() + STARTUP_TIMEOUT_MS;
    handle->registry = wl_display_get_registry(handle->display);
    if (!handle->registry)
    {
        fail(handle, "Obtaining wallpaper Wayland registry: %s", strerror(errno));
        goto failure;
    }

    wl_registry_add_listener(handle->registry, &registry_listener, handle);
    if (!startup_sync(handle, deadline))
    {
        goto failure;
    }

    if (!handle->compositor || !handle->shm || !handle->shell)
    {
        fail(handle, "Wallpaper requires wl_compositor v3, wl_shm and zwlr_layer_shell_v1");
        goto failure;
    }

    if (!ensure_surfaces(handle) || !startup_sync(handle, deadline))
    {
        goto failure;
    }

    return handle;

failure:
    snprintf(create_error, sizeof(create_error), "%s", handle->error);
    hypr_wallpaper_destroy(handle);
    return NULL;
}

void hypr_wallpaper_destroy(hypr_wallpaper *handle)
{
    if (!handle)
    {
        return;
    }

    while (handle->outputs)
    {
        struct wallpaper_output *output = handle->outputs;
        handle->outputs = output->next;
        destroy_output(output);
    }

    if (handle->shell)
    {
        /* The layer-shell global destructor was introduced in protocol v3.
         * This backend binds v1, so only destroy the local proxy. */
        wl_proxy_destroy((struct wl_proxy *)handle->shell);
    }

    if (handle->shm)
    {
        wl_shm_destroy(handle->shm);
    }

    if (handle->compositor)
    {
        wl_compositor_destroy(handle->compositor);
    }

    if (handle->registry)
    {
        wl_registry_destroy(handle->registry);
    }

    if (handle->display)
    {
        wl_display_disconnect(handle->display);
    }

    free(handle->image);
    free(handle);
}

int hypr_wallpaper_poll(hypr_wallpaper *handle)
{
    if (!handle || handle->failed || !pump(handle, 0) || !ensure_surfaces(handle))
    {
        return 0;
    }

    struct wallpaper_output **link = &handle->outputs;
    while (*link)
    {
        struct wallpaper_output *output = *link;
        if (output->closed)
        {
            *link = output->next;
            destroy_output(output);
            continue;
        }

        if (!draw_output(output))
        {
            return 0;
        }

        link = &output->next;
    }

    if (wl_display_flush(handle->display) < 0 && errno != EAGAIN)
    {
        return connection_error(handle, "Flushing wallpaper frame");
    }

    return 1;
}

static void start_transition(struct wallpaper_output *output, int64_t now)
{
    /* Freeze the last submitted blend, not an unpresented wall-clock frame. */
    if (output->target)
    {
        if (output->source)
        {
            blend_pixels(output->source, output->source, output->target,
                         (size_t)output->cache_width * output->cache_height, output->displayed_weight);
            free(output->target);
        }

        else
        {
            output->source = output->target;
        }

        output->target = NULL;
    }

    output->transitioning = output->source != NULL;
    output->transition_start = now;
    output->displayed_weight = 0;
    output->dirty = true;
}

int hypr_wallpaper_set_image(hypr_wallpaper *handle, const unsigned char *rgba, int width, int height, int stride)
{
    if (!handle || handle->failed)
    {
        return 0;
    }

    if (!rgba || width <= 0 || height <= 0 || width > INT_MAX / 4 || stride < width * 4 ||
        (size_t)height > SIZE_MAX / (size_t)stride)
    {
        snprintf(handle->error, sizeof(handle->error), "Invalid wallpaper RGBA dimensions or stride");
        return 0;
    }

    size_t row_size = (size_t)width * 4;
    unsigned char *copy = malloc(row_size * (size_t)height);
    if (!copy)
    {
        snprintf(handle->error, sizeof(handle->error), "Copying wallpaper RGBA: %s", strerror(errno));
        return 0;
    }

    for (int y = 0; y < height; y++)
    {
        memcpy(copy + (size_t)y * row_size, rgba + (size_t)y * stride, row_size);
    }

    free(handle->image);
    handle->image = copy;
    handle->image_width = width;
    handle->image_height = height;
    handle->error[0] = '\0';
    int64_t now = monotonic_ms();
    for (struct wallpaper_output *output = handle->outputs; output; output = output->next)
    {
        start_transition(output, now);
    }

    return hypr_wallpaper_poll(handle);
}

const char *hypr_wallpaper_get_error(hypr_wallpaper *handle)
{
    return handle ? handle->error : create_error;
}
