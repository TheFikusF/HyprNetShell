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

From the repository root, build the native library first:

```bash
meson setup Native/build Native
meson compile -C Native/build
```

Then build the managed solution:

```bash
dotnet build HyprNetShell.slnx
```

Run HyprNetShell from inside a compatible Wayland session:

```bash
dotnet run --project HyprNetShell.csproj
```

The managed build copies `Native/build/libhypr_layer.so` into the executable output directory. If the native library is missing, the build emits a warning and the application cannot start.

## Online account setup

The Settings → Accounts tab supports Google, Spotify, and ChatGPT sign-in. Client IDs can be saved directly in that tab, supplied through the environment, or embedded during publishing. Resolution uses this priority:

1. client ID embedded in the published build;
2. environment variable;
3. value saved in `config.json` through Settings → Accounts.

The supported environment variables are:

```bash
export HYPRNETSHELL_GOOGLE_CLIENT_ID="your-google-desktop-client-id"
export HYPRNETSHELL_SPOTIFY_CLIENT_ID="your-spotify-client-id"
export HYPRNETSHELL_OPENAI_CLIENT_ID="your-openai-client-id"
```

Create the Google credential as a **Desktop app** OAuth client. For Spotify, use Authorization Code with PKCE and register the exact loopback redirect URI `http://127.0.0.1:5543/auth/callback`. Spotify permits HTTP for explicit loopback IP addresses, but not for ordinary remote hosts or `localhost`. Client IDs are public identifiers; no client secret should be placed in the configuration, environment, or binary.

The initial Google connection requests identity scopes only, and the initial Spotify connection requests no optional scopes. Feature-specific permissions will be added alongside the features that need them.

ChatGPT sign-in currently mirrors Zed's Codex OAuth flow. This uses OpenAI's first-party OAuth registration and is not published as a stable third-party integration API, so it may stop working if OpenAI changes that flow.

For a self-contained NativeAOT build with embedded IDs, edit the local, gitignored `credentials.sh` and run:

```bash
./publish.sh
```

Only non-empty values from `credentials.sh` are embedded. `publish.sh` accepts additional `dotnet publish` arguments. A direct publish remains available when no embedding is wanted:

```bash
dotnet publish HyprNetShell.csproj \
  -p:PublishProfile=Properties/PublishProfiles/NativeAotOneFile.pubxml
```

The profile also creates
`bin/Release/net10.0/linux-x64/publish/HyprNetShell-0.1.0-linux-x64.tar.xz`,
ready for the Gentoo binary ebuild or a GitHub release.

## Gentoo installation

A binary-package ebuild and local-overlay instructions are available under
[`packaging/gentoo/`](packaging/gentoo/README.md). The ebuild installs the
NativeAOT bundle, runtime feature dependencies, and the PAM policy required by
the in-house lock screen.
