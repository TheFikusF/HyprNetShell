#define _POSIX_C_SOURCE 200809L

#include "hypr_lock.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <errno.h>
#include <poll.h>
#include <pthread.h>
#include <pwd.h>
#include <security/pam_appl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <xkbcommon/xkbcommon.h>

#include "ext-session-lock-v1-client-protocol.h"

#define PASSWORD_CAPACITY 512
#define USERNAME_CAPACITY 256
#define PAM_SERVICE_CAPACITY 64

struct lock_output {
    struct hypr_lock* lock;
    struct lock_output* next;
    uint32_t registry_name;
    uint32_t output_version;
    uint64_t id;
    struct wl_output* output;
    char* output_name;
    char fallback_output_name[32];
    struct wl_surface* surface;
    struct ext_session_lock_surface_v1* lock_surface;
    struct wl_egl_window* egl_window;
    EGLSurface egl_surface;
    uint32_t pending_serial;
    int width;
    int height;
    int configured;
};

struct hypr_lock {
    struct wl_display* display;
    struct wl_registry* registry;
    struct wl_compositor* compositor;
    struct wl_seat* seat;
    uint32_t seat_name;
    uint32_t seat_version;
    struct wl_keyboard* keyboard;
    struct ext_session_lock_manager_v1* manager;
    struct ext_session_lock_v1* session_lock;

    struct xkb_context* xkb_context;
    struct xkb_keymap* xkb_keymap;
    struct xkb_state* xkb_state;

    EGLDisplay egl_display;
    EGLConfig egl_config;
    EGLContext egl_context;
    EGLSurface fallback_surface;

    struct lock_output* outputs;
    struct lock_output* keyboard_focus;
    uint64_t next_output_id;
    uint64_t topology_serial;
    int state;
    int locked_received;
    int has_error;

    pthread_mutex_t auth_mutex;
    int auth_mutex_initialized;
    pthread_t auth_thread;
    int auth_thread_started;
    int auth_thread_active;
    int auth_state;
    int64_t next_auth_time_ms;
    char password[PASSWORD_CAPACITY + 1];
    size_t password_bytes;
    int password_characters;
    char auth_password[PASSWORD_CAPACITY + 1];
    char username[USERNAME_CAPACITY];
    char pam_service[PAM_SERVICE_CAPACITY];
};

static const struct wl_seat_listener seat_listener;
static const struct ext_session_lock_surface_v1_listener lock_surface_listener;

static void secure_zero(void* memory, size_t size) {
    volatile unsigned char* bytes = memory;
    while (size-- > 0) {
        *bytes++ = 0;
    }
}

static int64_t monotonic_milliseconds(void) {
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (int64_t)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}

static void fail(struct hypr_lock* lock, const char* message) {
    fprintf(stderr, "hypr_lock: %s\n", message);
    if (lock != NULL) {
        lock->has_error = 1;
        lock->state = HYPR_LOCK_STATE_ERROR;
    }
}

static void fail_egl(struct hypr_lock* lock, const char* message) {
    fprintf(stderr, "hypr_lock: %s (EGL error 0x%04x)\n", message, eglGetError());
    if (lock != NULL) {
        lock->has_error = 1;
        lock->state = HYPR_LOCK_STATE_ERROR;
    }
}

static struct lock_output* find_output(const struct hypr_lock* lock, uint64_t id) {
    if (lock == NULL || id == 0) {
        return NULL;
    }
    for (struct lock_output* output = lock->outputs; output != NULL; output = output->next) {
        if (output->id == id && output->configured && output->egl_surface != EGL_NO_SURFACE) {
            return output;
        }
    }
    return NULL;
}

static struct lock_output* find_output_by_surface(
    const struct hypr_lock* lock,
    const struct wl_surface* surface) {
    for (struct lock_output* output = lock->outputs; output != NULL; output = output->next) {
        if (output->surface == surface) {
            return output;
        }
    }
    return NULL;
}

static int make_fallback_current(struct hypr_lock* lock) {
    if (lock == NULL || lock->egl_display == EGL_NO_DISPLAY ||
        lock->egl_context == EGL_NO_CONTEXT || lock->fallback_surface == EGL_NO_SURFACE) {
        return 0;
    }
    if (!eglMakeCurrent(
            lock->egl_display,
            lock->fallback_surface,
            lock->fallback_surface,
            lock->egl_context)) {
        fail_egl(lock, "failed to make fallback EGL surface current");
        return 0;
    }
    return 1;
}

static void destroy_output_graphics(struct lock_output* output) {
    struct hypr_lock* lock = output->lock;
    if (output->egl_surface != EGL_NO_SURFACE && lock->egl_display != EGL_NO_DISPLAY) {
        make_fallback_current(lock);
        eglDestroySurface(lock->egl_display, output->egl_surface);
        output->egl_surface = EGL_NO_SURFACE;
    }
    if (output->egl_window != NULL) {
        wl_egl_window_destroy(output->egl_window);
        output->egl_window = NULL;
    }
}

static void destroy_output(struct lock_output* output, int send_requests) {
    if (output == NULL) {
        return;
    }
    if (output->lock->keyboard_focus == output) {
        output->lock->keyboard_focus = NULL;
    }
    destroy_output_graphics(output);
    if (send_requests && output->lock_surface != NULL) {
        ext_session_lock_surface_v1_destroy(output->lock_surface);
    }
    if (send_requests && output->surface != NULL) {
        wl_surface_destroy(output->surface);
    }
    if (send_requests && output->output != NULL) {
        if (output->output_version >= WL_OUTPUT_RELEASE_SINCE_VERSION) {
            wl_output_release(output->output);
        } else {
            wl_output_destroy(output->output);
        }
    }
    free(output->output_name);
    free(output);
}

static void create_lock_surface(struct lock_output* output) {
    struct hypr_lock* lock = output->lock;
    if (lock->session_lock == NULL || output->surface != NULL) {
        return;
    }
    output->surface = wl_compositor_create_surface(lock->compositor);
    if (output->surface == NULL) {
        fail(lock, "failed to create a lock wl_surface");
        return;
    }
    output->lock_surface = ext_session_lock_v1_get_lock_surface(
        lock->session_lock,
        output->surface,
        output->output);
    if (output->lock_surface == NULL ||
        ext_session_lock_surface_v1_add_listener(
            output->lock_surface,
            &lock_surface_listener,
            output) < 0) {
        fail(lock, "failed to create a session lock surface");
    }
}

static void lock_surface_configure(
    void* data,
    struct ext_session_lock_surface_v1* lock_surface,
    uint32_t serial,
    uint32_t width,
    uint32_t height) {
    struct lock_output* output = data;
    struct hypr_lock* lock = output->lock;
    if (width == 0 || height == 0 || width > INT32_MAX || height > INT32_MAX) {
        fail(lock, "compositor supplied invalid lock surface dimensions");
        return;
    }

    ext_session_lock_surface_v1_ack_configure(lock_surface, serial);
    output->pending_serial = serial;
    int dimensions_changed = output->width != (int)width || output->height != (int)height;
    output->width = (int)width;
    output->height = (int)height;

    if (output->egl_window == NULL) {
        output->egl_window = wl_egl_window_create(output->surface, output->width, output->height);
        if (output->egl_window == NULL) {
            fail(lock, "failed to create a lock EGL window");
            return;
        }
        output->egl_surface = eglCreateWindowSurface(
            lock->egl_display,
            lock->egl_config,
            (EGLNativeWindowType)output->egl_window,
            NULL);
        if (output->egl_surface == EGL_NO_SURFACE) {
            fail_egl(lock, "failed to create a lock EGL surface");
            return;
        }
    } else if (dimensions_changed) {
        wl_egl_window_resize(output->egl_window, output->width, output->height, 0, 0);
    }

    if (!output->configured || dimensions_changed) {
        output->configured = 1;
        lock->topology_serial++;
    }
}

static const struct ext_session_lock_surface_v1_listener lock_surface_listener = {
    .configure = lock_surface_configure,
};

static void session_locked(void* data, struct ext_session_lock_v1* session_lock) {
    (void)session_lock;
    struct hypr_lock* lock = data;
    lock->locked_received = 1;
    lock->state = HYPR_LOCK_STATE_LOCKED;
}

static void session_finished(void* data, struct ext_session_lock_v1* session_lock) {
    struct hypr_lock* lock = data;
    if (lock->locked_received) {
        ext_session_lock_v1_unlock_and_destroy(session_lock);
    } else {
        ext_session_lock_v1_destroy(session_lock);
    }
    lock->session_lock = NULL;
    lock->state = HYPR_LOCK_STATE_FINISHED;
}

static const struct ext_session_lock_v1_listener session_lock_listener = {
    .locked = session_locked,
    .finished = session_finished,
};

static void release_keyboard(struct hypr_lock* lock) {
    if (lock->keyboard == NULL) {
        return;
    }
    if (lock->seat_version >= WL_KEYBOARD_RELEASE_SINCE_VERSION) {
        wl_keyboard_release(lock->keyboard);
    } else {
        wl_keyboard_destroy(lock->keyboard);
    }
    lock->keyboard = NULL;
    lock->keyboard_focus = NULL;
}

static void release_seat(struct hypr_lock* lock) {
    if (lock->seat == NULL) {
        return;
    }
    release_keyboard(lock);
    if (lock->seat_version >= WL_SEAT_RELEASE_SINCE_VERSION) {
        wl_seat_release(lock->seat);
    } else {
        wl_seat_destroy(lock->seat);
    }
    lock->seat = NULL;
    lock->seat_name = 0;
    lock->seat_version = 0;
}

static void lock_output_geometry(
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
    (void)data; (void)output; (void)x; (void)y; (void)physical_width; (void)physical_height;
    (void)subpixel; (void)make; (void)model; (void)transform;
}

static void lock_output_mode(
    void* data,
    struct wl_output* output,
    uint32_t flags,
    int32_t width,
    int32_t height,
    int32_t refresh) {
    (void)data; (void)output; (void)flags; (void)width; (void)height; (void)refresh;
}

static void lock_output_done(void* data, struct wl_output* output) {
    (void)data; (void)output;
}

static void lock_output_scale(void* data, struct wl_output* output, int32_t factor) {
    (void)data; (void)output; (void)factor;
}

static void lock_output_name(void* data, struct wl_output* output, const char* name) {
    (void)output;
    struct lock_output* lock_output = data;
    char* replacement = name != NULL ? strdup(name) : NULL;
    if (name != NULL && replacement == NULL) {
        fail(lock_output->lock, "failed to copy lock output name");
        return;
    }
    free(lock_output->output_name);
    lock_output->output_name = replacement;
}

static void lock_output_description(void* data, struct wl_output* output, const char* description) {
    (void)data; (void)output; (void)description;
}

static const struct wl_output_listener output_listener = {
    .geometry = lock_output_geometry,
    .mode = lock_output_mode,
    .done = lock_output_done,
    .scale = lock_output_scale,
    .name = lock_output_name,
    .description = lock_output_description,
};

static void registry_global(
    void* data,
    struct wl_registry* registry,
    uint32_t name,
    const char* interface,
    uint32_t version) {
    struct hypr_lock* lock = data;
    if (strcmp(interface, wl_compositor_interface.name) == 0 && lock->compositor == NULL) {
        lock->compositor = wl_registry_bind(
            registry,
            name,
            &wl_compositor_interface,
            version < 4 ? version : 4);
    } else if (strcmp(interface, ext_session_lock_manager_v1_interface.name) == 0 &&
               lock->manager == NULL) {
        lock->manager = wl_registry_bind(
            registry,
            name,
            &ext_session_lock_manager_v1_interface,
            1);
    } else if (strcmp(interface, wl_seat_interface.name) == 0 && lock->seat == NULL) {
        lock->seat_version = version < 5 ? version : 5;
        lock->seat_name = name;
        lock->seat = wl_registry_bind(registry, name, &wl_seat_interface, lock->seat_version);
        if (lock->seat == NULL || wl_seat_add_listener(lock->seat, &seat_listener, lock) < 0) {
            fail(lock, "failed to initialize the Wayland seat");
        }
    } else if (strcmp(interface, wl_output_interface.name) == 0) {
        struct lock_output* output = calloc(1, sizeof(struct lock_output));
        if (output == NULL) {
            fail(lock, "out of memory while tracking a lock output");
            return;
        }
        output->lock = lock;
        output->registry_name = name;
        output->output_version = version < 4 ? version : 4;
        output->id = lock->next_output_id++;
        output->egl_surface = EGL_NO_SURFACE;
        snprintf(output->fallback_output_name, sizeof(output->fallback_output_name), "wl-output-%u", name);
        output->output = wl_registry_bind(registry, name, &wl_output_interface, output->output_version);
        if (output->output == NULL || wl_output_add_listener(output->output, &output_listener, output) < 0) {
            if (output->output != NULL) {
                wl_output_destroy(output->output);
            }
            free(output);
            fail(lock, "failed to bind a lock output");
            return;
        }
        output->next = lock->outputs;
        lock->outputs = output;
        create_lock_surface(output);
    }
}

static void registry_global_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)registry;
    struct hypr_lock* lock = data;
    if (lock->seat != NULL && lock->seat_name == name) {
        release_seat(lock);
        return;
    }
    struct lock_output** link = &lock->outputs;
    while (*link != NULL) {
        struct lock_output* output = *link;
        if (output->registry_name == name) {
            *link = output->next;
            destroy_output(output, 1);
            lock->topology_serial++;
            return;
        }
        link = &output->next;
    }
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

struct pam_conversation_data {
    const char* username;
    const char* password;
    int password_supplied;
};

static int pam_conversation(
    int message_count,
    const struct pam_message** messages,
    struct pam_response** responses,
    void* data) {
    if (message_count <= 0 || messages == NULL || responses == NULL || data == NULL) {
        return PAM_CONV_ERR;
    }
    struct pam_conversation_data* conversation = data;
    struct pam_response* result = calloc((size_t)message_count, sizeof(struct pam_response));
    if (result == NULL) {
        return PAM_BUF_ERR;
    }

    for (int index = 0; index < message_count; index++) {
        switch (messages[index]->msg_style) {
            case PAM_PROMPT_ECHO_OFF:
                if (conversation->password_supplied) {
                    goto conversation_error;
                }
                result[index].resp = strdup(conversation->password);
                conversation->password_supplied = 1;
                break;
            case PAM_PROMPT_ECHO_ON:
                result[index].resp = strdup(conversation->username);
                break;
            case PAM_ERROR_MSG:
            case PAM_TEXT_INFO:
                result[index].resp = NULL;
                break;
            default:
                goto conversation_error;
        }
        if ((messages[index]->msg_style == PAM_PROMPT_ECHO_OFF ||
             messages[index]->msg_style == PAM_PROMPT_ECHO_ON) && result[index].resp == NULL) {
            goto conversation_error;
        }
    }
    *responses = result;
    return PAM_SUCCESS;

conversation_error:
    for (int index = 0; index < message_count; index++) {
        if (result[index].resp != NULL) {
            secure_zero(result[index].resp, strlen(result[index].resp));
            free(result[index].resp);
        }
    }
    free(result);
    return PAM_CONV_ERR;
}

static void* authentication_worker(void* data) {
    struct hypr_lock* lock = data;
    struct pam_conversation_data conversation_data = {
        .username = lock->username,
        .password = lock->auth_password,
        .password_supplied = 0,
    };
    const struct pam_conv conversation = {
        .conv = pam_conversation,
        .appdata_ptr = &conversation_data,
    };
    pam_handle_t* handle = NULL;
    int result = pam_start(lock->pam_service, lock->username, &conversation, &handle);
    if (result == PAM_SUCCESS) {
        result = pam_authenticate(handle, 0);
    }
    if (result == PAM_SUCCESS) {
        result = pam_acct_mgmt(handle, 0);
    }
    if (handle != NULL) {
        pam_end(handle, result);
    }

    pthread_mutex_lock(&lock->auth_mutex);
    secure_zero(lock->auth_password, sizeof(lock->auth_password));
    if (result == PAM_SUCCESS) {
        lock->auth_state = HYPR_LOCK_AUTH_SUCCESS;
    } else if (result == PAM_AUTH_ERR || result == PAM_USER_UNKNOWN ||
               result == PAM_MAXTRIES || result == PAM_CRED_INSUFFICIENT ||
               result == PAM_ACCT_EXPIRED || result == PAM_NEW_AUTHTOK_REQD) {
        lock->auth_state = HYPR_LOCK_AUTH_DENIED;
        lock->next_auth_time_ms = monotonic_milliseconds() + 1500;
    } else {
        lock->auth_state = HYPR_LOCK_AUTH_ERROR;
        lock->next_auth_time_ms = monotonic_milliseconds() + 3000;
    }
    lock->auth_thread_active = 0;
    pthread_mutex_unlock(&lock->auth_mutex);
    return NULL;
}

static void clear_password(struct hypr_lock* lock) {
    secure_zero(lock->password, sizeof(lock->password));
    lock->password_bytes = 0;
    lock->password_characters = 0;
}

static void begin_authentication(struct hypr_lock* lock) {
    pthread_mutex_lock(&lock->auth_mutex);
    if (lock->auth_thread_active || lock->password_bytes == 0 ||
        monotonic_milliseconds() < lock->next_auth_time_ms) {
        pthread_mutex_unlock(&lock->auth_mutex);
        return;
    }
    if (lock->auth_thread_started) {
        pthread_t previous = lock->auth_thread;
        lock->auth_thread_started = 0;
        pthread_mutex_unlock(&lock->auth_mutex);
        pthread_join(previous, NULL);
        pthread_mutex_lock(&lock->auth_mutex);
    }
    memcpy(lock->auth_password, lock->password, lock->password_bytes + 1);
    clear_password(lock);
    lock->auth_state = HYPR_LOCK_AUTH_PENDING;
    lock->auth_thread_active = 1;
    if (pthread_create(&lock->auth_thread, NULL, authentication_worker, lock) != 0) {
        secure_zero(lock->auth_password, sizeof(lock->auth_password));
        lock->auth_thread_active = 0;
        lock->auth_state = HYPR_LOCK_AUTH_ERROR;
    } else {
        lock->auth_thread_started = 1;
    }
    pthread_mutex_unlock(&lock->auth_mutex);
}

static void keyboard_keymap(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t format,
    int32_t fd,
    uint32_t size) {
    (void)keyboard;
    struct hypr_lock* lock = data;
    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1) {
        close(fd);
        return;
    }
    char* keymap_text = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (keymap_text == MAP_FAILED) {
        fail(lock, "failed to map the keyboard keymap");
        return;
    }
    struct xkb_keymap* keymap = xkb_keymap_new_from_string(
        lock->xkb_context,
        keymap_text,
        XKB_KEYMAP_FORMAT_TEXT_V1,
        XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(keymap_text, size);
    if (keymap == NULL) {
        fail(lock, "failed to compile the keyboard keymap");
        return;
    }
    struct xkb_state* state = xkb_state_new(keymap);
    if (state == NULL) {
        xkb_keymap_unref(keymap);
        fail(lock, "failed to create keyboard state");
        return;
    }
    if (lock->xkb_state != NULL) {
        xkb_state_unref(lock->xkb_state);
    }
    if (lock->xkb_keymap != NULL) {
        xkb_keymap_unref(lock->xkb_keymap);
    }
    lock->xkb_keymap = keymap;
    lock->xkb_state = state;
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
    struct hypr_lock* lock = data;
    lock->keyboard_focus = find_output_by_surface(lock, surface);
}

static void keyboard_leave(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    struct wl_surface* surface) {
    (void)keyboard;
    (void)serial;
    struct hypr_lock* lock = data;
    if (lock->keyboard_focus == find_output_by_surface(lock, surface)) {
        lock->keyboard_focus = NULL;
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
    struct hypr_lock* lock = data;
    if (state != WL_KEYBOARD_KEY_STATE_PRESSED || lock->keyboard_focus == NULL) {
        return;
    }
    if (key == 28) {
        begin_authentication(lock);
        return;
    }

    pthread_mutex_lock(&lock->auth_mutex);
    if (lock->auth_thread_active) {
        pthread_mutex_unlock(&lock->auth_mutex);
        return;
    }
    if (key == 14) {
        if (lock->password_bytes > 0) {
            size_t offset = lock->password_bytes - 1;
            while (offset > 0 && ((unsigned char)lock->password[offset] & 0xc0) == 0x80) {
                offset--;
            }
            secure_zero(lock->password + offset, lock->password_bytes - offset);
            lock->password_bytes = offset;
            lock->password[lock->password_bytes] = '\0';
            if (lock->password_characters > 0) {
                lock->password_characters--;
            }
        }
        lock->auth_state = HYPR_LOCK_AUTH_IDLE;
        pthread_mutex_unlock(&lock->auth_mutex);
        return;
    }
    if (key == 1) {
        clear_password(lock);
        lock->auth_state = HYPR_LOCK_AUTH_IDLE;
        pthread_mutex_unlock(&lock->auth_mutex);
        return;
    }
    if (lock->xkb_state != NULL) {
        char text[64];
        int length = xkb_state_key_get_utf8(lock->xkb_state, key + 8, text, sizeof(text));
        if (length > 0 && (unsigned char)text[0] >= 0x20 && text[0] != 0x7f &&
            lock->password_bytes + (size_t)length <= PASSWORD_CAPACITY) {
            memcpy(lock->password + lock->password_bytes, text, (size_t)length);
            lock->password_bytes += (size_t)length;
            lock->password[lock->password_bytes] = '\0';
            lock->password_characters++;
            lock->auth_state = HYPR_LOCK_AUTH_IDLE;
        }
        secure_zero(text, sizeof(text));
    }
    pthread_mutex_unlock(&lock->auth_mutex);
}

static void keyboard_modifiers(
    void* data,
    struct wl_keyboard* keyboard,
    uint32_t serial,
    uint32_t depressed,
    uint32_t latched,
    uint32_t locked,
    uint32_t group) {
    (void)keyboard;
    (void)serial;
    struct hypr_lock* lock = data;
    if (lock->xkb_state != NULL) {
        xkb_state_update_mask(lock->xkb_state, depressed, latched, locked, 0, 0, group);
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
    struct hypr_lock* lock = data;
    int has_keyboard = (capabilities & WL_SEAT_CAPABILITY_KEYBOARD) != 0;
    if (has_keyboard && lock->keyboard == NULL) {
        lock->keyboard = wl_seat_get_keyboard(seat);
        if (lock->keyboard == NULL ||
            wl_keyboard_add_listener(lock->keyboard, &keyboard_listener, lock) < 0) {
            fail(lock, "failed to initialize lock keyboard input");
        }
    } else if (!has_keyboard && lock->keyboard != NULL) {
        release_keyboard(lock);
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

static int initialize_identity(struct hypr_lock* lock, const char* pam_service) {
    if (pam_service == NULL || pam_service[0] == '\0' || strlen(pam_service) >= sizeof(lock->pam_service)) {
        return 0;
    }
    memcpy(lock->pam_service, pam_service, strlen(pam_service) + 1);

    struct passwd entry;
    struct passwd* result = NULL;
    long suggested_size = sysconf(_SC_GETPW_R_SIZE_MAX);
    size_t buffer_size = suggested_size > 0 ? (size_t)suggested_size : 16384;
    char* buffer = malloc(buffer_size);
    if (buffer == NULL) {
        return 0;
    }
    int error = getpwuid_r(getuid(), &entry, buffer, buffer_size, &result);
    if (error != 0 || result == NULL || result->pw_name == NULL ||
        strlen(result->pw_name) >= sizeof(lock->username)) {
        free(buffer);
        return 0;
    }
    memcpy(lock->username, result->pw_name, strlen(result->pw_name) + 1);
    free(buffer);
    return 1;
}

static int initialize_egl(struct hypr_lock* lock) {
    PFNEGLGETPLATFORMDISPLAYEXTPROC get_platform_display =
        (PFNEGLGETPLATFORMDISPLAYEXTPROC)eglGetProcAddress("eglGetPlatformDisplayEXT");
    lock->egl_display = get_platform_display != NULL
        ? get_platform_display(EGL_PLATFORM_WAYLAND_EXT, lock->display, NULL)
        : eglGetDisplay((EGLNativeDisplayType)lock->display);
    if (lock->egl_display == EGL_NO_DISPLAY || !eglInitialize(lock->egl_display, NULL, NULL) ||
        !eglBindAPI(EGL_OPENGL_API)) {
        fail_egl(lock, "failed to initialize EGL");
        return 0;
    }
    const EGLint config_attributes[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT | EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_NONE,
    };
    EGLint count = 0;
    if (!eglChooseConfig(lock->egl_display, config_attributes, &lock->egl_config, 1, &count) || count == 0) {
        fail_egl(lock, "no suitable lock EGL config is available");
        return 0;
    }
    const EGLint context_attributes[] = {
        EGL_CONTEXT_MAJOR_VERSION, 3,
        EGL_CONTEXT_MINOR_VERSION, 3,
        EGL_NONE,
    };
    lock->egl_context = eglCreateContext(
        lock->egl_display,
        lock->egl_config,
        EGL_NO_CONTEXT,
        context_attributes);
    if (lock->egl_context == EGL_NO_CONTEXT) {
        fail_egl(lock, "failed to create lock EGL context");
        return 0;
    }
    const EGLint pbuffer_attributes[] = {
        EGL_WIDTH, 1,
        EGL_HEIGHT, 1,
        EGL_NONE,
    };
    lock->fallback_surface = eglCreatePbufferSurface(
        lock->egl_display,
        lock->egl_config,
        pbuffer_attributes);
    if (lock->fallback_surface == EGL_NO_SURFACE) {
        fail_egl(lock, "failed to create lock fallback EGL surface");
        return 0;
    }
    return make_fallback_current(lock);
}

hypr_lock* hypr_lock_create(const char* pam_service) {
    struct hypr_lock* lock = calloc(1, sizeof(struct hypr_lock));
    if (lock == NULL) {
        return NULL;
    }
    lock->egl_display = EGL_NO_DISPLAY;
    lock->egl_context = EGL_NO_CONTEXT;
    lock->fallback_surface = EGL_NO_SURFACE;
    lock->next_output_id = 1;
    lock->topology_serial = 1;
    lock->state = HYPR_LOCK_STATE_ACQUIRING;
    lock->auth_state = HYPR_LOCK_AUTH_IDLE;

    if (pthread_mutex_init(&lock->auth_mutex, NULL) != 0) {
        free(lock);
        return NULL;
    }
    lock->auth_mutex_initialized = 1;
    (void)mlock(lock->password, sizeof(lock->password));
    (void)mlock(lock->auth_password, sizeof(lock->auth_password));

    if (!initialize_identity(lock, pam_service)) {
        fail(lock, "failed to resolve the current user or PAM service");
        hypr_lock_destroy(lock);
        return NULL;
    }
    lock->xkb_context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    if (lock->xkb_context == NULL) {
        fail(lock, "failed to create xkb context");
        hypr_lock_destroy(lock);
        return NULL;
    }
    lock->display = wl_display_connect(NULL);
    if (lock->display == NULL) {
        fail(lock, "failed to connect to the Wayland compositor");
        hypr_lock_destroy(lock);
        return NULL;
    }
    lock->registry = wl_display_get_registry(lock->display);
    if (lock->registry == NULL || wl_registry_add_listener(lock->registry, &registry_listener, lock) < 0 ||
        wl_display_roundtrip(lock->display) < 0) {
        fail(lock, "failed to initialize the Wayland registry");
        hypr_lock_destroy(lock);
        return NULL;
    }
    if (lock->compositor == NULL || lock->manager == NULL) {
        fail(lock, "ext-session-lock-v1 or wl_compositor is unavailable");
        hypr_lock_destroy(lock);
        return NULL;
    }
    if (!initialize_egl(lock)) {
        hypr_lock_destroy(lock);
        return NULL;
    }

    lock->session_lock = ext_session_lock_manager_v1_lock(lock->manager);
    if (lock->session_lock == NULL ||
        ext_session_lock_v1_add_listener(lock->session_lock, &session_lock_listener, lock) < 0) {
        fail(lock, "failed to request a session lock");
        hypr_lock_destroy(lock);
        return NULL;
    }
    for (struct lock_output* output = lock->outputs; output != NULL; output = output->next) {
        create_lock_surface(output);
    }
    if (wl_display_flush(lock->display) < 0 && errno != EAGAIN) {
        fail(lock, "failed to submit the session lock request");
        hypr_lock_destroy(lock);
        return NULL;
    }
    return lock;
}

void hypr_lock_destroy(hypr_lock* lock) {
    if (lock == NULL) {
        return;
    }
    if (lock->auth_thread_started) {
        pthread_join(lock->auth_thread, NULL);
        lock->auth_thread_started = 0;
    }
    secure_zero(lock->password, sizeof(lock->password));
    secure_zero(lock->auth_password, sizeof(lock->auth_password));

    int can_send_requests = lock->display != NULL;
    if (lock->egl_display != EGL_NO_DISPLAY) {
        make_fallback_current(lock);
    }
    while (lock->outputs != NULL) {
        struct lock_output* output = lock->outputs;
        lock->outputs = output->next;
        destroy_output(output, can_send_requests);
    }
    if (lock->egl_display != EGL_NO_DISPLAY) {
        eglMakeCurrent(lock->egl_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (lock->fallback_surface != EGL_NO_SURFACE) {
            eglDestroySurface(lock->egl_display, lock->fallback_surface);
        }
        if (lock->egl_context != EGL_NO_CONTEXT) {
            eglDestroyContext(lock->egl_display, lock->egl_context);
        }
        eglTerminate(lock->egl_display);
    }

    if (can_send_requests && lock->session_lock != NULL && !lock->locked_received) {
        ext_session_lock_v1_destroy(lock->session_lock);
        lock->session_lock = NULL;
    }
    if (can_send_requests && lock->manager != NULL) {
        ext_session_lock_manager_v1_destroy(lock->manager);
    }
    if (can_send_requests) {
        release_seat(lock);
        if (lock->compositor != NULL) {
            wl_compositor_destroy(lock->compositor);
        }
        if (lock->registry != NULL) {
            wl_registry_destroy(lock->registry);
        }
        wl_display_flush(lock->display);
        wl_display_disconnect(lock->display);
    }
    if (lock->xkb_state != NULL) {
        xkb_state_unref(lock->xkb_state);
    }
    if (lock->xkb_keymap != NULL) {
        xkb_keymap_unref(lock->xkb_keymap);
    }
    if (lock->xkb_context != NULL) {
        xkb_context_unref(lock->xkb_context);
    }
    munlock(lock->password, sizeof(lock->password));
    munlock(lock->auth_password, sizeof(lock->auth_password));
    secure_zero(lock->username, sizeof(lock->username));
    if (lock->auth_mutex_initialized) {
        pthread_mutex_destroy(&lock->auth_mutex);
    }
    free(lock);
}

int hypr_lock_poll_events(hypr_lock* lock) {
    if (lock == NULL || lock->display == NULL || lock->state == HYPR_LOCK_STATE_ERROR) {
        return 0;
    }
    if (wl_display_dispatch_pending(lock->display) < 0) {
        fail(lock, "failed to dispatch Wayland events");
        return 0;
    }
    while (wl_display_prepare_read(lock->display) != 0) {
        if (wl_display_dispatch_pending(lock->display) < 0) {
            fail(lock, "failed to prepare Wayland event reading");
            return 0;
        }
    }
    if (wl_display_flush(lock->display) < 0 && errno != EAGAIN) {
        wl_display_cancel_read(lock->display);
        fail(lock, "failed to flush Wayland requests");
        return 0;
    }
    struct pollfd descriptor = {
        .fd = wl_display_get_fd(lock->display),
        .events = POLLIN,
        .revents = 0,
    };
    int ready = poll(&descriptor, 1, 0);
    if (ready > 0 && (descriptor.revents & POLLIN) != 0) {
        if (wl_display_read_events(lock->display) < 0 ||
            wl_display_dispatch_pending(lock->display) < 0) {
            fail(lock, "failed to read Wayland events");
            return 0;
        }
    } else {
        wl_display_cancel_read(lock->display);
        if (ready < 0 && errno != EINTR) {
            fail(lock, "failed to poll the Wayland display");
            return 0;
        }
        if (ready > 0 && (descriptor.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            fail(lock, "Wayland connection closed while locked");
            return 0;
        }
    }
    return 1;
}

int hypr_lock_get_state(const hypr_lock* lock) {
    return lock != NULL ? lock->state : HYPR_LOCK_STATE_ERROR;
}

int hypr_lock_has_error(const hypr_lock* lock) {
    return lock == NULL || lock->has_error;
}

uint64_t hypr_lock_get_topology_serial(const hypr_lock* lock) {
    return lock != NULL ? lock->topology_serial : 0;
}

int hypr_lock_get_surface_count(const hypr_lock* lock) {
    int count = 0;
    if (lock != NULL) {
        for (const struct lock_output* output = lock->outputs; output != NULL; output = output->next) {
            if (output->configured && output->egl_surface != EGL_NO_SURFACE) {
                count++;
            }
        }
    }
    return count;
}

uint64_t hypr_lock_get_surface_id(const hypr_lock* lock, int index) {
    if (lock == NULL || index < 0) {
        return 0;
    }
    for (const struct lock_output* output = lock->outputs; output != NULL; output = output->next) {
        if (output->configured && output->egl_surface != EGL_NO_SURFACE) {
            if (index == 0) {
                return output->id;
            }
            index--;
        }
    }
    return 0;
}

int hypr_lock_get_surface_width(const hypr_lock* lock, uint64_t id) {
    struct lock_output* output = find_output(lock, id);
    return output != NULL ? output->width : 0;
}

int hypr_lock_get_surface_height(const hypr_lock* lock, uint64_t id) {
    struct lock_output* output = find_output(lock, id);
    return output != NULL ? output->height : 0;
}

int hypr_lock_get_surface_name(const hypr_lock* lock, uint64_t id, char* buffer, int buffer_size) {
    struct lock_output* output = find_output(lock, id);
    if (output == NULL || buffer == NULL || buffer_size <= 0) {
        return 0;
    }
    const char* name = output->output_name != NULL ? output->output_name : output->fallback_output_name;
    int length = (int)strlen(name);
    int copy_length = length < buffer_size - 1 ? length : buffer_size - 1;
    memcpy(buffer, name, (size_t)copy_length);
    buffer[copy_length] = '\0';
    return length;
}

int hypr_lock_make_current(hypr_lock* lock, uint64_t id) {
    if (lock == NULL) {
        return 0;
    }
    if (id == 0) {
        return make_fallback_current(lock);
    }
    struct lock_output* output = find_output(lock, id);
    if (output == NULL || !eglMakeCurrent(
            lock->egl_display,
            output->egl_surface,
            output->egl_surface,
            lock->egl_context)) {
        if (output != NULL) {
            fail_egl(lock, "failed to make a lock surface current");
        }
        return 0;
    }
    return 1;
}

int hypr_lock_swap_buffers(hypr_lock* lock, uint64_t id) {
    struct lock_output* output = find_output(lock, id);
    if (output == NULL || !eglSwapBuffers(lock->egl_display, output->egl_surface)) {
        if (output != NULL) {
            fail_egl(lock, "failed to present a lock frame");
        }
        return 0;
    }
    if (wl_display_flush(lock->display) < 0 && errno != EAGAIN) {
        fail(lock, "failed to flush a lock frame");
        return 0;
    }
    return 1;
}

int hypr_lock_get_password_length(hypr_lock* lock) {
    if (lock == NULL) {
        return 0;
    }
    pthread_mutex_lock(&lock->auth_mutex);
    int length = lock->password_characters;
    pthread_mutex_unlock(&lock->auth_mutex);
    return length;
}

int hypr_lock_get_auth_state(hypr_lock* lock) {
    if (lock == NULL) {
        return HYPR_LOCK_AUTH_ERROR;
    }
    pthread_mutex_lock(&lock->auth_mutex);
    int state = lock->auth_state;
    pthread_mutex_unlock(&lock->auth_mutex);
    return state;
}

int hypr_lock_unlock(hypr_lock* lock) {
    if (lock == NULL || lock->session_lock == NULL || !lock->locked_received) {
        return 0;
    }
    pthread_mutex_lock(&lock->auth_mutex);
    int authenticated = lock->auth_state == HYPR_LOCK_AUTH_SUCCESS;
    pthread_mutex_unlock(&lock->auth_mutex);
    if (!authenticated) {
        return 0;
    }

    ext_session_lock_v1_unlock_and_destroy(lock->session_lock);
    lock->session_lock = NULL;
    struct wl_callback* callback = wl_display_sync(lock->display);
    if (callback == NULL || wl_display_roundtrip(lock->display) < 0) {
        if (callback != NULL) {
            wl_callback_destroy(callback);
        }
        fail(lock, "failed to synchronize the unlock request");
        return 0;
    }
    wl_callback_destroy(callback);
    lock->state = HYPR_LOCK_STATE_UNLOCKED;
    return 1;
}
