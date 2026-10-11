#ifndef HYPR_THUMBNAIL_POOL_H
#define HYPR_THUMBNAIL_POOL_H
#include <stddef.h>
#include <stdint.h>

enum
{
    THUMBNAIL_POOL_CAPACITY = 8,
    THUMBNAIL_POOL_MAX_WORKERS = 4
};
struct thumbnail_job
{
    uint64_t id, generation, buffer_generation;
    const uint32_t *source;
    size_t mapping_size;
    int source_width, source_height, opaque;
    unsigned transform;
    unsigned char *rgba;
    int width, height;
};
struct thumbnail_pool;
/* Main-thread-only API. Accepted jobs and their storage must stay alive until
 * take() or destroy() returns. Workers touch only immutable inputs and rgba.
 * Capacity includes queued, running AND completed jobs; no unbounded done list.
 * destroy joins, but does not free caller-owned jobs or their storage. */
struct thumbnail_pool *thumbnail_pool_create(unsigned workers);
int thumbnail_pool_submit(struct thumbnail_pool *pool,
                          struct thumbnail_job *job);
/* Queued cancellation completes without resampling; running work finishes.
 * Caller must discard cancelled outputs and still take/free every job. */
void thumbnail_pool_cancel(struct thumbnail_pool *pool, uint64_t id);
struct thumbnail_job *thumbnail_pool_take(struct thumbnail_pool *pool);
int thumbnail_pool_contains(struct thumbnail_pool *pool, uint64_t id);
unsigned thumbnail_pool_count(struct thumbnail_pool *pool);
unsigned thumbnail_pool_workers(const struct thumbnail_pool *pool);
void thumbnail_pool_destroy(struct thumbnail_pool *pool);
#endif
