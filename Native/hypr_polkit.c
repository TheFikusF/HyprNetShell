#include "hypr_polkit.h"

#define POLKIT_AGENT_I_KNOW_API_IS_SUBJECT_TO_CHANGE
#include <polkitagent/polkitagent.h>
#include <string.h>
#include <unistd.h>

typedef struct
{
    GMainContext *context;
    GMainLoop *loop;
    GCancellable *startup;
    GThread *thread;

    GMutex mutex;
    gboolean stopping;
    gboolean dispatch_queued;
    gboolean answer_pending;
    uint64_t generation;
    gchar *prompt;
    gchar *response;

    /* The remaining state belongs exclusively to the worker context. */
    GTask *task;
    PolkitAgentSession *session;
    GSource *cancel_source;
    gchar *message;
} HyprPolkit;

typedef struct
{
    PolkitAgentListener parent_instance;
    HyprPolkit *agent;
} HyprPolkitListener;

typedef struct
{
    PolkitAgentListenerClass parent_class;
} HyprPolkitListenerClass;

G_DEFINE_TYPE(HyprPolkitListener, hypr_polkit_listener,
              POLKIT_AGENT_TYPE_LISTENER)
G_LOCK_DEFINE_STATIC(generation_lock);
static uint64_t last_generation;

static uint64_t next_generation(void)
{
    uint64_t result = 0;
    G_LOCK(generation_lock);
    if (last_generation != UINT64_MAX)
    {
        result = ++last_generation;
    }

    G_UNLOCK(generation_lock);
    return result;
}

static void wipe_response(gchar *response)
{
    if (response != NULL)
    {
        volatile gchar *bytes = response;
        for (size_t i = strlen(response); i > 0; --i)
        {
            *bytes++ = 0;
        }

        g_free(response);
    }
}

static void clear_prompt(HyprPolkit *agent)
{
    agent->generation = 0;
    g_clear_pointer(&agent->prompt, g_free);
}

static void finish_request(HyprPolkit *agent, gboolean gained, gboolean cancel)
{
    PolkitAgentSession *session = g_steal_pointer(&agent->session);
    GTask *task = g_steal_pointer(&agent->task);
    g_signal_handlers_disconnect_by_data(session, agent);
    if (agent->cancel_source != NULL)
    {
        g_source_destroy(agent->cancel_source);
        g_clear_pointer(&agent->cancel_source, g_source_unref);
    }

    g_clear_pointer(&agent->message, g_free);
    g_mutex_lock(&agent->mutex);
    clear_prompt(agent);
    agent->answer_pending = FALSE;
    g_clear_pointer(&agent->response, wipe_response);
    g_mutex_unlock(&agent->mutex);

    if (cancel)
    {
        polkit_agent_session_cancel(session);
        g_task_return_new_error(task, POLKIT_ERROR, POLKIT_ERROR_CANCELLED,
                                "Authentication cancelled");
    }
    else if (gained)
    {
        g_task_return_boolean(task, TRUE);
    }
    else
    {
        g_task_return_new_error(task, POLKIT_ERROR, POLKIT_ERROR_FAILED,
                                "Authentication failed");
    }

    g_object_unref(session);
    g_object_unref(task);
}

static void session_completed(PolkitAgentSession *session, gboolean gained,
                              gpointer data)
{
    (void)session;
    finish_request(data, gained, FALSE);
}

static gboolean request_cancelled(GCancellable *cancellable, gpointer data)
{
    (void)cancellable;
    finish_request(data, FALSE, TRUE);
    return G_SOURCE_REMOVE;
}

static gboolean dispatch(gpointer data)
{
    HyprPolkit *agent = data;
    g_mutex_lock(&agent->mutex);
    agent->dispatch_queued = FALSE;
    gboolean stopping = agent->stopping;
    gboolean pending = agent->answer_pending;
    agent->answer_pending = FALSE;
    gchar *response = g_steal_pointer(&agent->response);
    g_mutex_unlock(&agent->mutex);

    if (agent->session != NULL)
    {
        GCancellable *cancellable = g_task_get_cancellable(agent->task);
        if (stopping || (pending && response == NULL) ||
            (cancellable != NULL && g_cancellable_is_cancelled(cancellable)))
        {
            finish_request(agent, FALSE, TRUE);
        }
        else if (pending)
        {
            PolkitAgentSession *session = g_object_ref(agent->session);
            polkit_agent_session_response(session, response);
            g_object_unref(session);
        }
    }

    wipe_response(response);
    if (stopping)
    {
        /* The default-priority GTask completion must reach the listener
         * registration before this idle runs again and stops the context. */
        g_mutex_lock(&agent->mutex);
        clear_prompt(agent);
        g_mutex_unlock(&agent->mutex);
        return G_SOURCE_CONTINUE;
    }

    return G_SOURCE_REMOVE;
}

static gboolean dispatch_or_stop(gpointer data)
{
    HyprPolkit *agent = data;
    g_mutex_lock(&agent->mutex);
    gboolean drained = agent->stopping && !agent->dispatch_queued;
    g_mutex_unlock(&agent->mutex);
    if (drained)
    {
        g_main_loop_quit(agent->loop);
        return G_SOURCE_REMOVE;
    }

    return dispatch(data);
}

/* Must be called under mutex. Attaching a source never runs it inline. */
static void queue_dispatch(HyprPolkit *agent)
{
    if (!agent->dispatch_queued)
    {
        agent->dispatch_queued = TRUE;
        GSource *source = g_idle_source_new();
        g_source_set_callback(source, dispatch_or_stop, agent, NULL);
        g_source_attach(source, agent->context);
        g_source_unref(source);
    }
}

static void session_request(PolkitAgentSession *session, const gchar *prompt,
                            gboolean echo_on, gpointer data)
{
    (void)session;
    HyprPolkit *agent = data;
    g_mutex_lock(&agent->mutex);
    gboolean overlapping = agent->generation != 0 || agent->answer_pending;
    clear_prompt(agent);
    if (!agent->stopping)
    {
        /* Overlapping prompts or pending responses are not safe to associate.
         */
        if (!echo_on && !overlapping && prompt != NULL && *prompt != '\0')
        {
            agent->generation = next_generation();
        }

        if (agent->generation != 0)
        {
            agent->prompt = g_strconcat(agent->message, "\n", prompt, NULL);
        }
        else
        {
            g_clear_pointer(&agent->response, wipe_response);
            agent->answer_pending = TRUE;
            queue_dispatch(agent);
        }
    }

    g_mutex_unlock(&agent->mutex);
}

static PolkitIdentity *select_identity(GList *identities)
{
    if (identities != NULL && identities->next == NULL)
    {
        return identities->data;
    }

    uid_t uid = getuid();
    for (GList *item = identities; item != NULL; item = item->next)
    {
        if (POLKIT_IS_UNIX_USER(item->data) &&
            (uid_t)polkit_unix_user_get_uid(POLKIT_UNIX_USER(item->data)) ==
                uid)
        {
            return item->data;
        }
    }

    return NULL;
}

static void
initiate_authentication(PolkitAgentListener *listener, const gchar *action_id,
                        const gchar *message, const gchar *icon_name,
                        PolkitDetails *details, const gchar *cookie,
                        GList *identities, GCancellable *cancellable,
                        GAsyncReadyCallback callback, gpointer user_data)
{
    (void)action_id;
    (void)icon_name;
    (void)details;
    HyprPolkit *agent = ((HyprPolkitListener *)listener)->agent;
    GTask *task = g_task_new(listener, cancellable, callback, user_data);
    g_mutex_lock(&agent->mutex);
    gboolean stopping = agent->stopping;
    g_mutex_unlock(&agent->mutex);

    if (stopping || agent->task != NULL)
    {
        g_task_return_new_error(task, POLKIT_ERROR, POLKIT_ERROR_CANCELLED,
                                "Authentication agent unavailable");
        g_object_unref(task);
        return;
    }

    PolkitIdentity *identity = select_identity(identities);
    if (identity == NULL)
    {
        g_task_return_new_error(task, POLKIT_ERROR, POLKIT_ERROR_FAILED,
                                "No unambiguous authentication identity");
        g_object_unref(task);
        return;
    }

    if (g_task_return_error_if_cancelled(task))
    {
        g_object_unref(task);
        return;
    }

    PolkitAgentSession *session = polkit_agent_session_new(identity, cookie);
    agent->task = task;
    agent->session = g_object_ref(session);
    agent->message =
        g_strdup(message != NULL ? message : "Authentication required");
    if (cancellable != NULL)
    {
        agent->cancel_source = g_cancellable_source_new(cancellable);
        g_source_set_callback(agent->cancel_source,
                              G_SOURCE_FUNC(request_cancelled), agent, NULL);
        g_source_attach(agent->cancel_source, agent->context);
    }

    g_signal_connect(session, "request", G_CALLBACK(session_request), agent);
    g_signal_connect(session, "completed", G_CALLBACK(session_completed),
                     agent);
    polkit_agent_session_initiate(session);
    g_object_unref(session);
}

static gboolean initiate_authentication_finish(PolkitAgentListener *listener,
                                               GAsyncResult *result,
                                               GError **error)
{
    g_return_val_if_fail(g_task_is_valid(result, listener), FALSE);
    return g_task_propagate_boolean(G_TASK(result), error);
}

static void hypr_polkit_listener_class_init(HyprPolkitListenerClass *klass)
{
    PolkitAgentListenerClass *listener_class =
        POLKIT_AGENT_LISTENER_CLASS(klass);
    listener_class->initiate_authentication = initiate_authentication;
    listener_class->initiate_authentication_finish =
        initiate_authentication_finish;
}

static void hypr_polkit_listener_init(HyprPolkitListener *listener)
{
    (void)listener;
}

static gpointer agent_worker(gpointer data)
{
    HyprPolkit *agent = data;
    GError *error = NULL;
    g_main_context_push_thread_default(agent->context);
    HyprPolkitListener *listener =
        g_object_new(hypr_polkit_listener_get_type(), NULL);
    listener->agent = agent;

    PolkitSubject *subject = polkit_unix_session_new_for_process_sync(
        getpid(), agent->startup, &error);
    gboolean resolved = subject != NULL;
    gpointer registration = NULL;
    if (resolved)
    {
        registration = polkit_agent_listener_register(
            POLKIT_AGENT_LISTENER(listener), POLKIT_AGENT_REGISTER_FLAGS_NONE,
            subject, NULL, agent->startup, &error);
        g_object_unref(subject);
    }

    if (registration != NULL)
    {
        g_main_loop_run(agent->loop);
        polkit_agent_listener_unregister(registration);
    }
    else if (!g_cancellable_is_cancelled(agent->startup))
    {
        g_printerr("hypr-polkit: could not %s (error code %d)\n",
                   resolved ? "register the agent"
                            : "resolve the current Unix session",
                   error != NULL ? error->code : 0);
    }

    g_clear_error(&error);
    g_object_unref(listener);
    g_main_context_pop_thread_default(agent->context);
    return NULL;
}

void *hypr_polkit_start(void)
{
    HyprPolkit *agent = g_new0(HyprPolkit, 1);
    g_mutex_init(&agent->mutex);
    agent->context = g_main_context_new();
    agent->loop = g_main_loop_new(agent->context, FALSE);
    agent->startup = g_cancellable_new();
    agent->thread = g_thread_try_new("hypr-polkit", agent_worker, agent, NULL);
    if (agent->thread == NULL)
    {
        g_printerr("hypr-polkit: could not start the agent worker\n");
        hypr_polkit_destroy(agent);
        return NULL;
    }

    return agent;
}

void hypr_polkit_destroy(void *handle)
{
    HyprPolkit *agent = handle;
    if (agent == NULL)
    {
        return;
    }

    if (agent->thread != NULL)
    {
        g_mutex_lock(&agent->mutex);
        agent->stopping = TRUE;
        clear_prompt(agent);
        g_clear_pointer(&agent->response, wipe_response);
        queue_dispatch(agent);
        g_mutex_unlock(&agent->mutex);
        g_cancellable_cancel(agent->startup);
        g_thread_join(agent->thread);
    }

    g_object_unref(agent->startup);
    g_main_loop_unref(agent->loop);
    g_main_context_unref(agent->context);
    g_mutex_clear(&agent->mutex);
    g_free(agent);
}

uint64_t hypr_polkit_prompt(void *handle, char *message, size_t capacity)
{
    HyprPolkit *agent = handle;
    if (message == NULL || capacity == 0)
    {
        return 0;
    }

    *message = '\0';
    if (agent == NULL)
    {
        return 0;
    }

    uint64_t result = 0;
    g_mutex_lock(&agent->mutex);
    if (agent->generation != 0)
    {
        size_t length = strlen(agent->prompt) + 1;
        if (length <= capacity)
        {
            memcpy(message, agent->prompt, length);
            result = agent->generation;
        }
    }

    g_mutex_unlock(&agent->mutex);
    return result;
}

void hypr_polkit_answer(void *handle, uint64_t generation, const char *response)
{
    HyprPolkit *agent = handle;
    if (agent == NULL || generation == 0)
    {
        return;
    }

    g_mutex_lock(&agent->mutex);
    if (agent->generation == generation)
    {
        /* The helper protocol is line-oriented; never pass a line break. */
        if (response != NULL && strpbrk(response, "\r\n") == NULL)
        {
            agent->response = g_strdup(response);
        }

        clear_prompt(agent);
        agent->answer_pending = TRUE;
        queue_dispatch(agent);
    }

    g_mutex_unlock(&agent->mutex);
}
