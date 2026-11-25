#ifndef HYPR_SU_H
#define HYPR_SU_H

#ifdef __cplusplus
extern "C"
{
#endif

    /* Only pass trusted, allowlisted, silent shell commands. Serialize calls
     * per handle. The caller must poll to enforce the deadline and must not
     * reap the su child itself. */
    void *hypr_su_start(const char *command);
    /* 0: pending, 1: needs password, 2: success, -1: failure. */
    int hypr_su_poll(void *handle);
    /* Accepts one response, up to 255 bytes, with no ASCII control characters.
     */
    int hypr_su_answer(void *handle, const char *response);
    /* Cancels/reaps and frees any non-NULL handle. May block during cleanup. */
    void hypr_su_destroy(void *handle);

#ifdef __cplusplus
}
#endif

#endif
