#ifndef HYPR_POLKIT_H
#define HYPR_POLKIT_H

#include <stddef.h>
#include <stdint.h>

/* Starts a background registration attempt. A non-NULL handle owns the worker
 * even if registration fails; it must be destroyed. */
void *hypr_polkit_start(void);

/* Cancels authentication and joins the worker. Do not overlap destruction with
 * other calls on the handle. */
void hypr_polkit_destroy(void *agent);

/* Copies the current plain-text prompt, including its NUL terminator, and
 * returns its generation. Returns 0 if unavailable or if the buffer is too
 * small. Polling the same unanswered prompt returns the same generation. */
uint64_t hypr_polkit_prompt(void *agent, char *message, size_t capacity);

/* Only the first answer for the current generation is accepted. NULL cancels.
 * The response is copied before return; callers own and should wipe their copy.
 */
void hypr_polkit_answer(void *agent, uint64_t generation, const char *response);

#endif
