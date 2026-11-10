#define _POSIX_C_SOURCE 200809L
#include "hypr_temperature.h"
#include "hyprland-ctm-control-v1-client-protocol.h"
#include <errno.h>
#include <math.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>

struct temperature_output
{
    struct temperature_output *next;
    struct wl_output *proxy;
    uint32_t name;
};

struct hypr_temperature
{
    struct wl_display *display;
    struct wl_registry *registry;
    struct hyprland_ctm_control_manager_v1 *manager;
    struct temperature_output *outputs;
    uint32_t manager_name;
    int kelvin;
    int requested;
    int dirty;
    int failed;
    char error[256];
};

static _Thread_local char create_error[256];

static int fail(hypr_temperature *handle, const char *operation)
{
    int code = wl_display_get_error(handle->display);
    handle->failed = 1;
    snprintf(handle->error, sizeof(handle->error), "%s: %s", operation,
             strerror(code ? code : (errno ? errno : EIO)));
    return 0;
}

static int64_t milliseconds(void)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) < 0)
    {
        return -1;
    }

    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

/* A prepared read is always completed or cancelled, including timeout/EINTR. */
static int pump(hypr_temperature *handle, int timeout)
{
    while (wl_display_prepare_read(handle->display) != 0)
    {
        if (wl_display_dispatch_pending(handle->display) < 0 || handle->failed)
        {
            return fail(handle, "Dispatch temperature events");
        }
    }

    int flush = wl_display_flush(handle->display);
    if (flush < 0 && errno != EAGAIN)
    {
        wl_display_cancel_read(handle->display);
        return fail(handle, "Flush temperature requests");
    }

    struct pollfd fd = {.fd = wl_display_get_fd(handle->display),
                        .events = POLLIN | (flush < 0 ? POLLOUT : 0)};
    int result = poll(&fd, 1, timeout);
    int poll_error = errno;
    if (result < 0 || !(fd.revents & POLLIN))
    {
        wl_display_cancel_read(handle->display);
    }

    else if (wl_display_read_events(handle->display) < 0)
    {
        return fail(handle, "Read temperature events");
    }

    if (result < 0 && poll_error != EINTR)
    {
        errno = poll_error;
        return fail(handle, "Poll temperature connection");
    }

    if (fd.revents & (POLLERR | POLLHUP | POLLNVAL))
    {
        errno = EPIPE;
        return fail(handle, "Temperature connection closed");
    }

    if (wl_display_dispatch_pending(handle->display) < 0)
    {
        return fail(handle, "Dispatch temperature events");
    }

    return !handle->failed;
}

static void sync_done(void *data, struct wl_callback *callback, uint32_t serial)
{
    (void)callback;
    (void)serial;
    *(int *)data = 1;
}

static const struct wl_callback_listener SYNC_LISTENER = {.done = sync_done};

static int synchronize(hypr_temperature *handle, int64_t deadline)
{
    int done = 0;
    struct wl_callback *callback = wl_display_sync(handle->display);
    if (!callback)
    {
        return fail(handle, "Create temperature sync");
    }

    wl_callback_add_listener(callback, &SYNC_LISTENER, &done);
    while (!done)
    {
        int64_t now = milliseconds();
        if (now < 0 || now >= deadline)
        {
            snprintf(handle->error, sizeof(handle->error),
                     "Temperature startup synchronization timed out");
            wl_callback_destroy(callback);
            return 0;
        }

        if (!pump(handle, (int)(deadline - now)))
        {
            wl_callback_destroy(callback);
            return 0;
        }
    }

    wl_callback_destroy(callback);
    return 1;
}

static void blocked(void *data, struct hyprland_ctm_control_manager_v1 *manager)
{
    hypr_temperature *handle = data;
    /* Hyprland marks rejected resources blocked before sending this event;
     * its resource destructor skips CTM resets for blocked managers. */
    hyprland_ctm_control_manager_v1_destroy(manager);
    handle->manager = NULL;
    snprintf(handle->error, sizeof(handle->error),
             "CTM control blocked by another client; release it and recreate "
             "the handle");
}

static const struct hyprland_ctm_control_manager_v1_listener MANAGER_LISTENER =
    {.blocked = blocked};

static void global(void *data, struct wl_registry *registry, uint32_t name,
                   const char *interface, uint32_t version)
{
    hypr_temperature *handle = data;
    if (strcmp(interface, hyprland_ctm_control_manager_v1_interface.name) ==
            0 &&
        !handle->manager_name)
    {
        handle->manager_name = name;
        /* v1 silently ignores rejected clients: availability cannot be known,
         * making retrying consumers believe they own CTM control indefinitely.
         */
        if (version < 2)
        {
            snprintf(
                handle->error, sizeof(handle->error),
                "CTM protocol v2 required to detect another owner's control");
            return;
        }

        handle->manager = wl_registry_bind(
            registry, name, &hyprland_ctm_control_manager_v1_interface, 2);
        if (!handle->manager)
        {
            fail(handle, "Bind CTM manager");
            return;
        }

        hyprland_ctm_control_manager_v1_add_listener(handle->manager,
                                                     &MANAGER_LISTENER, handle);
        handle->dirty = 1;
    }

    else if (strcmp(interface, wl_output_interface.name) == 0)
    {
        struct temperature_output *output = calloc(1, sizeof(*output));
        if (!output)
        {
            fail(handle, "Allocate temperature output");
            return;
        }

        /* v1 avoids done/scale/name events; geometry/mode are safely discarded
         * by libwayland when no listener is installed. No output metadata
         * needed. */
        output->proxy =
            wl_registry_bind(registry, name, &wl_output_interface, 1);
        if (!output->proxy)
        {
            free(output);
            fail(handle, "Bind temperature output");
            return;
        }

        output->name = name;
        output->next = handle->outputs;
        handle->outputs = output;
        handle->dirty = 1;
    }
}

static void global_remove(void *data, struct wl_registry *registry,
                          uint32_t name)
{
    (void)registry;
    hypr_temperature *handle = data;
    if (name == handle->manager_name)
    {
        if (handle->manager)
        {
            hyprland_ctm_control_manager_v1_destroy(handle->manager);
            handle->manager = NULL;
        }

        handle->manager_name = 0;
        snprintf(handle->error, sizeof(handle->error),
                 "CTM manager removed by compositor");
    }

    struct temperature_output **link = &handle->outputs;
    while (*link)
    {
        struct temperature_output *output = *link;
        if (output->name == name)
        {
            *link = output->next;
            wl_output_destroy(output->proxy);
            free(output);
            handle->dirty = 1;
            break;
        }

        link = &output->next;
    }
}

static const struct wl_registry_listener REGISTRY_LISTENER = {
    .global = global, .global_remove = global_remove};

static double channel(double value)
{
    return fmin(1.0, fmax(0.0, value / 255.0));
}

static void apply(hypr_temperature *handle)
{
    if (!handle->dirty || !handle->requested || !handle->manager ||
        handle->failed)
    {
        return;
    }

    double green = 1.0;
    double blue = 1.0;
    if (handle->kelvin != 6500)
    {
        /* Standard logarithmic blackbody RGB approximation, restricted to its
         * warm branch. Red stays 1; exact daylight bypasses the approximation.
         */
        double temperature = handle->kelvin / 100.0;
        green = channel(99.4708025861 * log(temperature) - 161.1195681661);
        blue =
            channel(138.5177312231 * log(temperature - 10.0) - 305.0447927307);
    }

    for (struct temperature_output *output = handle->outputs; output;
         output = output->next)
    {
        hyprland_ctm_control_manager_v1_set_ctm_for_output(
            handle->manager, output->proxy, wl_fixed_from_int(1), 0, 0, 0,
            wl_fixed_from_double(green), 0, 0, 0, wl_fixed_from_double(blue));
    }

    /* Omitted outputs reset to identity: always send the complete output set.
     */
    hyprland_ctm_control_manager_v1_commit(handle->manager);
    handle->dirty = 0;
}

hypr_temperature *hypr_temperature_create(void)
{
    create_error[0] = '\0';
    hypr_temperature *handle = calloc(1, sizeof(*handle));
    if (!handle)
    {
        snprintf(create_error, sizeof(create_error),
                 "Allocate temperature handle: %s", strerror(errno));
        return NULL;
    }

    handle->kelvin = 6500;
    handle->display = wl_display_connect(NULL);
    if (!handle->display)
    {
        snprintf(create_error, sizeof(create_error),
                 "Connect temperature Wayland display: %s", strerror(errno));
        free(handle);
        return NULL;
    }

    handle->registry = wl_display_get_registry(handle->display);
    if (!handle->registry)
    {
        fail(handle, "Get temperature registry");
    }

    else
    {
        wl_registry_add_listener(handle->registry, &REGISTRY_LISTENER, handle);
        int64_t now = milliseconds();
        /* Second sync covers manager events generated by binds in first sync.
         */
        if (now >= 0 && synchronize(handle, now + 2000) &&
            synchronize(handle, now + 2000) && !handle->failed &&
            handle->manager)
        {
            return handle;
        }
    }

    snprintf(create_error, sizeof(create_error), "%s",
             handle->error[0] ? handle->error
                              : "Hyprland CTM control protocol is unavailable");
    hypr_temperature_destroy(handle);
    return NULL;
}

void hypr_temperature_destroy(hypr_temperature *handle)
{
    if (!handle)
    {
        return;
    }

    if (handle->manager)
    {
        hyprland_ctm_control_manager_v1_destroy(handle->manager);
    }

    /* Disconnect alone is insufficient: give the reset destructor a bounded
     * opportunity to reach the compositor even under socket backpressure. */
    int64_t deadline = milliseconds() + 200;
    while (wl_display_flush(handle->display) < 0 && errno == EAGAIN)
    {
        int64_t now = milliseconds();
        if (now < 0 || now >= deadline)
        {
            break;
        }

        struct pollfd fd = {.fd = wl_display_get_fd(handle->display),
                            .events = POLLOUT};
        int result = poll(&fd, 1, (int)(deadline - now));
        if (result == 0 || (result < 0 && errno != EINTR) ||
            (fd.revents & (POLLERR | POLLHUP | POLLNVAL)))
        {
            break;
        }
    }

    while (handle->outputs)
    {
        struct temperature_output *output = handle->outputs;
        handle->outputs = output->next;
        wl_output_destroy(output->proxy);
        free(output);
    }

    if (handle->registry)
    {
        wl_registry_destroy(handle->registry);
    }

    wl_display_disconnect(handle->display);
    free(handle);
}

int hypr_temperature_poll(hypr_temperature *handle)
{
    if (!handle || handle->failed || !pump(handle, 0))
    {
        return 0;
    }

    apply(handle);
    if (wl_display_flush(handle->display) < 0 && errno != EAGAIN)
    {
        return fail(handle, "Flush temperature matrix");
    }

    return 1;
}

int hypr_temperature_set_temperature(hypr_temperature *handle, int kelvin)
{
    if (!handle)
    {
        return 0;
    }

    if (handle->failed)
    {
        return 0;
    }

    if (kelvin < 2000 || kelvin > 6500)
    {
        snprintf(handle->error, sizeof(handle->error),
                 "Temperature must be between 2000 and 6500 kelvin");
        return 0;
    }

    handle->kelvin = kelvin;
    handle->requested = 1;
    handle->dirty = 1;
    if (!hypr_temperature_poll(handle))
    {
        return 0;
    }

    if (!hypr_temperature_is_available(handle))
    {
        if (!handle->error[0])
        {
            snprintf(handle->error, sizeof(handle->error),
                     "CTM manager unavailable");
        }

        return 0;
    }

    handle->error[0] = '\0';
    return 1;
}

int hypr_temperature_is_available(hypr_temperature *handle)
{
    return handle && !handle->failed && handle->manager;
}

int hypr_temperature_is_active(hypr_temperature *handle)
{
    return hypr_temperature_is_available(handle) && handle->requested &&
           handle->kelvin != 6500 && handle->outputs;
}

const char *hypr_temperature_get_error(hypr_temperature *handle)
{
    return handle ? handle->error : create_error;
}
