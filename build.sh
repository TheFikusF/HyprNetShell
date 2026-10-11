#!/bin/sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
NATIVE_DIR="$ROOT_DIR/Native"
NATIVE_BUILD_DIR="$NATIVE_DIR/build/clang"
SOLUTION="$ROOT_DIR/HyprNetShell.slnx"
PROJECT="$ROOT_DIR/HyprNetShell.csproj"
PUBLISH_PROFILE="$ROOT_DIR/Properties/PublishProfiles/NativeAotOneFile.pubxml"
CREDENTIALS_FILE="$ROOT_DIR/credentials.sh"

if [ -f "$CREDENTIALS_FILE" ]; then
    # This file is intentionally gitignored and may define any subset of the OAuth credentials.
    . "$CREDENTIALS_FILE"
fi

HYPRNETSHELL_GOOGLE_CLIENT_ID=${HYPRNETSHELL_GOOGLE_CLIENT_ID:-}
HYPRNETSHELL_GOOGLE_CLIENT_SECRET=${HYPRNETSHELL_GOOGLE_CLIENT_SECRET:-}
HYPRNETSHELL_SPOTIFY_CLIENT_ID=${HYPRNETSHELL_SPOTIFY_CLIENT_ID:-}
HYPRNETSHELL_OPENAI_CLIENT_ID=${HYPRNETSHELL_OPENAI_CLIENT_ID:-}

usage() {
    cat <<EOF
Usage: $(basename "$0") <command> [arguments]

Commands:
  native             Configure and build the native libraries
  static [args]      Build the static SQLite, SkiaSharp and HarfBuzzSharp libraries
  managed [args]     Build the managed solution
  build [args]       Build the native libraries and managed solution
  run [args]         Build everything and run HyprNetShell
  publish [args]     Build native and static libraries, then publish NativeAOT
  help               Show this help

Arguments for static are passed to scripts/build-static-libraries.py.
Arguments for managed and build are passed to dotnet build.
Arguments for run are passed to HyprNetShell.
Arguments for publish are passed to dotnet publish.
Managed builds and publishes embed credentials from the environment or credentials.sh.
EOF
}

build_native() {
    if ! command -v clang >/dev/null 2>&1; then
        printf 'Clang is required to build the native libraries. Install clang and retry.\n' >&2
        exit 1
    fi

    # A separate Meson tree avoids cached GCC selection without wiping AOT dependencies.
    if [ -f "$NATIVE_BUILD_DIR/build.ninja" ]; then
        CC=clang meson setup "$NATIVE_BUILD_DIR" "$NATIVE_DIR" --reconfigure --buildtype=debugoptimized
    else
        CC=clang meson setup "$NATIVE_BUILD_DIR" "$NATIVE_DIR" --buildtype=debugoptimized
    fi

    meson compile -C "$NATIVE_BUILD_DIR"

    # Managed builds and AOT linking consume these established library paths.
    cp "$NATIVE_BUILD_DIR/libhypr_layer.so" "$NATIVE_DIR/build/libhypr_layer.so"
    cp "$NATIVE_BUILD_DIR/libhypr_layer.a" "$NATIVE_DIR/build/libhypr_layer.a"
    if [ -f "$NATIVE_BUILD_DIR/libhypr_audio.so" ]; then
        cp "$NATIVE_BUILD_DIR/libhypr_audio.so" "$NATIVE_DIR/build/libhypr_audio.so"
        cp "$NATIVE_BUILD_DIR/libhypr_audio.a" "$NATIVE_DIR/build/libhypr_audio.a"
    fi
}

build_static() {
    python3 "$ROOT_DIR/scripts/build-static-libraries.py" "$@"
}

build_managed() {
    dotnet build "$SOLUTION" \
        -p:HyprNetShellGoogleClientId="$HYPRNETSHELL_GOOGLE_CLIENT_ID" \
        -p:HyprNetShellGoogleClientSecret="$HYPRNETSHELL_GOOGLE_CLIENT_SECRET" \
        -p:HyprNetShellSpotifyClientId="$HYPRNETSHELL_SPOTIFY_CLIENT_ID" \
        -p:HyprNetShellOpenAiClientId="$HYPRNETSHELL_OPENAI_CLIENT_ID" \
        "$@"
}

build_all() {
    build_native
    build_managed "$@"
}

run() {
    build_all
    exec dotnet run --project "$PROJECT" --no-build --no-restore -- "$@"
}

publish() {
    build_native
    build_static
    exec dotnet publish "$PROJECT" \
        -p:PublishProfile="$PUBLISH_PROFILE" \
        -p:HyprNetShellGoogleClientId="$HYPRNETSHELL_GOOGLE_CLIENT_ID" \
        -p:HyprNetShellGoogleClientSecret="$HYPRNETSHELL_GOOGLE_CLIENT_SECRET" \
        -p:HyprNetShellSpotifyClientId="$HYPRNETSHELL_SPOTIFY_CLIENT_ID" \
        -p:HyprNetShellOpenAiClientId="$HYPRNETSHELL_OPENAI_CLIENT_ID" \
        "$@"
}

command=${1:-help}
if [ "$#" -gt 0 ]; then
    shift
fi

case "$command" in
    native)
        build_native
        ;;
    static)
        build_static "$@"
        ;;
    managed)
        build_managed "$@"
        ;;
    build)
        build_all "$@"
        ;;
    run)
        run "$@"
        ;;
    publish)
        publish "$@"
        ;;
    help|-h|--help)
        usage
        ;;
    *)
        printf 'Unknown command: %s\n\n' "$command" >&2
        usage >&2
        exit 2
        ;;
esac
