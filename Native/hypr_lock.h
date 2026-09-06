#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hypr_lock hypr_lock;

enum hypr_lock_state {
    HYPR_LOCK_STATE_ACQUIRING = 0,
    HYPR_LOCK_STATE_LOCKED = 1,
    HYPR_LOCK_STATE_FINISHED = 2,
    HYPR_LOCK_STATE_UNLOCKED = 3,
    HYPR_LOCK_STATE_ERROR = 4,
};

enum hypr_lock_auth_state {
    HYPR_LOCK_AUTH_IDLE = 0,
    HYPR_LOCK_AUTH_PENDING = 1,
    HYPR_LOCK_AUTH_SUCCESS = 2,
    HYPR_LOCK_AUTH_DENIED = 3,
    HYPR_LOCK_AUTH_ERROR = 4,
};

hypr_lock* hypr_lock_create(const char* pam_service);
void hypr_lock_destroy(hypr_lock* lock);

int hypr_lock_poll_events(hypr_lock* lock);
int hypr_lock_get_state(const hypr_lock* lock);
int hypr_lock_has_error(const hypr_lock* lock);

uint64_t hypr_lock_get_topology_serial(const hypr_lock* lock);
int hypr_lock_get_surface_count(const hypr_lock* lock);
uint64_t hypr_lock_get_surface_id(const hypr_lock* lock, int index);
int hypr_lock_get_surface_width(const hypr_lock* lock, uint64_t id);
int hypr_lock_get_surface_height(const hypr_lock* lock, uint64_t id);
int hypr_lock_get_surface_name(const hypr_lock* lock, uint64_t id, char* buffer, int buffer_size);

int hypr_lock_make_current(hypr_lock* lock, uint64_t id);
int hypr_lock_swap_buffers(hypr_lock* lock, uint64_t id);

int hypr_lock_get_password_length(hypr_lock* lock);
int hypr_lock_get_auth_state(hypr_lock* lock);
int hypr_lock_unlock(hypr_lock* lock);

#ifdef __cplusplus
}
#endif
