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
├── Program.cs                         Thin command-line mode dispatcher
├── HyprNetShell.csproj                Executable, native copy/AOT integration
├── HyprNetShell.slnx                  Managed solution
├── Application/
│   ├── ShellApplication.cs            Shell startup and frame-loop orchestration
│   ├── DesktopEntries/                GLib/GIO-based .desktop entry launching
│   ├── Diagnostics/                   Optional runtime performance profiling
│   ├── LockScreen/                    Lock process and background transfer
│   └── Screenshots/                   Selection, capture, PNG, and OCR handling
├── Core/
│   ├── HyprNetShell.Core.csproj       Bar/application domain project
│   ├── Assets/                        [SvgAsset] declarations used by generated code
│   ├── Bar/
│   │   ├── StatusBar.cs               Composition root for modules and services

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
│   ├── Theme.cs                       Shared visual theme values
│   ├── ThemeManager.cs                Current application theme
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
│   ├── Managed/                       HyprLayer/session-lock wrappers and P/Invoke declarations
│   ├── meson.build                    Native shared/static library build
│   ├── hypr_layer.c                   Wayland, layer-shell, EGL, and input implementation
│   ├── hypr_layer.h                   Public C ABI
│   └── protocols/                     Vendored Wayland protocol XML
├── Properties/PublishProfiles/        NativeAOT publish profile
└── assets/
    ├── fonts/                         Fonts embedded by Rendering
    ├── icons/                         SVG icon sets consumed as AdditionalFiles
    └── svgs/ and images               Other embedded visual assets
```

Generated and local output directories such as `bin/`, `obj/`, `.idea/`, and `Native/build/` are not source code and should not be edited directly.

## Project boundaries

### Executable

`Program.cs` only dispatches command-line modes. `Application/ShellApplication.cs` owns normal shell startup, logging lifetime, creation order, and the frame loop. Each frame:

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
- shared theme values: `GUI/Theme.cs`, accessed through `ThemeManager.Current`.

Feature services should expose snapshots rather than allowing rendering code to mutate service internals. Command failures and unavailable hardware should normally produce an empty snapshot or preserve the previous snapshot instead of terminating the frame loop.

### GUI

`GUI` is a small retained node system, not a wrapper around an external toolkit. Nodes measure, arrange, draw, and register interactive rectangles. Input originates in `Layout.Input`, and interactive nodes contribute rectangles through `Layout.AddInputRegion`.

`ThemeManager.Current` owns the application theme; do not pass theme instances through modules or dialogs. Text and dropdown nodes use its text size/color when constructed unless explicitly overridden. Box borders use its border color when a nonzero border width is set without an explicit color. Rebuild retained text/dropdown nodes after replacing the theme to refresh their defaults.

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
- the `NativeMethods` declarations in `Native/Managed/NativeMethods.cs`;
- the managed wrapper in `Native/Managed/HyprLayer.cs` when behavior or ownership changes.

The native object owns Wayland globals, surfaces, seats/input objects, xkb state, and EGL resources. Preserve deterministic cleanup and error propagation through `hypr_layer_has_error`. Do not throw C++ exceptions or expose C++ ABI types; this is a C11 library.

The full-output transparent surface reserves only the configured top bar height. Its Wayland input region is dynamically restricted to rectangles supplied by the managed layout. Changes to surface size, exclusive zone, anchors, keyboard interactivity, or input regions can affect compositor behavior and require an in-session test.

## External integrations

The code intentionally uses Linux and desktop command-line interfaces instead of adding large framework dependencies. Important integrations include:

- Hyprland IPC and `hyprctl`;
- `socat` for callbacks from dynamically registered Hyprland binds;
- session D-Bus for notifications, StatusNotifierItem, dbusmenu, and MPRIS;
- `wpctl`, `nmcli`, `bluetoothctl`, and `powerprofilesctl`;
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

Do not silently swallow runtime failures. Any caught exception or unsuccessful external HTTP/process result that causes a fallback, empty snapshot, preserved snapshot, or failed user action must be logged with the operation and actionable failure details. Expected cancellation and the normal absence of an optional account, service, or hardware do not require warnings. Never include access tokens, credentials, authorization headers, or other secrets in logs.

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

## Formatting rules and mandatory final pass

`.editorconfig` is the formatting baseline. These rules apply to all new and edited source files. The mandatory full-solution formatter may also update other source files; do not make unrelated functional changes or manually edit generated output.

### Blocks and spacing

- Use braces for every control-flow body, even a single statement. Never write `if (condition) DoSomething();` or a nonempty inline control-flow block.
- Use Allman braces: opening and closing braces each have their own line, with four-space indentation in C#.
- Only object declarations/initializers and empty blocks may keep `{ }` on the same line. An empty block such as `catch (OperationCanceledException) { }` is permitted, not required; existing logging and cancellation rules still apply.
- After a closing brace, leave exactly one empty line before any following code, including `else`, `catch`, and `finally`. Consecutive enclosing closing braces and punctuation belonging to the same expression (such as `};` or `});`) do not need intervening empty lines. Do not insert a trailing empty line when no code follows.

```csharp
if (condition)
{
    DoSomething();
}

DoSomethingElse();
```

### Type member order and names

Within each type, use this order:

1. Nested types (inner classes, records, structs, enums, and interfaces).
2. Constants.
3. Readonly fields.
4. Remaining private fields, then any other mutable fields.
5. Properties (including getter/setter properties) and events.
6. Constructors.
7. Methods.

Keep members in their existing relative order within each group unless another change requires reordering. Preserve field-initializer dependencies and runtime behavior when moving declarations; do not reorder initializers unsafely just to meet the grouping rule.

Constants, including local constants, must use `UPPER_CASE` with underscores between words. Keep other names consistent with the surrounding code: PascalCase for types, properties, and methods; camelCase for parameters and locals; preserve the existing private-field prefix convention. Update references when renaming constants.

### Required post-edit workflow

Agents may write intermediate code in any format, but formatting is **mandatory after all functional edits are complete**, before final validation and the final response:

1. Run the full-solution repository formatter from the repository root after all edits are complete. Do not restrict it with `--include` or enumerate edited files:

   ```bash
   dotnet format HyprNetShell.slnx --no-restore --exclude-diagnostics IDE1006
   ```

   Running this full-solution command after the last edit is required; an editor's Format File action or a file-scoped formatting command is not a substitute. Use the full `dotnet format`, not just `dotnet format whitespace`, so missing-brace and other supported analyzer fixes can run too. Exclude `IDE1006` because its naming code-fix provider does not support solution-wide Fix All; this does not disable the naming rule. Manually rename noncompliant constants in edited files to `UPPER_CASE` and update all references.
2. Manually finish rules the built-in formatter cannot reliably enforce: member grouping, `UPPER_CASE` word boundaries, inline-brace exceptions, and exactly one blank line after closing braces. `.editorconfig` cannot express member ordering, and the experimental block-spacing option is not a complete closing-brace-spacing check. If the formatter expands a permitted inline initializer or empty block, leaving it expanded is valid.
3. For edited native C/header files, use `clang-format` on only those files with Allman braces, four-space indentation, and single-line nonempty blocks/control bodies disabled, then manually check the same applicable block-spacing and naming rules. For other file types, use their existing formatter when available and check `.editorconfig` whitespace rules; do not introduce a dependency just for formatting.
4. Review the final diff for unintended changes and rerun the relevant build/validation. If any subsequent edits are made, repeat the full-solution formatting command before final validation and the final response.
5. Report which formatting command ran. If a formatter is unavailable or fails, do not claim formatting passed: perform the manual rule check and explicitly report the limitation.

## Build and validation

Use the project script to build native code before managed code:

```bash
./build.sh build
```

The native and managed sides can be built independently:

```bash
./build.sh native
./build.sh managed
```

NativeAOT validation:

```bash
./build.sh publish
```

There is currently no automated test project. At minimum:

- managed-only changes: run `./build.sh managed`;
- native-only changes: run `./build.sh native`, then rebuild the executable;
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
- Were all added/edited source files formatted after the last edits, manually checked for rules the formatter cannot enforce, and the formatting result reported?
- Were the most specific available build/validation commands run?
