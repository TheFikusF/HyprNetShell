#ifndef HYPR_TEMPERATURE_H
#define HYPR_TEMPERATURE_H

#ifdef __cplusplus
extern "C"
{
#endif

    typedef struct hypr_temperature hypr_temperature;

    /* Each handle owns an independent connection. All calls, including
     * destruction, must run on its creating thread. Creation synchronizes for
     * at most 2 seconds. Requires CTM v2 for reliable ownership detection.
     * Unsupported/blocked compositors return NULL; get_error(NULL) is
     * thread-local. */
    hypr_temperature *hypr_temperature_create(void);
    /* Sends the manager destructor (resetting CTMs), flushing for at most 200
     * ms. */
    void hypr_temperature_destroy(hypr_temperature *handle);
    /* Nonblocking event/flush pump. Call regularly, including while
     * unavailable. Returns 1 on transport success, 0 on failure. Unavailability
     * is not failure. */
    int hypr_temperature_poll(hypr_temperature *handle);
    /* Accepts 2000..6500 K; 6500 is exact identity and disables active state.
     * Returns 1 when queued on an available manager, not compositor
     * acknowledgement. Valid requests are remembered while unavailable and
     * reapplied on hotplug. Returns 0 for invalid input, unavailability, or
     * connection failure. */
    int hypr_temperature_set_temperature(hypr_temperature *handle, int kelvin);
    /* Cached state, refreshed by poll. Available means manager present/not
     * blocked; active additionally requires a nonidentity request and at least
     * one output. When unavailable, destroy and recreate the handle to retry
     * ownership; blocked managers are never automatically rebound. */
    int hypr_temperature_is_available(hypr_temperature *handle);
    int hypr_temperature_is_active(hypr_temperature *handle);
    /* Borrowed diagnostic, empty when none; never writes to stderr or logs.
     * Handle diagnostics persist until a successful set, or another error. */
    const char *hypr_temperature_get_error(hypr_temperature *handle);

#ifdef __cplusplus
}
#endif

#endif
