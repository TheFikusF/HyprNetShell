#!/bin/sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
CREDENTIALS_FILE="$ROOT_DIR/credentials.sh"

if [ -f "$CREDENTIALS_FILE" ]; then
    # This file is intentionally gitignored and may define any subset of the IDs.
    . "$CREDENTIALS_FILE"
fi

HYPRNETSHELL_GOOGLE_CLIENT_ID=${HYPRNETSHELL_GOOGLE_CLIENT_ID:-}
HYPRNETSHELL_SPOTIFY_CLIENT_ID=${HYPRNETSHELL_SPOTIFY_CLIENT_ID:-}
HYPRNETSHELL_OPENAI_CLIENT_ID=${HYPRNETSHELL_OPENAI_CLIENT_ID:-}

exec dotnet publish "$ROOT_DIR/HyprNetShell.csproj" \
    -p:PublishProfile="$ROOT_DIR/Properties/PublishProfiles/NativeAotOneFile.pubxml" \
    -p:HyprNetShellGoogleClientId="$HYPRNETSHELL_GOOGLE_CLIENT_ID" \
    -p:HyprNetShellSpotifyClientId="$HYPRNETSHELL_SPOTIFY_CLIENT_ID" \
    -p:HyprNetShellOpenAiClientId="$HYPRNETSHELL_OPENAI_CLIENT_ID" \
    "$@"
