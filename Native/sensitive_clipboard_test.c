#define _GNU_SOURCE
#include <assert.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>

static void *watched_buffer;
static size_t watched_length;
static unsigned zeroed_frees;
static void checked_free(void *pointer);

/* Exercise the actual private callbacks, discarding unrelated compositor code
 * at link time. No Wayland connection or clipboard selection is created. */
#define free checked_free
#include "hypr_layer.c"
#undef free

static void checked_free(void *pointer)
{
    if (pointer != NULL && pointer == watched_buffer)
    {
        for (size_t i = 0; i < watched_length; i++)
        {
            assert(((unsigned char *)pointer)[i] == 0);
        }

        zeroed_frees++;
        watched_buffer = NULL;
    }

    free(pointer);
}

struct wl_proxy *
__wrap_wl_proxy_marshal_flags(struct wl_proxy *proxy, uint32_t opcode,
                              const struct wl_interface *interface,
                              uint32_t version, uint32_t flags, ...)
{
    (void)proxy;
    (void)opcode;
    (void)interface;
    (void)version;
    (void)flags;
    return NULL;
}

uint32_t __wrap_wl_proxy_get_version(struct wl_proxy *proxy)
{
    (void)proxy;
    return 1;
}

static struct clipboard_source make_source(void)
{
    struct clipboard_source source = {0};
    source.data = (unsigned char *)strdup("123456");
    assert(source.data != NULL);
    source.length = 6;
    source.sensitive = 1;
    return source;
}

static void expect_send(struct clipboard_source *source, const char *mime,
                        const char *expected)
{
    int descriptors[2];
    assert(pipe(descriptors) == 0);
    clipboard_source_send(source, NULL, mime, descriptors[1]);
    char received[32] = {0};
    ssize_t length = read(descriptors[0], received, sizeof(received));
    assert(length == (ssize_t)strlen(expected));
    assert(memcmp(received, expected, (size_t)length) == 0);
    assert(read(descriptors[0], received, sizeof(received)) == 0);
    close(descriptors[0]);
    assert(fcntl(descriptors[1], F_GETFD) == -1 && errno == EBADF);
}

static void test_epipe(int initially_blocked, int initially_pending)
{
    /* A child with default disposition would die if the callback leaked
     * SIGPIPE. */
    pid_t child = fork();
    assert(child >= 0);
    if (child == 0)
    {
        alarm(3);
        assert(signal(SIGPIPE, SIG_DFL) != SIG_ERR);
        sigset_t signals, before, after, pending;
        sigemptyset(&signals);
        sigaddset(&signals, SIGPIPE);
        assert(pthread_sigmask(initially_blocked ? SIG_BLOCK : SIG_UNBLOCK,
                               &signals, NULL) == 0);
        if (initially_pending)
        {
            assert(raise(SIGPIPE) == 0);
        }

        assert(pthread_sigmask(SIG_SETMASK, NULL, &before) == 0);
        struct clipboard_source source = make_source();
        int descriptors[2];
        assert(pipe(descriptors) == 0);
        close(descriptors[0]);
        clipboard_source_send(&source, NULL, "text/plain", descriptors[1]);
        assert(pthread_sigmask(SIG_SETMASK, NULL, &after) == 0);
        assert(sigismember(&before, SIGPIPE) == sigismember(&after, SIGPIPE));
        assert(sigpending(&pending) == 0);
        assert(sigismember(&pending, SIGPIPE) == initially_pending);
        clear_clipboard_data(&source);
        _exit(0);
    }

    int status;
    assert(waitpid(child, &status, 0) == child);
    assert(WIFEXITED(status) && WEXITSTATUS(status) == 0);
}

static void test_full_pipe(void)
{
    int descriptors[2];
    assert(pipe2(descriptors, O_NONBLOCK) == 0);
    unsigned char bytes[4096] = {0};
    while (write(descriptors[1], bytes, sizeof(bytes)) > 0)
    {
    }

    assert(errno == EAGAIN);
    struct clipboard_source source = make_source();
    clipboard_source_send(&source, NULL, "text/plain", descriptors[1]);
    clear_clipboard_data(&source);
    close(descriptors[0]);
}

static void test_maximum_payload(void)
{
    unsigned char payload[4096];
    unsigned char received[4096];
    memset(payload, '7', sizeof(payload));
    struct clipboard_source source = {0};
    source.data = payload;
    source.length = sizeof(payload);
    source.sensitive = 1;
    int descriptors[2];
    assert(pipe(descriptors) == 0);
    assert(fcntl(descriptors[1], F_SETPIPE_SZ, 4096) >= 4096);
    clipboard_source_send(&source, NULL, "text/plain", descriptors[1]);
    size_t offset = 0;
    while (offset < sizeof(received))
    {
        ssize_t count =
            read(descriptors[0], received + offset, sizeof(received) - offset);
        assert(count > 0);
        offset += (size_t)count;
    }

    assert(memcmp(payload, received, sizeof(payload)) == 0);
    assert(read(descriptors[0], received, sizeof(received)) == 0);
    close(descriptors[0]);
}

int main(void)
{
    alarm(10);
    struct clipboard_source source = make_source();
    expect_send(&source, "text/plain;charset=utf-8", "123456");
    expect_send(&source, "text/plain", "123456");
    expect_send(&source, "x-kde-passwordManagerHint", "secret");
    expect_send(&source, "application/unknown", "");
    assert(memcmp(source.data, "123456", 6) == 0);
    source.sensitive = 0;
    expect_send(&source, "application/custom", "123456");
    source.sensitive = 1;

    watched_buffer = source.data;
    watched_length = source.length;
    clear_clipboard_data(&source);
    assert(source.data == NULL && source.length == 0 && zeroed_frees == 1);
    clear_clipboard_data(&source);
    expect_send(&source, "text/plain", "");

    struct hypr_layer layer = {0};
    struct clipboard_source *cancelled = malloc(sizeof(*cancelled));
    assert(cancelled != NULL);
    *cancelled = make_source();
    cancelled->layer = &layer;
    layer.clipboard_sources = cancelled;
    watched_buffer = cancelled->data;
    watched_length = cancelled->length;
    clipboard_source_cancelled(cancelled, (void *)1);
    assert(layer.clipboard_sources == NULL && zeroed_frees == 2);

    test_epipe(0, 0);
    test_epipe(1, 0);
    test_epipe(1, 1);
    test_full_pipe();
    test_maximum_payload();
    puts("sensitive clipboard synthetic tests passed");
    return 0;
}
