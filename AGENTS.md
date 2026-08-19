# AGENTS.md

This file is a repository guide for coding agents and contributors working on HyprNetShell. Keep changes focused, preserve the custom lightweight architecture, and validate both the managed and native sides when a change crosses their boundary.

## Project overview

HyprNetShell is a Linux-only Hyprland shell/status bar. It combines:

- a C native library for Wayland `wlr-layer-shell`, EGL, pointer, keyboard, and scroll input;
- a .NET 10 application loop and shell feature orchestration;
- a custom retained node layout/input system;
- an unsafe OpenGL renderer;
- a Roslyn source generator that embeds SVG assets as strongly typed C# values.

The application deliberately does not use GTK, Qt, Avalonia, SDL, GLFW, or a normal desktop window. Do not introduce one of these frameworks unless the task explicitly calls for a fundamental architecture change.

## Repository map

```text
HyprNetShell/
├── Program.cs                         Process entry point and frame loop
├── NativeMethods.cs                   Managed HyprLayer wrapper and native P/Invoke declarations
├── DesktopEntryLauncher.cs            GLib/GIO-based .desktop entry launching
├── HyprNetShell.csproj                Executable, native copy/AOT integration
├── HyprNetShell.slnx                  Managed solution
├── Core/
│   ├── HyprNetShell.Core.csproj       Bar/application domain project
│   ├── Assets/                        [SvgAsset] declarations used by generated code
│   ├── Bar/
│   │   ├── StatusBar.cs               Composition root for modules and services
│   │   ├── Theme.cs                   Shared visual theme values
│   │   ├── Modules/                   Bar modules and center widgets
│   │   └── MainDialogTabs/            Launcher, calculator, clipboard, wallpaper, config
│   ├── Features/
│   │   ├── Hyprland/                  Hyprland IPC, commands, and dynamic bindings
│   │   ├── Sni/                       StatusNotifierItem watcher, host, and D-Bus menus
│   │   └── System/                    Audio, battery, network, media, display, etc.
│   ├── Logging/                       File and stderr logging
│   ├── Models/                        Immutable service/UI snapshots
│   ├── Platform/                      Process execution and platform helpers
│   └── Services/                      Shared service interfaces
├── GUI/
│   ├── HyprNetShell.GUI.csproj        Custom UI/layout project
│   └── Layout/
│       ├── Layout.cs                  Root layout, current input, Wayland input regions
│       ├── Node.cs                    Base node and layout/style primitives
│       └── Nodes/                     Boxes, text, images, sliders, switches, scrollbars
├── Rendering/
│   ├── HyprNetShell.Rendering.csproj  Unsafe rendering project and native packages
│   ├── Renderer.cs                    OpenGL renderer implementation
│   ├── FontRenderer.cs                Embedded-font text atlas and drawing
│   ├── TextureRepository.cs           Image/SVG texture cache
│   ├── IRenderApi.cs                  Interface consumed by GUI and Core
│   └── Primitives/                    Colors, gradients, geometry, and math
├── Generators/
│   ├── HyprNetShell.Generators.csproj Roslyn analyzer/source-generator project
│   └── SvgAssetGenerator.cs           Generates values for [SvgAsset] properties
├── Native/
│   ├── meson.build                    Native shared/static library build
│   ├── hypr_layer.c                   Wayland, layer-shell, EGL, and input implementation
│   ├── hypr_layer.h                   Public C ABI
│   └── protocols/                     Vendored layer-shell protocol XML
├── Properties/PublishProfiles/        NativeAOT publish profile
└── assets/
    ├── fonts/                         Fonts embedded by Rendering
    ├── icons/                         SVG icon sets consumed as AdditionalFiles
    └── svgs/ and images               Other embedded visual assets
```

Generated and local output directories such as `bin/`, `obj/`, `.idea/`, and `Native/build/` are not source code and should not be edited directly.

## Project boundaries

### Executable

`Program.cs` owns process startup, logging lifetime, creation order, and the frame loop. Each frame:

1. `HyprLayer.Update()` polls native events and creates managed input state.
2. Input is forwarded to `StatusBar` and `GUI.Layout`.
3. `Renderer.BeginFrame()` starts OpenGL drawing.
4. `StatusBar.Draw()` builds and draws node trees.
5. GUI input rectangles are sent back to the native Wayland surface.
6. The renderer flushes and EGL swaps buffers.

Keep the executable thin. Shell modules and services belong in `Core`; reusable nodes belong in `GUI`; drawing operations belong in `Rendering`.

### Core

`Core/Bar/StatusBar.cs` is the composition root. It creates feature services and their visual modules, refreshes `IBarDataService` implementations, and controls disposal.

Use these locations consistently:

- visual behavior for one bar item: `Core/Bar/Modules/`;
- main-dialog behavior: `Core/Bar/MainDialogTabs/`;
- external state or side effects: `Core/Features/`;
- immutable values passed from a service to UI: `Core/Models/`;
- reusable process/platform helper: `Core/Platform/`;
- shared theme values: `Core/Bar/Theme.cs`.

Feature services should expose snapshots rather than allowing rendering code to mutate service internals. Command failures and unavailable hardware should normally produce an empty snapshot or preserve the previous snapshot instead of terminating the frame loop.

### GUI

`GUI` is a small retained node system, not a wrapper around an external toolkit. Nodes measure, arrange, draw, and register interactive rectangles. Input originates in `Layout.Input`, and interactive nodes contribute rectangles through `Layout.AddInputRegion`.

When adding a node:

- put reusable controls under `GUI/Layout/Nodes/`;
- render exclusively through `IRenderApi`;
- preserve layout sizing and alignment conventions in `Node.cs`;
- ensure clickable/scrollable areas register correct input regions;
- avoid placing feature-specific business logic in GUI primitives.

### Rendering

`Rendering` owns unsafe and OpenGL-specific code. It loads OpenGL through the proc-address callback supplied by the EGL native layer. `IRenderApi` is the abstraction used by higher projects.

Add generic drawing capabilities to `IRenderApi` and `Renderer` together. Keep GL resources owned and disposed in `Rendering`; do not leak Silk.NET types into `Core` or `GUI`. Fonts are embedded resources, while SVG/image resources are decoded and cached by the texture repository.

### Generators and assets

The Core project includes SVG files as `AdditionalFiles` and references `Generators` as an analyzer. `SvgAssetGenerator` finds static partial properties decorated with `[SvgAsset]`, validates their asset paths, and emits base64-backed `SvgAsset` values.

To add an icon:

1. Prefer an existing file under `assets/icons/`.
2. Add a static partial property to `Core/Assets/Icons.cs` or the appropriate asset class.
3. Annotate it with the repository-relative asset path.
4. Build and resolve `HNSVG001`/`HNSVG002` diagnostics rather than manually writing generated code.

Do not edit files under `obj/` produced by the generator.

### Native boundary

`Native/hypr_layer.h` defines the C ABI. Every ABI change must be kept in sync across:

- `Native/hypr_layer.h`;
- `Native/hypr_layer.c`;
- the `NativeMethods` declarations in `NativeMethods.cs`;
- the managed `HyprLayer` wrapper when behavior or ownership changes.

The native object owns Wayland globals, surfaces, seats/input objects, xkb state, and EGL resources. Preserve deterministic cleanup and error propagation through `hypr_layer_has_error`. Do not throw C++ exceptions or expose C++ ABI types; this is a C11 library.

The full-output transparent surface reserves only the configured top bar height. Its Wayland input region is dynamically restricted to rectangles supplied by the managed layout. Changes to surface size, exclusive zone, anchors, keyboard interactivity, or input regions can affect compositor behavior and require an in-session test.

## External integrations

The code intentionally uses Linux and desktop command-line interfaces instead of adding large framework dependencies. Important integrations include:

- Hyprland IPC and `hyprctl`;
- `socat` for callbacks from dynamically registered Hyprland binds;
- session D-Bus for notifications, StatusNotifierItem, dbusmenu, and MPRIS;
- `wpctl`, `nmcli`, `bluetoothctl`, `powerprofilesctl`, and `playerctl`;
- `wl-copy`/`wl-paste` for clipboard transport;
- `hyprpaper`, `hyprsunset`, and `hyprlock`;
- Linux `sysfs` for battery, backlight, thermal, and hardware data;
- GLib/GIO native libraries for `.desktop` entry launching.

Use `Core/Platform/CommandRunner.cs` for ordinary process calls where possible. Give commands bounded timeouts, honor cancellation during refreshes, and avoid blocking the render thread. Do not invoke a shell when an argument list can be passed directly. Existing shell command construction in Hyprland binding registration is a special case because Hyprland executes the registered command.

## State, configuration, and logging

Persistent files follow XDG locations:

- wallpaper settings: `$XDG_CONFIG_HOME/hyprnetshell/wallpapers.json`;
- temperature schedule: `$XDG_CONFIG_HOME/hyprnetshell/temperature-curve.json`;
- battery charge limit: `$XDG_CONFIG_HOME/hyprnetshell/battery-charge-limit`;
- logs: `$XDG_STATE_HOME/hyprnetshell/hyprnetshell.log`.

When XDG variables are unset, use the existing `~/.config` and `~/.local/state` fallbacks. Keep JSON models compatible with source-generated `System.Text.Json` contexts so NativeAOT remains viable. Clipboard and notification histories are intentionally in memory.

Use `AppLogger` for runtime failures in Core. Expected optional-feature failures should usually be warnings or empty states, while fatal startup/frame-loop failures are handled in `Program.cs`.

## Coding conventions

- Target frameworks are .NET 10 for runtime projects and `netstandard2.0` for the generator.
- Nullable reference types and implicit usings are enabled.
- Match the existing file-scoped namespace and modern C# style.
- Keep unsafe code and native resource handling concentrated in `Rendering` or the interop layer.
- Prefer records/record structs for snapshots and value types.
- Preserve cancellation and timeout behavior in asynchronous services.
- Implement `IDisposable` for owners of processes, tasks, D-Bus connections, native handles, or GL resources.
- Dispose in reverse ownership order and make shutdown tolerant of already-exited external processes.
- Do not add comments that merely narrate code; document non-obvious protocol, ownership, and concurrency constraints.
- Avoid new dependencies when the existing architecture or platform APIs can solve the task cleanly.

## Build and validation

Build native code before managed code:

```bash
meson setup Native/build Native
meson compile -C Native/build
dotnet build HyprNetShell.slnx
```

For an existing native build after Meson changes:

```bash
meson setup Native/build Native --reconfigure
meson compile -C Native/build
```

NativeAOT validation:

```bash
dotnet publish HyprNetShell.csproj \
  -p:PublishProfile=Properties/PublishProfiles/NativeAotOneFile.pubxml
```

There is currently no automated test project. At minimum:

- managed-only changes: run `dotnet build HyprNetShell.slnx`;
- native-only changes: run `meson compile -C Native/build`, then rebuild the executable;
- ABI changes: run both builds and verify the copied shared library;
- generator/asset changes: run the managed build and check generator diagnostics;
- trimming/reflection/serialization changes: also run the NativeAOT publish;
- input, rendering, D-Bus, or compositor changes: smoke-test inside Hyprland when possible.

Do not claim an in-session behavior test unless it was actually performed. If the environment is not a running Hyprland session, state that limitation clearly.

## Change checklist

Before finishing a change, check the relevant items:

- Is the code in the correct project and directory?
- Did a model/service/UI change update all of its call sites?
- Does a native API change match the managed declaration exactly?
- Are external commands cancellable, bounded, and safely argument-escaped?
- Are new owned resources disposed during shutdown?
- Are asset paths included by `Core/HyprNetShell.Core.csproj` and recognized by the generator?
- Does the change remain compatible with NativeAOT and source-generated JSON?
- Were the most specific available build/validation commands run?
