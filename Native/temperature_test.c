#define _POSIX_C_SOURCE 200809L
#include "hyprland-ctm-control-v1-client-protocol.h"
#include <assert.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <wayland-client.h>

/* Test the actual client state machine and wire arguments without a desktop.
 * The transport is inert; registry/blocked events are delivered explicitly.
 * This does not replace an integration test against a real compositor. */
static int socket_fds[2];
static int flush_error;
static int dispatch_count;
static int matrix_count;
static int commit_count;
static int destroy_count;
static int reset_count;
static int output_destroy_count;
static int bind_count;
static int expected_outputs;
static uint32_t bound_version;
static wl_fixed_t last_matrix[9];
static struct wl_output *batch_outputs[16];
static struct hyprland_ctm_control_manager_v1 *owner;
static struct hyprland_ctm_control_manager_v1 *next_manager;

static void *mock_bind(struct wl_registry *registry, uint32_t name,
                       const struct wl_interface *interface, uint32_t version)
{
    (void)registry;
    ++bind_count;
    bound_version = version;
    if (interface == &hyprland_ctm_control_manager_v1_interface)
    {
        return next_manager;
    }

    assert(interface == &wl_output_interface);
    return (void *)(uintptr_t)(name + 1024);
}

static int mock_add_listener(
    struct hyprland_ctm_control_manager_v1 *manager,
    const struct hyprland_ctm_control_manager_v1_listener *listener, void *data)
{
    (void)manager;
    (void)data;
    assert(listener->blocked);
    return 0;
}

static void mock_matrix(struct hyprland_ctm_control_manager_v1 *manager,
                        struct wl_output *output, wl_fixed_t m0, wl_fixed_t m1,
                        wl_fixed_t m2, wl_fixed_t m3, wl_fixed_t m4,
                        wl_fixed_t m5, wl_fixed_t m6, wl_fixed_t m7,
                        wl_fixed_t m8)
{
    assert(manager == owner);
    assert(matrix_count < 16);
    for (int i = 0; i < matrix_count; ++i)
    {
        assert(batch_outputs[i] != output);
    }

    batch_outputs[matrix_count++] = output;
    wl_fixed_t matrix[9] = {m0, m1, m2, m3, m4, m5, m6, m7, m8};
    if (matrix_count > 1)
    {
        assert(memcmp(last_matrix, matrix, sizeof(matrix)) == 0);
    }

    memcpy(last_matrix, matrix, sizeof(matrix));
    for (int i = 0; i < 9; ++i)
    {
        assert(matrix[i] >= 0 && matrix[i] <= 256);
        if (i != 0 && i != 4 && i != 8)
        {
            assert(matrix[i] == 0);
        }
    }

    assert(m0 == 256);
}

static void mock_commit(struct hyprland_ctm_control_manager_v1 *manager)
{
    assert(manager == owner);
    /* A partial batch would reset omitted outputs on the real compositor. */
    assert(matrix_count == expected_outputs);
    matrix_count = 0;
    ++commit_count;
}

static void
mock_manager_destroy(struct hyprland_ctm_control_manager_v1 *manager)
{
    ++destroy_count;
    /* Mirrors Hyprland's blocked-resource destructor guard. */
    if (manager == owner)
    {
        ++reset_count;
    }
}

static void mock_output_destroy(struct wl_output *output)
{
    assert(output);
    ++output_destroy_count;
}

static int mock_prepare_read(struct wl_display *display)
{
    (void)display;
    return 0;
}

static int mock_flush(struct wl_display *display)
{
    (void)display;
    if (flush_error)
    {
        errno = flush_error;
        return -1;
    }

    return 0;
}

static int mock_get_fd(struct wl_display *display)
{
    (void)display;
    return socket_fds[0];
}

static void mock_cancel_read(struct wl_display *display)
{
    (void)display;
}

static int mock_dispatch(struct wl_display *display)
{
    (void)display;
    ++dispatch_count;
    return 0;
}

static int mock_get_error(struct wl_display *display)
{
    (void)display;
    return 0;
}

static void mock_disconnect(struct wl_display *display)
{
    (void)display;
}

#define wl_registry_bind mock_bind
#define hyprland_ctm_control_manager_v1_add_listener mock_add_listener
#define hyprland_ctm_control_manager_v1_set_ctm_for_output mock_matrix
#define hyprland_ctm_control_manager_v1_commit mock_commit
#define hyprland_ctm_control_manager_v1_destroy mock_manager_destroy
#define wl_output_destroy mock_output_destroy
#define wl_display_prepare_read mock_prepare_read
#define wl_display_flush mock_flush
#define wl_display_get_fd mock_get_fd
#define wl_display_cancel_read mock_cancel_read
#define wl_display_dispatch_pending mock_dispatch
#define wl_display_get_error mock_get_error
#define wl_display_disconnect mock_disconnect
#include "hypr_temperature.c"

static void add_output(hypr_temperature *handle, uint32_t name)
{
    global(handle, NULL, name, "wl_output", 4);
    assert(bound_version == 1);
    ++expected_outputs;
}

static void test_matrices_and_hotplug(void)
{
    hypr_temperature *handle = calloc(1, sizeof(*handle));
    assert(handle);
    next_manager = owner;
    global(handle, NULL, 1, "hyprland_ctm_control_manager_v1", 2);
    assert(bound_version == 2);
    assert(hypr_temperature_is_available(handle));
    assert(!hypr_temperature_is_active(handle));
    add_output(handle, 10);
    add_output(handle, 11);
    assert(hypr_temperature_poll(handle));
    assert(commit_count == 0);
    assert(hypr_temperature_set_temperature(handle, 2000));
    assert(hypr_temperature_is_active(handle));
    /* Fixed-point values of the independent 2000 K reference (1,.537,.0545). */
    assert(last_matrix[0] == 256);
    assert(last_matrix[4] == 137);
    assert(last_matrix[8] == 14);

    int previous_green = 0;
    int previous_blue = 0;
    for (int kelvin = 2000; kelvin <= 6500; ++kelvin)
    {
        assert(hypr_temperature_set_temperature(handle, kelvin));
        assert(last_matrix[4] >= previous_green);
        assert(last_matrix[8] >= previous_blue);
        previous_green = last_matrix[4];
        previous_blue = last_matrix[8];
    }

    assert(last_matrix[4] == 256 && last_matrix[8] == 256);
    assert(!hypr_temperature_is_active(handle));
    int commits = commit_count;
    assert(!hypr_temperature_set_temperature(handle, 1999));
    assert(!hypr_temperature_set_temperature(handle, 6501));
    assert(commit_count == commits && handle->kelvin == 6500);
    assert(strstr(hypr_temperature_get_error(handle), "between"));
    assert(hypr_temperature_set_temperature(handle, 4000));
    assert(!*hypr_temperature_get_error(handle));
    wl_fixed_t warm_matrix[9];
    memcpy(warm_matrix, last_matrix, sizeof(warm_matrix));

    add_output(handle, 12);
    assert(hypr_temperature_poll(handle));
    assert(memcmp(warm_matrix, last_matrix, sizeof(warm_matrix)) == 0);
    global_remove(handle, NULL, 11);
    --expected_outputs;
    assert(hypr_temperature_poll(handle));
    assert(output_destroy_count == 1);
    commits = commit_count;
    global_remove(handle, NULL, 999);
    assert(hypr_temperature_poll(handle));
    assert(commit_count == commits);
    global_remove(handle, NULL, 10);
    global_remove(handle, NULL, 12);
    expected_outputs = 0;
    assert(hypr_temperature_poll(handle));
    assert(!hypr_temperature_is_active(handle));
    assert(hypr_temperature_is_available(handle));
    add_output(handle, 13);
    assert(hypr_temperature_poll(handle));
    assert(hypr_temperature_is_active(handle));
    assert(memcmp(warm_matrix, last_matrix, sizeof(warm_matrix)) == 0);

    global_remove(handle, NULL, 1);
    assert(!hypr_temperature_is_available(handle));
    assert(!hypr_temperature_set_temperature(handle, 3000));
    assert(handle->kelvin == 3000);
    global(handle, NULL, 2, "hyprland_ctm_control_manager_v1", 2);
    assert(hypr_temperature_poll(handle));
    assert(hypr_temperature_is_active(handle));
    assert(last_matrix[8] < warm_matrix[8]);

    /* Backpressure is retryable, a broken connection is terminal and cannot
     * masquerade as available to managed code deciding whether to recreate. */
    flush_error = EAGAIN;
    assert(hypr_temperature_poll(handle));
    assert(hypr_temperature_is_available(handle));
    flush_error = EPIPE;
    assert(!hypr_temperature_poll(handle));
    assert(!hypr_temperature_is_available(handle));
    assert(!hypr_temperature_is_active(handle));
    char diagnostic[256];
    snprintf(diagnostic, sizeof(diagnostic), "%s",
             hypr_temperature_get_error(handle));
    assert(!hypr_temperature_set_temperature(handle, 0));
    assert(strcmp(diagnostic, hypr_temperature_get_error(handle)) == 0);
    flush_error = 0;
    hypr_temperature_destroy(handle);
    expected_outputs = 0;
}

static void test_blocked_retry_and_v1(void)
{
    int resets = reset_count;
    int destroys = destroy_count;
    int commits = commit_count;
    hypr_temperature *handle = calloc(1, sizeof(*handle));
    assert(handle);
    next_manager = (void *)(uintptr_t)2;
    global(handle, NULL, 1, "hyprland_ctm_control_manager_v1", 2);
    blocked(handle, handle->manager);
    assert(!hypr_temperature_is_available(handle));
    assert(!hypr_temperature_is_active(handle));
    assert(destroy_count == destroys + 1);
    assert(reset_count == resets);
    assert(strstr(hypr_temperature_get_error(handle), "blocked"));
    assert(!hypr_temperature_set_temperature(handle, 3500));
    assert(hypr_temperature_poll(handle));
    assert(commit_count == commits);
    int binds = bind_count;
    global(handle, NULL, 1, "hyprland_ctm_control_manager_v1", 2);
    assert(bind_count == binds);
    hypr_temperature_destroy(handle);
    assert(reset_count == resets && destroy_count == destroys + 1);

    /* A fresh handle is the supported retry path after the other owner exits.
     */
    handle = calloc(1, sizeof(*handle));
    assert(handle);
    next_manager = owner;
    global(handle, NULL, 1, "hyprland_ctm_control_manager_v1", 2);
    assert(hypr_temperature_is_available(handle));
    hypr_temperature_destroy(handle);
    assert(reset_count == resets + 1);

    handle = calloc(1, sizeof(*handle));
    assert(handle);
    binds = bind_count;
    global(handle, NULL, 1, "hyprland_ctm_control_manager_v1", 1);
    assert(bind_count == binds);
    assert(!hypr_temperature_is_available(handle));
    assert(strstr(hypr_temperature_get_error(handle), "v2 required"));
    hypr_temperature_destroy(handle);
    assert(reset_count == resets + 1);
}

int main(void)
{
    assert(pipe(socket_fds) == 0);
    owner = (void *)(uintptr_t)1;
    test_matrices_and_hotplug();
    test_blocked_retry_and_v1();
    assert(dispatch_count > 0);
    assert(!hypr_temperature_poll(NULL));
    assert(!hypr_temperature_is_available(NULL));
    assert(!hypr_temperature_is_active(NULL));
    assert(!hypr_temperature_set_temperature(NULL, 4000));
    hypr_temperature_destroy(NULL);
    close(socket_fds[0]);
    close(socket_fds[1]);
    puts("PASS: temperature endpoints/range, complete matrix batches, hotplug, "
         "blocked retry, v1 rejection, transport failures");
    return 0;
}
