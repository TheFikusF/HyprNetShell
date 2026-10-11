#include "thumbnail_pool.h"
#include "thumbnail_resample.h"
#include <pthread.h>
#include <stdlib.h>
#include <unistd.h>

enum slot_state
{
    SLOT_EMPTY,
    SLOT_QUEUED,
    SLOT_RUNNING,
    SLOT_DONE
};
struct thumbnail_pool
{
    pthread_mutex_t mutex;
    pthread_cond_t wake;
    pthread_t threads[THUMBNAIL_POOL_MAX_WORKERS];
    struct thumbnail_job *jobs[THUMBNAIL_POOL_CAPACITY];
    enum slot_state states[THUMBNAIL_POOL_CAPACITY];
    unsigned workers;
    int stopping;
};
static void *thumbnail_worker(void *data)
{
    struct thumbnail_pool *pool = data;
    pthread_mutex_lock(&pool->mutex);
    for (;;)
    {
        unsigned slot = THUMBNAIL_POOL_CAPACITY;
        for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
        {
            if (pool->states[i] == SLOT_QUEUED)
            {
                slot = i;
                break;
            }
        }

        if (pool->stopping)
        {
            break;
        }

        if (slot == THUMBNAIL_POOL_CAPACITY)
        {
            pthread_cond_wait(&pool->wake, &pool->mutex);
            continue;
        }

        struct thumbnail_job *job = pool->jobs[slot];
        pool->states[slot] = SLOT_RUNNING;
        pthread_mutex_unlock(&pool->mutex);
        thumbnail_resample_dispatch(job->source, job->source_width, job->source_height,
                                    job->transform, job->opaque, job->rgba, job->width,
                                    job->height);
        pthread_mutex_lock(&pool->mutex);
        pool->states[slot] = SLOT_DONE;
    }

    pthread_mutex_unlock(&pool->mutex);
    return NULL;
}

struct thumbnail_pool *thumbnail_pool_create(unsigned workers)
{
    if (workers == 0)
    {
        long cpus = sysconf(_SC_NPROCESSORS_ONLN);
        workers = cpus > 2 ? (unsigned)(cpus - 1) : 1;
    }

    if (workers > THUMBNAIL_POOL_MAX_WORKERS)
    {
        workers = THUMBNAIL_POOL_MAX_WORKERS;
    }

    struct thumbnail_pool *pool = calloc(1, sizeof(*pool));
    if (pool == NULL)
    {
        return NULL;
    }

    if (pthread_mutex_init(&pool->mutex, NULL) != 0)
    {
        free(pool);
        return NULL;
    }

    if (pthread_cond_init(&pool->wake, NULL) != 0)
    {
        pthread_mutex_destroy(&pool->mutex);
        free(pool);
        return NULL;
    }

    for (unsigned i = 0; i < workers; i++)
    {
        if (pthread_create(&pool->threads[i], NULL, thumbnail_worker, pool) != 0)
        {
            thumbnail_pool_destroy(pool);
            return NULL;
        }

        pool->workers++;
    }

    return pool;
}

int thumbnail_pool_submit(struct thumbnail_pool *pool, struct thumbnail_job *job)
{
    int accepted = 0;
    pthread_mutex_lock(&pool->mutex);
    unsigned slot = THUMBNAIL_POOL_CAPACITY;
    for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
    {
        if (pool->states[i] != SLOT_EMPTY && pool->jobs[i]->id == job->id)
        {
            pthread_mutex_unlock(&pool->mutex);
            return 0;
        }

        if (pool->states[i] == SLOT_EMPTY)
        {
            slot = i;
        }
    }

    if (!pool->stopping && slot != THUMBNAIL_POOL_CAPACITY)
    {
        pool->jobs[slot] = job;
        pool->states[slot] = SLOT_QUEUED;
        accepted = 1;
        pthread_cond_signal(&pool->wake);
    }

    pthread_mutex_unlock(&pool->mutex);
    return accepted;
}

void thumbnail_pool_cancel(struct thumbnail_pool *pool, uint64_t id)
{
    pthread_mutex_lock(&pool->mutex);
    for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
    {
        if (pool->states[i] == SLOT_QUEUED && pool->jobs[i]->id == id)
        {
            pool->states[i] = SLOT_DONE;
        }
    }

    pthread_mutex_unlock(&pool->mutex);
}

struct thumbnail_job *thumbnail_pool_take(struct thumbnail_pool *pool)
{
    struct thumbnail_job *job = NULL;
    pthread_mutex_lock(&pool->mutex);
    for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
    {
        if (pool->states[i] == SLOT_DONE)
        {
            job = pool->jobs[i];
            pool->jobs[i] = NULL;
            pool->states[i] = SLOT_EMPTY;
            break;
        }
    }

    pthread_mutex_unlock(&pool->mutex);
    return job;
}

int thumbnail_pool_contains(struct thumbnail_pool *pool, uint64_t id)
{
    int found = 0;
    pthread_mutex_lock(&pool->mutex);
    for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
    {
        found |= pool->states[i] != SLOT_EMPTY && pool->jobs[i]->id == id;
    }

    pthread_mutex_unlock(&pool->mutex);
    return found;
}

unsigned thumbnail_pool_count(struct thumbnail_pool *pool)
{
    unsigned count = 0;
    pthread_mutex_lock(&pool->mutex);
    for (unsigned i = 0; i < THUMBNAIL_POOL_CAPACITY; i++)
    {
        count += pool->states[i] != SLOT_EMPTY;
    }

    pthread_mutex_unlock(&pool->mutex);
    return count;
}

unsigned thumbnail_pool_workers(const struct thumbnail_pool *pool)
{
    return pool->workers;
}

void thumbnail_pool_destroy(struct thumbnail_pool *pool)
{
    if (pool == NULL)
    {
        return;
    }

    pthread_mutex_lock(&pool->mutex);
    pool->stopping = 1;
    pthread_cond_broadcast(&pool->wake);
    pthread_mutex_unlock(&pool->mutex);
    for (unsigned i = 0; i < pool->workers; i++)
    {
        pthread_join(pool->threads[i], NULL);
    }

    pthread_cond_destroy(&pool->wake);
    pthread_mutex_destroy(&pool->mutex);
    free(pool);
}
