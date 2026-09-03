<img width="1920" height="69" alt="image" src="https://github.com/user-attachments/assets/96eabc48-c185-4ef3-b153-fb05bbcba1c5" />

# HyprNetShell

HyprNetShell is an experimental Linux status bar and desktop shell for Hyprland, written in C# and C. It creates a Wayland `wlr-layer-shell` surface through a small native library and renders a custom interface directly with OpenGL.

The project includes Hyprland workspaces, system and media controls, notifications, a system tray, and an application launcher. It uses its own layout and input system instead of GTK, Qt, Avalonia, SDL, or GLFW.

## Repository structure

```text
Core/           Bar composition, modules, models, and system/Hyprland services
GUI/            Custom retained node layout and input system
Rendering/      OpenGL renderer, text, image, and SVG support
Generators/     Roslyn source generator for embedded SVG assets
Native/         Wayland layer-shell, EGL, and input native library
assets/         Embedded fonts, icons, SVGs, and images
Program.cs      Application entry point and frame loop
NativeMethods.cs
                Managed wrapper and P/Invoke declarations for Native/
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

Runtime features additionally use tools such as `hyprctl`, `socat`, `wpctl`, `nmcli`, `bluetoothctl`, `wl-clipboard`, `hyprpaper`, and `hyprsunset`.

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

For a self-contained NativeAOT build:

```bash
dotnet publish HyprNetShell.csproj \
  -p:PublishProfile=Properties/PublishProfiles/NativeAotOneFile.pubxml
```
