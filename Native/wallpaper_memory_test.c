#define _GNU_SOURCE
#include <assert.h>
#include <errno.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <time.h>
#include <wayland-client.h>

/* Only allocations made by the included backend are counted. The transport
 * uses a fixed arena; libc/Wayland internals and compositor memory are not RSS
 * measurements. Real memfd, ftruncate, mmap and munmap remain in use. */
#define TRACKING_SLOTS 256
struct allocation
{
    void *pointer;
    size_t size;
    int mapping;
};
struct mock_proxy
{
    int live;
    uint32_t version;
    void *data;
};
static struct allocation allocations[TRACKING_SLOTS];
static struct mock_proxy proxies[TRACKING_SLOTS];
static size_t heap_bytes, heap_count, map_bytes, map_count;
static size_t peak_bytes, peak_heap, peak_map;
static size_t malloc_calls, calloc_calls, free_calls, mmap_calls, munmap_calls;
static int64_t now_ms = 10000;

static void track(void *pointer, size_t size, int mapping)
{
    assert(pointer && pointer != MAP_FAILED);
    for (size_t i = 0; i < TRACKING_SLOTS; ++i)
    {
        if (!allocations[i].pointer)
        {
            allocations[i] = (struct allocation){pointer, size, mapping};
            if (mapping)
            {
                map_bytes += size;
                ++map_count;
            }
            else
            {
                heap_bytes += size;
                ++heap_count;
            }

            if (heap_bytes + map_bytes > peak_bytes)
            {
                peak_bytes = heap_bytes + map_bytes;
            }

            if (heap_bytes > peak_heap)
            {
                peak_heap = heap_bytes;
            }

            if (map_bytes > peak_map)
            {
                peak_map = map_bytes;
            }

            return;
        }
    }

    assert(!"allocation tracking exhausted");
}

static void untrack(void *pointer, size_t size, int mapping)
{
    for (size_t i = 0; i < TRACKING_SLOTS; ++i)
    {
        if (allocations[i].pointer == pointer)
        {
            assert(allocations[i].mapping == mapping);
            if (mapping)
            {
                assert(allocations[i].size == size);
                map_bytes -= size;
                --map_count;
            }
            else
            {
                heap_bytes -= allocations[i].size;
                --heap_count;
            }

            allocations[i].pointer = NULL;
            return;
        }
    }

    assert(!"untracked or duplicate release");
}

static void *tracked_malloc(size_t size)
{
    void *pointer = malloc(size);
    ++malloc_calls;
    track(pointer, size, 0);
    return pointer;
}

static void *tracked_calloc(size_t count, size_t size)
{
    void *pointer = calloc(count, size);
    ++calloc_calls;
    track(pointer, count * size, 0);
    return pointer;
}

static void tracked_free(void *pointer)
{
    if (pointer)
    {
        untrack(pointer, 0, 0);
        ++free_calls;
        free(pointer);
    }
}

static void *tracked_mmap(void *address, size_t size, int protection, int flags,
                          int fd, off_t offset)
{
    void *pointer = mmap(address, size, protection, flags, fd, offset);
    ++mmap_calls;
    track(pointer, size, 1);
    return pointer;
}

static int tracked_munmap(void *pointer, size_t size)
{
    untrack(pointer, size, 1);
    ++munmap_calls;
    return munmap(pointer, size);
}

static int mock_clock_gettime(clockid_t clock, struct timespec *time)
{
    assert(clock == CLOCK_MONOTONIC);
    time->tv_sec = now_ms / 1000;
    time->tv_nsec = (now_ms % 1000) * 1000000;
    return 0;
}

/* Linker wrapping intercepts the wire calls made by generated inline protocol
 * helpers, while retaining their real constructor/destructor behavior. */
struct wl_proxy *
__wrap_wl_proxy_marshal_flags(struct wl_proxy *proxy, uint32_t opcode,
                              const struct wl_interface *interface,
                              uint32_t version, uint32_t flags, ...)
{
    (void)opcode;
    if (proxy)
    {
        assert(((struct mock_proxy *)proxy)->live);
    }

    if (flags & WL_MARSHAL_FLAG_DESTROY)
    {
        ((struct mock_proxy *)proxy)->live = 0;
    }

    if (interface)
    {
        for (size_t i = 0; i < TRACKING_SLOTS; ++i)
        {
            if (!proxies[i].live)
            {
                proxies[i] = (struct mock_proxy){.live = 1, .version = version};
                return (struct wl_proxy *)&proxies[i];
            }
        }

        assert(!"proxy arena exhausted");
    }

    return NULL;
}

uint32_t __wrap_wl_proxy_get_version(struct wl_proxy *proxy)
{
    assert(((struct mock_proxy *)proxy)->live);
    return ((struct mock_proxy *)proxy)->version;
}

int __wrap_wl_proxy_add_listener(struct wl_proxy *proxy,
                                 void (**listener)(void), void *data)
{
    assert(listener);
    assert(((struct mock_proxy *)proxy)->live);
    ((struct mock_proxy *)proxy)->data = data;
    return 0;
}

void __wrap_wl_proxy_destroy(struct wl_proxy *proxy)
{
    assert(((struct mock_proxy *)proxy)->live);
    ((struct mock_proxy *)proxy)->live = 0;
}

int __wrap_wl_display_dispatch_pending(struct wl_display *display)
{
    (void)display;
    return 0;
}

int __wrap_wl_display_prepare_read(struct wl_display *display)
{
    (void)display;
    errno = EAGAIN;
    return -1;
}

int __wrap_wl_display_flush(struct wl_display *display)
{
    (void)display;
    return 0;
}

#define malloc tracked_malloc
#define calloc tracked_calloc
#define free tracked_free
#define mmap tracked_mmap
#define munmap tracked_munmap
#define clock_gettime mock_clock_gettime
#include "hypr_wallpaper.c"
#undef malloc
#undef calloc
#undef free
#undef mmap
#undef munmap
#undef clock_gettime

#define IMAGE_WIDTH 320
#define IMAGE_HEIGHT 180
#define CYCLES 100
static unsigned char image[IMAGE_WIDTH * IMAGE_HEIGHT * 4];

static void release_all(struct wallpaper_output *output)
{
    while (output->buffers)
    {
        struct wallpaper_buffer *buffer = output->buffers;
        assert(((struct mock_proxy *)buffer->proxy)->data == buffer);
        buffer_listener.release(buffer, buffer->proxy);
    }

    assert(output->buffer_count == 0);
}

static struct wallpaper_output *add_output(hypr_wallpaper *handle,
                                           uint32_t name)
{
    registry_global(handle, NULL, name, "wl_output", 3);
    assert(ensure_surfaces(handle));
    struct wallpaper_output *output = handle->outputs;
    assert(output->name == name);
    layer_listener.configure(output, output->layer, 1, IMAGE_WIDTH,
                             IMAGE_HEIGHT);
    return output;
}

static void set_image(hypr_wallpaper *handle, unsigned char value)
{
    memset(image, value, sizeof(image));
    assert(hypr_wallpaper_set_image(handle, image, IMAGE_WIDTH, IMAGE_HEIGHT,
                                    IMAGE_WIDTH * 4));
}

static void finish(hypr_wallpaper *handle, struct wallpaper_output *output)
{
    release_all(output);
    now_ms += TRANSITION_MS + FRAME_INTERVAL_MS;
    assert(hypr_wallpaper_poll(handle));
    assert(!output->transitioning && !output->source && output->target);
    release_all(output);
}

int main(void)
{
    hypr_wallpaper *handle = tracked_calloc(1, sizeof(*handle));
    registry_global(handle, NULL, 1, "wl_compositor", 3);
    registry_global(handle, NULL, 2, "wl_shm", 1);
    registry_global(handle, NULL, 3, "zwlr_layer_shell_v1", 1);
    struct wallpaper_output *output = add_output(handle, 10);
    set_image(handle, 0);
    finish(handle, output);
    size_t steady_bytes = heap_bytes;
    size_t steady_count = heap_count;
    assert(steady_bytes ==
           sizeof(*handle) + sizeof(*output) + 2 * sizeof(image));
    assert(steady_count == 4 && map_count == 0);

    for (int cycle = 0; cycle < CYCLES; ++cycle)
    {
        /* Completed transition with a submitted intermediate blend. */
        set_image(handle, (unsigned char)cycle);
        release_all(output);
        now_ms += TRANSITION_MS / 2;
        assert(hypr_wallpaper_poll(handle));
        assert(output->displayed_weight == 128 && output->source);
        finish(handle, output);
        assert(heap_bytes == steady_bytes && heap_count == steady_count &&
               map_count == 0);

        /* Interrupt repeatedly, retaining releases to exercise backpressure. */
        set_image(handle, 17);
        now_ms += FRAME_INTERVAL_MS;
        assert(hypr_wallpaper_poll(handle));
        assert(output->buffer_count == 2 && map_count == 2);
        size_t capped_bytes = map_bytes;
        for (int interrupted = 0; interrupted < 8; ++interrupted)
        {
            set_image(handle, (unsigned char)(interrupted + 30));
            now_ms += TRANSITION_MS;
            assert(hypr_wallpaper_poll(handle));
            assert(output->buffer_count == 2 && map_count == 2 &&
                   map_bytes == capped_bytes);
        }

        /* Resize and scale while old-size mappings are still busy. */
        layer_listener.configure(output, output->layer, 2, IMAGE_WIDTH + 16,
                                 IMAGE_HEIGHT + 8);
        output_listener.scale(output, output->proxy, 2);
        struct wallpaper_buffer *old = output->buffers;
        buffer_listener.release(old, old->proxy);
        assert(hypr_wallpaper_poll(handle));
        assert(output->buffer_count == 2 && map_count == 2);
        finish(handle, output);
        layer_listener.configure(output, output->layer, 3, IMAGE_WIDTH,
                                 IMAGE_HEIGHT);
        output_listener.scale(output, output->proxy, 1);
        finish(handle, output);
        assert(heap_bytes == steady_bytes && heap_count == steady_count &&
               map_count == 0);

        /* Hot-unplug and layer close must reclaim even unreleased mappings. */
        struct wallpaper_output *extra = add_output(handle, 20);
        set_image(handle, 91);
        set_image(handle, 92);
        now_ms += FRAME_INTERVAL_MS;
        assert(hypr_wallpaper_poll(handle));
        assert(extra->buffer_count == 2 && output->buffer_count == 2);
        registry_remove(handle, NULL, 20);
        assert(handle->outputs == output && map_count == 2);
        extra = add_output(handle, 21);
        assert(hypr_wallpaper_poll(handle));
        layer_listener.closed(extra, extra->layer);
        assert(hypr_wallpaper_poll(handle));
        assert(handle->outputs == output && map_count == 2);
        finish(handle, output);
        assert(heap_bytes == steady_bytes && heap_count == steady_count &&
               map_bytes == 0 && map_count == 0);
    }

    /* Destruction during a transition, with both SHM slots unreleased. */
    set_image(handle, 123);
    now_ms += FRAME_INTERVAL_MS;
    assert(hypr_wallpaper_poll(handle));
    assert(output->buffer_count == 2);
    hypr_wallpaper_destroy(handle);
    assert(heap_bytes == 0 && heap_count == 0 && map_bytes == 0 &&
           map_count == 0);
    assert(malloc_calls + calloc_calls == free_calls &&
           mmap_calls == munmap_calls);
    for (size_t i = 0; i < TRACKING_SLOTS; ++i)
    {
        assert(!proxies[i].live);
    }

    printf("PASS: %d cycles; steady heap=%zu bytes/%zu allocations, SHM=0 "
           "bytes/0 mappings\n",
           CYCLES, steady_bytes, steady_count);
    printf("Peak: combined=%zu bytes, heap=%zu bytes, SHM=%zu bytes "
           "(independent high-water marks)\n",
           peak_bytes, peak_heap, peak_map);
    printf("Calls: malloc=%zu calloc=%zu free=%zu mmap=%zu munmap=%zu; final "
           "bytes/counts all zero\n",
           malloc_calls, calloc_calls, free_calls, mmap_calls, munmap_calls);
    puts("Mocked Wayland/time; real backend allocation/SHM/release paths, not "
         "live compositor RSS.");
    return 0;
}
