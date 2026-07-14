# HyprNetShell

Minimal C#/.NET + native Wayland `wlr-layer-shell` status bar foundation for Hyprland.

It creates a top layer-shell surface, reserves top screen space with `exclusive_zone`, creates an EGL/OpenGL context, and renders a Hyprland-oriented status bar from C# using Silk.NET OpenGL.

## Structure

```text
Core/                 bar composition, state models, feature services
Core/Bar/             StatusBar drawing and theme
Core/Features/        Hyprland, system, and SNI feature ports
Core/Platform/        small platform helpers such as command execution
Native/               Wayland layer-shell + EGL shared library
Rendering/            unsafe Silk.NET renderer and 0xProto text atlas
```

The main executable project intentionally does not enable `AllowUnsafeBlocks`. Unsafe OpenGL/font code lives in `Rendering/HyprNetShell.Rendering.csproj`. The executable only owns the Wayland loop; `Core` owns the bar state refresh and calls the render API.

## Dependencies

Runtime/build dependencies:

```bash
wayland
wayland-protocols
wlr-protocols
wayland-scanner
egl
opengl
meson
ninja
dotnet-sdk
wireplumber (for the `wpctl` audio controls)
bluez-utils (for the `bluetoothctl` device controls)
xkbcommon (for keyboard-layout-aware launcher input)
socat (for Hyprland hotkey notifications)
cliphist and wl-clipboard (for clipboard history)
```

Gentoo package names are typically:

```bash
dev-libs/wayland
dev-libs/wayland-protocols
gui-libs/wlroots
media-libs/mesa
dev-build/meson
dev-build/ninja
dev-dotnet/dotnet-sdk-bin
```

The native project vendors the small layer-shell v1 protocol XML in `Native/protocols/`, so `wlr-protocols` is useful as a reference package but is not required by this Meson build.

## Build

Build the native shared library:

```bash
cd Native
meson setup build
meson compile -C build
```

The Meson build runs `wayland-scanner` and generates:

```text
wlr-layer-shell-unstable-v1-client-protocol.h
wlr-layer-shell-unstable-v1-protocol.c
```

Build/restore the C# app:

```bash
dotnet build HyprNetShell.csproj
```

The .NET build copies `Native/build/libhypr_layer.so` into `bin/Debug/net10.0`, so Rider can run the app directly after the native library has been built.

```bash
dotnet run --project HyprNetShell.csproj
```

Run this inside Hyprland or another compositor implementing `zwlr_layer_shell_v1`.

The built-in Hyprland key watcher registers `SUPER+L` to toggle the application launcher and removes the
binding during shutdown.

## What It Draws

The current renderer draws:

```text
left:   Hyprland workspace boxes and focused window title
center: date and time modules
right:  network, battery, and future tray modules
```

Rendering is intentionally primitive:

- filled rectangles
- rounded module boxes based on the main `bar.css` theme values
- rectangle borders
- 0xProto text rendered via a cached STB TrueType atlas
- simple horizontal layout blocks

Set `HYPRBAR_FONT_PATH` if your 0xProto TTF lives somewhere other than `/usr/local/share/fonts/0xProto-Regular-NL.ttf`.

The weather widget defaults to Prague and can be pointed at another location with:

```bash
export HYPRNETSHELL_WEATHER_LATITUDE="50.0755"
export HYPRNETSHELL_WEATHER_LONGITUDE="14.4378"
export HYPRNETSHELL_WEATHER_LOCATION="Prague"
```

Set `HYPRNETSHELL_WEATHER_URL` to override the page opened when the widget is clicked.

## Native ABI

The native library exposes a small C ABI:

```c
typedef struct hypr_layer_window hypr_layer_window;

hypr_layer_window* hypr_layer_create_top_bar(int reserved_height);
void hypr_layer_destroy(hypr_layer_window* window);

void hypr_layer_make_current(hypr_layer_window* window);
void hypr_layer_swap_buffers(hypr_layer_window* window);
void hypr_layer_poll_events(hypr_layer_window* window);

int hypr_layer_get_width(hypr_layer_window* window);
int hypr_layer_get_height(hypr_layer_window* window);
int hypr_layer_should_close(hypr_layer_window* window);
int hypr_layer_has_error(hypr_layer_window* window);

void* hypr_layer_get_proc_address(const char* name);
```

`hypr_layer_create_top_bar` creates a full-output drawable layer surface while
keeping `reserved_height` as the top exclusive zone, so normal tiled windows
still reserve only the bar-sized strip. On compositors exposing layer-shell v5,
the surface explicitly marks the top edge as the exclusive edge. The Wayland
input region is limited to the same top strip, so transparent full-screen
drawing space does not intercept pointer input below the bar.

`hypr_layer_get_proc_address` is passed to Silk.NET so C# can load OpenGL entry points from the EGL context.

## Notes

This is a foundation, not a full Waybar replacement. It deliberately avoids GTK, Qt, SDL, GLFW, Avalonia, normal desktop windows, and large abstractions.

The comments in `Native/hypr_layer.c` focus on the Wayland/EGL setup path because that is the least familiar and most failure-prone part.
