# Synthetic sensitive clipboard test

Run from the repository root after `./build.sh native` has generated the protocol
headers. No compositor connection is opened and no real clipboard is modified.
Only the source-destruction proxy calls are mocked; sends use real local pipes.

```sh
cc -std=c11 -UNDEBUG -Wall -Wextra -Werror \
  -ffunction-sections -fdata-sections -pthread \
  -I Native/build/clang Native/sensitive_clipboard_test.c \
  -Wl,--gc-sections -Wl,--wrap=wl_proxy_marshal_flags \
  -Wl,--wrap=wl_proxy_get_version -lwayland-client \
  -o /tmp/sensitive_clipboard_test
/tmp/sensitive_clipboard_test
```

If using another native build directory, replace `Native/build/clang` with its
path. Requires Linux, a GNU-compatible linker, the native development headers,
and generated Wayland protocol headers. Assertions must remain enabled.

For GCC ASan/UBSan, add `-fsanitize=address,undefined --param asan-globals=0
-fno-omit-frame-pointer` to the compile/link invocation. Disabling global
instrumentation permits unused compositor callback tables to be discarded;
heap and stack instrumentation remain enabled. Under a ptrace-based sandbox,
run with `ASAN_OPTIONS=detect_leaks=0` because LeakSanitizer cannot operate there.
A ten-second alarm prevents blocking-send regressions
from hanging the runner; EPIPE cases run in isolated children with default
SIGPIPE disposition and a three-second alarm.

Coverage:

- Both text MIME types return the OTP; the password-manager hint returns exactly
  `secret`, without a NUL terminator or OTP bytes.
- Unknown sensitive MIME types return an empty stream.
- Nonsensitive custom MIME sends preserve existing behavior.
- All 4096 bytes are delivered into an initially empty minimum-sized Linux pipe.
- A full nonblocking pipe does not stall the callback.
- EPIPE does not kill the process, restores the previous SIGPIPE mask, consumes
  a newly generated SIGPIPE, and preserves an already-pending SIGPIPE.
- A free interceptor verifies sensitive bytes are zero immediately before free.
  Repeated cleanup is safe, and source cancellation unlinks and clears storage.

Limitations: this is a standalone white-box regression test, not a Meson target
(new test files only were authorized). It includes the production translation
unit and discards unrelated functions with linker section garbage collection.
It does not simulate source creation, offer advertisement, selection replacement,
allocation/flush failures, full layer destruction, managed queuing, or a live
compositor. Cleanup's zeroing primitive is tested directly and through source
cancellation; other cleanup call sites still require code review.
