#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define HYPR_AUDIO_ABI_VERSION 1u

typedef struct hypr_audio hypr_audio;

typedef struct hypr_audio_device {
    uint32_t struct_size;
    uint32_t id;
    const char* name;
    int32_t volume;
    uint8_t muted;
    uint8_t active;
    uint8_t reserved[2];
} hypr_audio_device;

typedef struct hypr_audio_snapshot {
    uint32_t struct_size;
    uint32_t abi_version;
    const hypr_audio_device* outputs;
    uint32_t output_count;
    const hypr_audio_device* inputs;
    uint32_t input_count;
    uint8_t is_recording;
    uint8_t reserved[7];
} hypr_audio_snapshot;

typedef void (*hypr_audio_snapshot_callback)(
    void* user_data,
    const hypr_audio_snapshot* snapshot);

/* The snapshot and every pointer in it are valid only for the callback duration. */
hypr_audio* hypr_audio_create(
    hypr_audio_snapshot_callback callback,
    void* user_data);
void hypr_audio_destroy(hypr_audio* audio);

uint32_t hypr_audio_get_abi_version(void);
int hypr_audio_is_available(const hypr_audio* audio);
int hypr_audio_set_default(hypr_audio* audio, uint32_t device_id);
int hypr_audio_set_volume(hypr_audio* audio, uint32_t device_id, int volume_percent);
int hypr_audio_set_muted(hypr_audio* audio, uint32_t device_id, int muted);

#ifdef __cplusplus
}
#endif
