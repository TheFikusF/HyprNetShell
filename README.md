<img width="1920" height="69" alt="image" src="https://github.com/user-attachments/assets/96eabc48-c185-4ef3-b153-fb05bbcba1c5" />

# HyprNetShell

HyprNetShell is an experimental Linux status bar and desktop shell for Hyprland, written in C# and C. It creates a Wayland `wlr-layer-shell` surface through a small native library and renders a custom interface directly with OpenGL.

The project includes Hyprland workspaces, system and media controls, notifications, a system tray, and an application launcher. It uses its own layout and input system instead of GTK, Qt, Avalonia, SDL, or GLFW.

## Repository structure

```text
Application/    Shell orchestration, lock screen, screenshots, and launch helpers
Core/           Bar composition, modules, models, and system/Hyprland services
GUI/            Custom retained node layout and input system
Rendering/      OpenGL renderer, text, image, and SVG support
Generators/     Roslyn source generator for embedded SVG assets
Native/         Native library plus managed Wayland/PInvoke wrappers
assets/         Embedded fonts, icons, SVGs, and images
Program.cs      Thin command-line mode dispatcher
```

See [`AGENTS.md`](AGENTS.md) for a more detailed project map.

## Build

### Requirements

- Linux x86-64 running Hyprland or another compositor with `zwlr_layer_shell_v1`
- .NET 10 SDK
- A C11 compiler and `pkg-config`
- Meson and Ninja
- Wayland client and Wayland EGL development files
- EGL/OpenGL development files
- xkbcommon development files
- `wayland-scanner`
- GLib/GIO runtime libraries

The layer-shell protocol XML is vendored in `Native/protocols/`, so a separate `wlr-protocols` package is not required.

Runtime features additionally use tools such as `hyprctl`, `socat`, `wpctl`, `nmcli`, `bluetoothctl`, `wl-clipboard`, `hyprpaper`, and `hyprsunset`. Media metadata and controls use MPRIS over the session D-Bus.

Online account credentials require a Freedesktop Secret Service provider such as GNOME Keyring, KDE Wallet, or KeePassXC with Secret Service enabled. HyprNetShell does not fall back to plaintext credential files.

### Steps

From the repository root, build the native libraries and managed solution through the project script:

```bash
./build.sh build
```

The native and managed sides can also be built independently:

```bash
./build.sh native
./build.sh managed
```

Build and run HyprNetShell from inside a compatible Wayland session:

```bash
./build.sh run
```

Run `./build.sh help` for the complete command list and argument-forwarding behavior.

The managed build copies `Native/build/libhypr_layer.so` into the executable output directory. If the native library is missing, the build emits a warning and the application cannot start.

## Online account setup

The Settings → Accounts tab supports Google, Spotify, and ChatGPT sign-in. Client IDs can be saved directly in that tab, supplied through the environment, or embedded during publishing. Resolution uses this priority:

1. client ID embedded by `build.sh`;
2. environment variable;
3. value saved in `config.json` through Settings → Accounts.

The supported environment variables are:

```bash
export HYPRNETSHELL_GOOGLE_CLIENT_ID="your-google-desktop-client-id"
export HYPRNETSHELL_GOOGLE_CLIENT_SECRET="your-google-desktop-client-secret"
export HYPRNETSHELL_SPOTIFY_CLIENT_ID="your-spotify-client-id"
export HYPRNETSHELL_OPENAI_CLIENT_ID="your-openai-client-id"
```

Create the Google credential as a **Desktop app** OAuth client and supply both values from its downloaded credential JSON. Google assumes installed apps cannot keep the desktop `client_secret` confidential; PKCE protects each authorization-code exchange, while the static value may still be required by Google's token endpoint. The secret is intentionally excluded from `config.json`, the settings UI, status snapshots, and logs. Supply it through `HYPRNETSHELL_GOOGLE_CLIENT_SECRET` or the gitignored `credentials.sh` when building or running through `build.sh`.

For Spotify, use Authorization Code with PKCE and register the exact loopback redirect URI `http://127.0.0.1:5543/auth/callback`. Spotify permits HTTP for explicit loopback IP addresses, but not for ordinary remote hosts or `localhost`. Do not configure client secrets for Spotify or ChatGPT.

The initial Google connection requests identity scopes only, and the initial Spotify connection requests no optional scopes. Feature-specific permissions will be added alongside the features that need them.

ChatGPT sign-in currently mirrors Zed's Codex OAuth flow. This uses OpenAI's first-party OAuth registration and is not published as a stable third-party integration API, so it may stop working if OpenAI changes that flow.

The script embeds non-empty values from the environment or the local, gitignored `credentials.sh` in every managed build. For a self-contained NativeAOT build, run:

```bash
./build.sh publish
```

`build.sh publish` accepts additional `dotnet publish` arguments.

The profile also creates
`bin/Release/net10.0/linux-x64/publish/HyprNetShell-0.1.0-linux-x64.tar.xz`,
ready for the Gentoo binary ebuild or a GitHub release.

## Gentoo installation

A binary-package ebuild and local-overlay instructions are available under
[`packaging/gentoo/`](packaging/gentoo/README.md). The ebuild installs the
NativeAOT bundle, runtime feature dependencies, and the PAM policy required by
the in-house lock screen.

## Window Overview

Press **SUPER + O** to toggle a separate fullscreen Overview on the currently
focused output. This dynamic Hyprland binding uses the existing `socat` callback
mechanism and is registered only if SUPER + O is unused; conflicts or registration
failures are logged rather than replacing your binding. Hovering the workspace
module still opens its original text-only popup and never starts thumbnail capture.

Overview shows an automatically fitted window grid with monitor/workspace labels.
Use the **arrow keys** to select a window and **Enter** to focus it, or click a
preview to focus its exact Hyprland window address and close. **Escape** closes
Overview. Opening and closing animate with a short fade/scale transition; hovered
previews lift and enlarge. Windows without an unambiguous exact-address thumbnail mapping
show a placeholder; titles and application IDs are never used to guess mappings.
Only mapped windows in the Overview request capture. Live capture is capped
at 15 FPS per window, with round-robin scheduling to avoid starting every capture
in the same event-loop tick. The protocol still requires full-resolution SHM,
but native conversion produces aspect-preserved, bilinear-filtered RGBA previews
bounded by 640×360; only those smaller buffers are copied into managed memory
and uploaded. This trades some detail in large previews for lower CPU, allocation,
and upload costs. Layout/captions are cached, and texture cleanup runs once after
all outputs rather than evicting another output's live textures. Preview availability
depends on the compositor protocols supported by the existing thumbnail backend.

The background is a dark-tinted, centered cover crop of the current wallpaper
image managed by the shell—not a desktop screenshot. It is decoded, downsampled
to at most 768 pixels on either axis, and blurred once asynchronously when the
wallpaper path changes. Immutable RGBA pixels use a stable texture key with a new
revision per wallpaper; no image decoding or blur runs on the render thread.
Missing, loading, or unsupported wallpapers fall back to the theme panel color;
load failures are logged. Wallpapers changed externally (or files overwritten at
the same path) are not detected by this cache.

The Overview owns one output and keyboard focus. Screenshot selection, a shell
dialog, locking, removal of its output, or shutdown closes it and releases capture
demand. Other outputs retain their bars without receiving Overview input.
`StatusBarServices.Overview` exposes `RequestToggle`, `ProcessPendingRequests`,
`HandleInput`, `Draw`, `Close`, `IsVisible`, and `OwnerOutputId`; Application owns
output selection and fullscreen layout orchestration.
