#define _GNU_SOURCE

#include "hypr_audio.h"

#include <math.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#include <pipewire/keys.h>
#include <wp/wp.h>

struct hypr_audio {
    pthread_t thread;
    pthread_mutex_t lock;
    GMainContext* context;
    GMainLoop* loop;
    WpCore* core;
    WpObjectManager* object_manager;
    WpPlugin* mixer_api;
    WpPlugin* default_nodes_api;
    hypr_audio_snapshot_callback callback;
    void* user_data;
    atomic_int available;
    atomic_int stopping;
    int pending_components;
};

typedef enum audio_command_type {
    AUDIO_COMMAND_SET_DEFAULT,
    AUDIO_COMMAND_SET_VOLUME,
    AUDIO_COMMAND_SET_MUTED,
} audio_command_type;

typedef struct audio_command {
    struct hypr_audio* audio;
    audio_command_type type;
    uint32_t device_id;
    int value;
} audio_command;

static void publish_snapshot(struct hypr_audio* audio);

static const char* device_name(WpPipewireObject* node) {
    const char* name = wp_pipewire_object_get_property(node, PW_KEY_NODE_DESCRIPTION);
    if (name == NULL || name[0] == '\0') {
        name = wp_pipewire_object_get_property(node, PW_KEY_NODE_NICK);
    }
    if (name == NULL || name[0] == '\0') {
        name = wp_pipewire_object_get_property(node, PW_KEY_NODE_NAME);
    }
    return name != NULL ? name : "Unknown audio device";
}

static gboolean contains_case_insensitive(const char* value, const char* needle) {
    if (value == NULL) {
        return FALSE;
    }
    char* lower_value = g_utf8_strdown(value, -1);
    char* lower_needle = g_utf8_strdown(needle, -1);
    gboolean result = strstr(lower_value, lower_needle) != NULL;
    g_free(lower_needle);
    g_free(lower_value);
    return result;
}

static gboolean is_recording_stream(WpPipewireObject* node) {
    const char* media_class = wp_pipewire_object_get_property(node, PW_KEY_MEDIA_CLASS);
    if (g_strcmp0(media_class, "Stream/Input/Audio") != 0 ||
        wp_node_get_state(WP_NODE(node), NULL) != WP_NODE_STATE_RUNNING) {
        return FALSE;
    }

    const char* application_name = wp_pipewire_object_get_property(node, PW_KEY_APP_NAME);
    const char* process_binary = wp_pipewire_object_get_property(node, PW_KEY_APP_PROCESS_BINARY);
    if (application_name == NULL && process_binary == NULL) {
        return FALSE;
    }

    const char* media_role = wp_pipewire_object_get_property(node, PW_KEY_MEDIA_ROLE);
    const char* application_id = wp_pipewire_object_get_property(node, PW_KEY_APP_ID);
    const char* node_name = wp_pipewire_object_get_property(node, PW_KEY_NODE_NAME);
    const char* media_name = wp_pipewire_object_get_property(node, PW_KEY_MEDIA_NAME);
    const char* target_object = wp_pipewire_object_get_property(node, PW_KEY_TARGET_OBJECT);
    const char* node_target = wp_pipewire_object_get_property(node, "node.target");

    return g_ascii_strcasecmp(media_role != NULL ? media_role : "", "Abstract") != 0 &&
        !contains_case_insensitive(application_id, "pavucontrol") &&
        !contains_case_insensitive(process_binary, "pavucontrol") &&
        !contains_case_insensitive(node_name, "peak detect") &&
        !contains_case_insensitive(media_name, "peak detect") &&
        !contains_case_insensitive(target_object, ".monitor") &&
        !contains_case_insensitive(node_target, ".monitor");
}

static void free_device_array(GArray* devices) {
    if (devices == NULL) {
        return;
    }
    for (guint i = 0; i < devices->len; ++i) {
        hypr_audio_device* device = &g_array_index(devices, hypr_audio_device, i);
        g_free((void*)device->name);
    }
    g_array_unref(devices);
}

static void append_device(
    GArray* devices,
    WpPipewireObject* node,
    uint32_t default_id,
    WpPlugin* mixer_api) {
    uint32_t id = wp_proxy_get_bound_id(WP_PROXY(node));
    GVariant* controls = NULL;
    gboolean muted = FALSE;
    gdouble volume = 1.0;

    if (mixer_api != NULL) {
        g_signal_emit_by_name(mixer_api, "get-volume", id, &controls);
    }
    if (controls != NULL) {
        g_variant_lookup(controls, "mute", "b", &muted);
        g_variant_lookup(controls, "volume", "d", &volume);
        g_variant_unref(controls);
    }

    hypr_audio_device device = {
        .struct_size = sizeof(hypr_audio_device),
        .id = id,
        .name = g_strdup(device_name(node)),
        .volume = CLAMP((int32_t)lround(volume * 100.0), 0, 100),
        .muted = muted ? 1 : 0,
        .active = id == default_id ? 1 : 0,
        .reserved = {0, 0},
    };
    g_array_append_val(devices, device);
}

static void publish_snapshot(struct hypr_audio* audio) {
    if (audio == NULL || audio->callback == NULL ||
        audio->object_manager == NULL ||
        atomic_load_explicit(&audio->stopping, memory_order_acquire)) {
        return;
    }

    uint32_t default_output = UINT32_MAX;
    uint32_t default_input = UINT32_MAX;
    if (audio->default_nodes_api != NULL) {
        g_signal_emit_by_name(
            audio->default_nodes_api, "get-default-node", "Audio/Sink", &default_output);
        g_signal_emit_by_name(
            audio->default_nodes_api, "get-default-node", "Audio/Source", &default_input);
    }

    GArray* outputs = g_array_new(FALSE, FALSE, sizeof(hypr_audio_device));
    GArray* inputs = g_array_new(FALSE, FALSE, sizeof(hypr_audio_device));
    gboolean recording = FALSE;
    WpIterator* iterator = wp_object_manager_new_filtered_iterator(
        audio->object_manager, WP_TYPE_NODE, NULL);
    GValue value = G_VALUE_INIT;

    while (wp_iterator_next(iterator, &value)) {
        WpPipewireObject* node = g_value_get_object(&value);
        const char* media_class = wp_pipewire_object_get_property(node, PW_KEY_MEDIA_CLASS);
        const char* link_group = wp_pipewire_object_get_property(node, PW_KEY_NODE_LINK_GROUP);

        if (g_str_has_prefix(media_class != NULL ? media_class : "", "Audio/Sink") &&
            link_group == NULL) {
            append_device(outputs, node, default_output, audio->mixer_api);
        } else if (g_str_has_prefix(media_class != NULL ? media_class : "", "Audio/Source") &&
            link_group == NULL) {
            append_device(inputs, node, default_input, audio->mixer_api);
        } else if (!recording && is_recording_stream(node)) {
            recording = TRUE;
        }
        g_value_unset(&value);
    }
    wp_iterator_unref(iterator);

    hypr_audio_snapshot snapshot = {
        .struct_size = sizeof(hypr_audio_snapshot),
        .abi_version = HYPR_AUDIO_ABI_VERSION,
        .outputs = (const hypr_audio_device*)outputs->data,
        .output_count = outputs->len,
        .inputs = (const hypr_audio_device*)inputs->data,
        .input_count = inputs->len,
        .is_recording = recording ? 1 : 0,
        .reserved = {0, 0, 0, 0, 0, 0, 0},
    };
    audio->callback(audio->user_data, &snapshot);

    free_device_array(inputs);
    free_device_array(outputs);
}

static void on_audio_changed(WpObjectManager* manager, struct hypr_audio* audio) {
    (void)manager;
    publish_snapshot(audio);
}

static void on_default_changed(WpPlugin* plugin, struct hypr_audio* audio) {
    (void)plugin;
    publish_snapshot(audio);
}

static void on_mixer_changed(WpPlugin* plugin, guint32 id, struct hypr_audio* audio) {
    (void)plugin;
    (void)id;
    publish_snapshot(audio);
}

static void on_node_state_changed(
    WpNode* node,
    WpNodeState old_state,
    WpNodeState new_state,
    struct hypr_audio* audio) {
    (void)node;
    (void)old_state;
    (void)new_state;
    publish_snapshot(audio);
}

static void on_object_added(
    WpObjectManager* manager,
    WpObject* object,
    struct hypr_audio* audio) {
    (void)manager;
    if (WP_IS_NODE(object)) {
        g_signal_connect(object, "state-changed", G_CALLBACK(on_node_state_changed), audio);
    }
}

static void on_object_removed(
    WpObjectManager* manager,
    WpObject* object,
    struct hypr_audio* audio) {
    (void)manager;
    if (WP_IS_NODE(object)) {
        g_signal_handlers_disconnect_by_data(object, audio);
    }
}

static void on_object_manager_installed(
    WpObjectManager* manager,
    struct hypr_audio* audio) {
    (void)manager;
    atomic_store_explicit(&audio->available, 1, memory_order_release);
    publish_snapshot(audio);
}

static void on_core_disconnected(WpCore* core, struct hypr_audio* audio) {
    (void)core;
    atomic_store_explicit(&audio->available, 0, memory_order_release);
    if (audio->loop != NULL) {
        g_main_loop_quit(audio->loop);
    }
}

static void on_component_loaded(
    WpCore* core,
    GAsyncResult* result,
    struct hypr_audio* audio) {
    GError* error = NULL;
    if (!wp_core_load_component_finish(core, result, &error)) {
        g_clear_error(&error);
        atomic_store_explicit(&audio->available, 0, memory_order_release);
        g_main_loop_quit(audio->loop);
        return;
    }

    if (--audio->pending_components != 0) {
        return;
    }

    audio->mixer_api = wp_plugin_find(core, "mixer-api");
    audio->default_nodes_api = wp_plugin_find(core, "default-nodes-api");
    if (audio->mixer_api == NULL || audio->default_nodes_api == NULL) {
        g_main_loop_quit(audio->loop);
        return;
    }

    g_object_set(audio->mixer_api, "scale", 1, NULL);
    g_signal_connect(audio->mixer_api, "changed", G_CALLBACK(on_mixer_changed), audio);
    g_signal_connect(
        audio->default_nodes_api, "changed", G_CALLBACK(on_default_changed), audio);
    wp_core_install_object_manager(core, audio->object_manager);
}

static gboolean execute_command(gpointer data) {
    audio_command* command = data;
    struct hypr_audio* audio = command->audio;

    if (!atomic_load_explicit(&audio->stopping, memory_order_acquire) &&
        atomic_load_explicit(&audio->available, memory_order_acquire)) {
        if (command->type == AUDIO_COMMAND_SET_DEFAULT) {
            WpNode* node = wp_object_manager_lookup(
                audio->object_manager,
                WP_TYPE_NODE,
                WP_CONSTRAINT_TYPE_G_PROPERTY,
                "bound-id",
                "=u",
                command->device_id,
                NULL);
            if (node != NULL) {
                const char* media_class = wp_pipewire_object_get_property(
                    WP_PIPEWIRE_OBJECT(node), PW_KEY_MEDIA_CLASS);
                const char* name = wp_pipewire_object_get_property(
                    WP_PIPEWIRE_OBJECT(node), PW_KEY_NODE_NAME);
                const char* default_class = NULL;
                if (g_str_has_prefix(media_class != NULL ? media_class : "", "Audio/Sink") &&
                    !g_str_has_suffix(media_class, "/Internal")) {
                    default_class = "Audio/Sink";
                } else if (g_str_has_prefix(media_class != NULL ? media_class : "", "Audio/Source") &&
                    !g_str_has_suffix(media_class, "/Internal")) {
                    default_class = "Audio/Source";
                }
                if (default_class != NULL && name != NULL) {
                    gboolean changed = FALSE;
                    g_signal_emit_by_name(
                        audio->default_nodes_api,
                        "set-default-configured-node-name",
                        default_class,
                        name,
                        &changed);
                }
                g_object_unref(node);
            }
        } else {
            GVariantBuilder builder;
            g_variant_builder_init(&builder, G_VARIANT_TYPE_VARDICT);
            if (command->type == AUDIO_COMMAND_SET_VOLUME) {
                g_variant_builder_add(
                    &builder,
                    "{sv}",
                    "volume",
                    g_variant_new_double(CLAMP(command->value, 0, 100) / 100.0));
            } else {
                g_variant_builder_add(
                    &builder,
                    "{sv}",
                    "mute",
                    g_variant_new_boolean(command->value != 0));
            }
            GVariant* controls = g_variant_builder_end(&builder);
            gboolean changed = FALSE;
            g_signal_emit_by_name(
                audio->mixer_api,
                "set-volume",
                command->device_id,
                controls,
                &changed);
        }
    }

    return G_SOURCE_REMOVE;
}

static gboolean stop_main_loop(gpointer data) {
    struct hypr_audio* audio = data;
    if (audio->loop != NULL) {
        g_main_loop_quit(audio->loop);
    }
    return G_SOURCE_REMOVE;
}

static void* audio_thread(void* data) {
    struct hypr_audio* audio = data;
    GMainContext* context = g_main_context_new();
    g_main_context_push_thread_default(context);

    GMainLoop* loop = g_main_loop_new(context, FALSE);
    WpProperties* properties = wp_properties_new(
        PW_KEY_REMOTE_NAME, "[pipewire-0-manager,pipewire-0]", NULL);
    WpCore* core = wp_core_new(context, NULL, properties);
    WpObjectManager* object_manager = wp_object_manager_new();

    wp_object_manager_add_interest(object_manager, WP_TYPE_NODE, NULL);
    wp_object_manager_request_object_features(
        object_manager, WP_TYPE_NODE, WP_PIPEWIRE_OBJECT_FEATURES_MINIMAL);
    g_signal_connect(
        object_manager, "object-added", G_CALLBACK(on_object_added), audio);
    g_signal_connect(
        object_manager, "object-removed", G_CALLBACK(on_object_removed), audio);
    g_signal_connect(
        object_manager, "objects-changed", G_CALLBACK(on_audio_changed), audio);
    g_signal_connect(
        object_manager, "installed", G_CALLBACK(on_object_manager_installed), audio);
    g_signal_connect(core, "disconnected", G_CALLBACK(on_core_disconnected), audio);

    pthread_mutex_lock(&audio->lock);
    audio->context = g_main_context_ref(context);
    audio->loop = loop;
    audio->core = core;
    audio->object_manager = object_manager;
    int stopping = atomic_load_explicit(&audio->stopping, memory_order_acquire);
    pthread_mutex_unlock(&audio->lock);

    if (!stopping && wp_core_connect(core)) {
        audio->pending_components = 2;
        wp_core_load_component(
            core,
            "libwireplumber-module-default-nodes-api",
            "module",
            NULL,
            NULL,
            NULL,
            (GAsyncReadyCallback)on_component_loaded,
            audio);
        wp_core_load_component(
            core,
            "libwireplumber-module-mixer-api",
            "module",
            NULL,
            NULL,
            NULL,
            (GAsyncReadyCallback)on_component_loaded,
            audio);
        g_main_loop_run(loop);
    }

    atomic_store_explicit(&audio->available, 0, memory_order_release);
    pthread_mutex_lock(&audio->lock);
    g_clear_pointer(&audio->context, g_main_context_unref);
    audio->loop = NULL;
    audio->core = NULL;
    audio->object_manager = NULL;
    pthread_mutex_unlock(&audio->lock);

    g_clear_object(&audio->default_nodes_api);
    g_clear_object(&audio->mixer_api);
    g_clear_object(&object_manager);
    g_clear_object(&core);
    g_main_loop_unref(loop);
    g_main_context_pop_thread_default(context);
    g_main_context_unref(context);
    return NULL;
}

static int queue_command(
    struct hypr_audio* audio,
    audio_command_type type,
    uint32_t device_id,
    int value) {
    if (audio == NULL || device_id == 0 ||
        !atomic_load_explicit(&audio->available, memory_order_acquire)) {
        return 0;
    }

    audio_command* command = g_new0(audio_command, 1);
    command->audio = audio;
    command->type = type;
    command->device_id = device_id;
    command->value = value;

    pthread_mutex_lock(&audio->lock);
    GMainContext* context =
        !atomic_load_explicit(&audio->stopping, memory_order_acquire) &&
        audio->context != NULL
        ? g_main_context_ref(audio->context)
        : NULL;
    pthread_mutex_unlock(&audio->lock);
    if (context == NULL) {
        g_free(command);
        return 0;
    }

    g_main_context_invoke_full(
        context, G_PRIORITY_DEFAULT, execute_command, command, g_free);
    g_main_context_unref(context);
    return 1;
}

hypr_audio* hypr_audio_create(
    hypr_audio_snapshot_callback callback,
    void* user_data) {
    if (callback == NULL) {
        return NULL;
    }

    wp_init(WP_INIT_ALL);
    struct hypr_audio* audio = calloc(1, sizeof(struct hypr_audio));
    if (audio == NULL) {
        return NULL;
    }
    audio->callback = callback;
    audio->user_data = user_data;
    atomic_init(&audio->available, 0);
    atomic_init(&audio->stopping, 0);
    if (pthread_mutex_init(&audio->lock, NULL) != 0) {
        free(audio);
        return NULL;
    }
    if (pthread_create(&audio->thread, NULL, audio_thread, audio) != 0) {
        pthread_mutex_destroy(&audio->lock);
        free(audio);
        return NULL;
    }
    return audio;
}

void hypr_audio_destroy(hypr_audio* audio) {
    if (audio == NULL) {
        return;
    }

    atomic_store_explicit(&audio->available, 0, memory_order_release);
    pthread_mutex_lock(&audio->lock);
    atomic_store_explicit(&audio->stopping, 1, memory_order_release);
    GMainContext* context = audio->context != NULL
        ? g_main_context_ref(audio->context)
        : NULL;
    pthread_mutex_unlock(&audio->lock);

    if (context != NULL) {
        g_main_context_invoke_full(
            context, G_PRIORITY_HIGH, stop_main_loop, audio, NULL);
        g_main_context_unref(context);
    }
    pthread_join(audio->thread, NULL);
    pthread_mutex_destroy(&audio->lock);
    free(audio);
}

uint32_t hypr_audio_get_abi_version(void) {
    return HYPR_AUDIO_ABI_VERSION;
}

int hypr_audio_is_available(const hypr_audio* audio) {
    return audio != NULL &&
        atomic_load_explicit(&audio->available, memory_order_acquire) ? 1 : 0;
}

int hypr_audio_set_default(hypr_audio* audio, uint32_t device_id) {
    return queue_command(audio, AUDIO_COMMAND_SET_DEFAULT, device_id, 0);
}

int hypr_audio_set_volume(
    hypr_audio* audio,
    uint32_t device_id,
    int volume_percent) {
    return queue_command(audio, AUDIO_COMMAND_SET_VOLUME, device_id, volume_percent);
}

int hypr_audio_set_muted(hypr_audio* audio, uint32_t device_id, int muted) {
    return queue_command(audio, AUDIO_COMMAND_SET_MUTED, device_id, muted);
}
