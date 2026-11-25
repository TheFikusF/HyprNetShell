#define _GNU_SOURCE
#include "hypr_su.h"

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define SU_PATH "/usr/bin/su"
#define TIMEOUT_SECONDS 60
#define PASSWORD_LIMIT 255
#define CLEANUP_CHECKS 220
#define CLEANUP_INTERVAL_NS 10000000L

static const char PASSWORD_PROMPT[] = "Password: ";

typedef struct
{
    int master;
    pid_t pid;
    struct timespec deadline;
    size_t prompt_bytes;
    unsigned int ending_bytes;
    int result;
    int status;
    bool answered;
    bool eof;
    bool reaped;
} su_auth;

static int spawn_su(pid_t *pid, const char *slave_path, const char *command)
{
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attr;
    sigset_t mask;
    sigset_t defaults;
    char *const argv[] = {SU_PATH,         "--shell", "/bin/sh", "--command",
                          (char *)command, "--",      "root",    NULL};
    char *const env[] = {"LC_ALL=C", "LANG=C", "PATH=/usr/bin:/bin", NULL};

    if (posix_spawn_file_actions_init(&actions) != 0)
    {
        return -1;
    }

    if (posix_spawnattr_init(&attr) != 0)
    {
        posix_spawn_file_actions_destroy(&actions);
        return -1;
    }

    sigemptyset(&mask);
    sigfillset(&defaults);
    // After setsid, opening the slave without O_NOCTTY makes it the controlling
    // terminal.
    int error =
        posix_spawnattr_setflags(&attr, POSIX_SPAWN_SETSID |
                                            POSIX_SPAWN_SETSIGMASK |
                                            POSIX_SPAWN_SETSIGDEF) ||
        posix_spawnattr_setsigmask(&attr, &mask) ||
        posix_spawnattr_setsigdefault(&attr, &defaults) ||
        posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, slave_path,
                                         O_RDWR, 0) ||
        posix_spawn_file_actions_adddup2(&actions, STDIN_FILENO,
                                         STDOUT_FILENO) ||
        posix_spawn_file_actions_adddup2(&actions, STDIN_FILENO,
                                         STDERR_FILENO) ||
        posix_spawn_file_actions_addclosefrom_np(&actions, STDERR_FILENO + 1) ||
        posix_spawn(pid, SU_PATH, &actions, &attr, argv, env);
    posix_spawnattr_destroy(&attr);
    posix_spawn_file_actions_destroy(&actions);
    return error;
}

void *hypr_su_start(const char *command)
{
    if (command == NULL || *command == '\0')
    {
        return NULL;
    }

    su_auth *auth = calloc(1, sizeof(*auth));
    if (auth == NULL)
    {
        return NULL;
    }

    if (clock_gettime(CLOCK_MONOTONIC, &auth->deadline) != 0)
    {
        free(auth);
        return NULL;
    }

    auth->deadline.tv_sec += TIMEOUT_SECONDS;
    auth->master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC | O_NONBLOCK);
    char slave_path[128];
    int slave = -1;
    if (auth->master >= 0 && grantpt(auth->master) == 0 &&
        unlockpt(auth->master) == 0 &&
        ptsname_r(auth->master, slave_path, sizeof(slave_path)) == 0)
    {
        slave = open(slave_path, O_RDWR | O_NOCTTY | O_CLOEXEC);
    }

    bool started = slave >= 0 && fchmod(slave, S_IRUSR | S_IWUSR) == 0 &&
                   spawn_su(&auth->pid, slave_path, command) == 0;
    if (slave >= 0)
    {
        close(slave);
    }

    if (!started)
    {
        if (auth->master >= 0)
        {
            close(auth->master);
        }

        free(auth);
        return NULL;
    }

    return auth;
}

static int fail(su_auth *auth)
{
    if (!auth->reaped)
    {
        kill(auth->pid, SIGTERM);
    }

    close(auth->master);
    auth->master = -1;
    auth->result = -1;
    return -1;
}

static bool hidden_canonical_input(int fd)
{
    struct termios tty;
    return tcgetattr(fd, &tty) == 0 &&
           (tty.c_lflag & (ECHO | ICANON)) == ICANON;
}

static bool consume_output(su_auth *auth, unsigned char ch)
{
    if (!auth->answered)
    {
        if (auth->prompt_bytes >= sizeof(PASSWORD_PROMPT) - 1 ||
            ch != PASSWORD_PROMPT[auth->prompt_bytes])
        {
            return false;
        }

        auth->prompt_bytes++;
        return true;
    }

    // PAM may finish the hidden prompt with LF (normally mapped to CRLF by the
    // PTY).
    if ((auth->ending_bytes == 0 && (ch == '\r' || ch == '\n')) ||
        (auth->ending_bytes == 1 && ch == '\n'))
    {
        auth->ending_bytes = ch == '\n' ? 2 : 1;
        return true;
    }

    return false;
}

int hypr_su_poll(void *handle)
{
    su_auth *auth = handle;
    if (auth == NULL)
    {
        return -1;
    }

    if (auth->result != 0)
    {
        return auth->result;
    }

    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0 ||
        now.tv_sec > auth->deadline.tv_sec ||
        (now.tv_sec == auth->deadline.tv_sec &&
         now.tv_nsec >= auth->deadline.tv_nsec))
    {
        return fail(auth);
    }

    bool drained = false;
    for (int attempt = 0; !auth->eof && attempt < 16; attempt++)
    {
        unsigned char ch;
        ssize_t count = read(auth->master, &ch, 1);
        if (count == 1)
        {
            if (!consume_output(auth, ch))
            {
                return fail(auth);
            }
        }
        else if (count == 0 || errno == EIO)
        {
            auth->eof = true;
        }
        else if (errno == EAGAIN || errno == EWOULDBLOCK)
        {
            drained = true;
            break;
        }
        else if (errno != EINTR)
        {
            return fail(auth);
        }
    }

    if (!auth->reaped)
    {
        pid_t exited = waitpid(auth->pid, &auth->status, WNOHANG);
        if (exited == auth->pid)
        {
            auth->reaped = true;
        }
        else if (exited < 0 && errno != EINTR)
        {
            auth->reaped = errno == ECHILD;
            return fail(auth);
        }
    }

    if (auth->reaped && auth->eof)
    {
        if ((!auth->answered && auth->prompt_bytes != 0) ||
            !WIFEXITED(auth->status) || WEXITSTATUS(auth->status) != 0 ||
            auth->ending_bytes == 1)
        {
            return fail(auth);
        }

        close(auth->master);
        auth->master = -1;
        auth->result = 2;
        return 2;
    }

    return drained && !auth->eof && !auth->reaped && !auth->answered &&
                   auth->prompt_bytes == sizeof(PASSWORD_PROMPT) - 1 &&
                   hidden_canonical_input(auth->master)
               ? 1
               : 0;
}

int hypr_su_answer(void *handle, const char *response)
{
    su_auth *auth = handle;
    if (auth == NULL || response == NULL || auth->answered)
    {
        return 0;
    }

    size_t length = strnlen(response, PASSWORD_LIMIT + 1);
    if (length > PASSWORD_LIMIT)
    {
        return 0;
    }

    for (size_t i = 0; i < length; i++)
    {
        unsigned char ch = (unsigned char)response[i];
        if (ch < 0x20 || ch == 0x7f)
        {
            return 0;
        }
    }

    if (hypr_su_poll(auth) != 1)
    {
        return 0;
    }

    char line[PASSWORD_LIMIT + 1];
    memcpy(line, response, length);
    line[length++] = '\n';
    ssize_t written = hidden_canonical_input(auth->master)
                          ? write(auth->master, line, length)
                          : -1;
    explicit_bzero(line, sizeof(line));
    if (written != (ssize_t)length)
    {
        fail(auth);
        return 0;
    }

    auth->answered = true;
    return 1;
}

void hypr_su_destroy(void *handle)
{
    su_auth *auth = handle;
    if (auth == NULL)
    {
        return;
    }

    if (auth->master >= 0)
    {
        fail(auth);
    }

    if (!auth->reaped)
    {
        // Give util-linux su time to terminate its command before forcing su
        // itself out.
        struct timespec pause = {.tv_nsec = CLEANUP_INTERVAL_NS};
        for (int check = 0; check < CLEANUP_CHECKS; check++)
        {
            pid_t exited = waitpid(auth->pid, NULL, WNOHANG);
            if (exited == auth->pid || (exited < 0 && errno == ECHILD))
            {
                free(auth);
                return;
            }

            nanosleep(&pause, NULL);
        }

        kill(auth->pid, SIGKILL);
        while (waitpid(auth->pid, NULL, 0) < 0 && errno == EINTR)
        {
        }
    }

    free(auth);
}
