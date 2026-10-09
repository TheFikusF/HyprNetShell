<img width="1920" height="69" alt="HyprNetShell status bar" src="https://github.com/user-attachments/assets/96eabc48-c185-4ef3-b153-fb05bbcba1c5" />

# HyprNetShell

An experimental Linux status bar and desktop shell for **Hyprland**, written in C# and C. It uses a small native Wayland layer-shell library, a custom layout/input system, and direct OpenGL rendering—no GTK, Qt, Avalonia, SDL, or GLFW.

Features include workspaces, system and media controls, notifications, a system tray, an application launcher, clipboard history, wallpapers, screenshots, and a lock screen.

This is a work in progress, not a polished drop-in desktop. Hyprland is the intended environment; other layer-shell compositors may support the bar, but Hyprland-specific features will not work there.

## Build and run

You need:

- Linux x86-64 and a running Hyprland session;
- .NET 10 SDK, a C11 compiler, `pkg-config`, Meson, and Ninja;
- development files for Wayland client/EGL, EGL/OpenGL, xkbcommon, and PAM, plus `wayland-scanner`;
- GLib/GIO runtime libraries. WirePlumber 0.5 development files are optional for the native audio integration.

Wayland protocol XML files are vendored; no separate `wlr-protocols` package is needed.

From the repository root:

```bash
./build.sh run       # Build native + managed code, then launch
./build.sh build     # Build only
./build.sh publish   # Build native + static libraries, then publish NativeAOT
```

Run inside your Wayland session. `./build.sh help` lists all commands; `native` and `managed` can build each side separately. A managed-only build still needs the native library built first.

### NativeAOT static libraries

NativeAOT publishes statically link our Wayland and audio libraries, SQLite, SkiaSharp, and HarfBuzzSharp into the executable. Regular managed builds continue using shared libraries.

Publishing additionally requires Python 3.12 or newer, Clang/Clang++, `ar`, Fontconfig development files, and WirePlumber 0.5 development files (mandatory for AOT). `scripts/build-static-libraries.py` downloads version-pinned SQLite, SkiaSharp, and HarfBuzz sources and Skia's pinned image/font dependencies into the ignored `Native/build/` directory. If GN is not installed, it downloads a GN binary there. The first publish needs network access and can take several minutes; later builds reuse those downloads and incremental native outputs. To prepare the archives separately or limit compilation parallelism:

```bash
./build.sh static --jobs 2
```

The source versions match SkiaSharp 4.151.1, HarfBuzzSharp 14.2.0, and SQLitePCLRaw 2.1.12 (SQLite 3.53.3). HarfBuzz builds without optional system integrations and verifies every managed native import against the archive. Use `./build.sh static --harfbuzz-only` to build just that archive. Update the script's source pins/checksums together with those package versions.

Runtime integrations use tools such as `hyprctl`, `socat`, `wpctl`, `nmcli`, `bluetoothctl`, `wl-copy`/`wl-paste`, `hyprpaper`, and `hyprsunset`. Install the tools for the features you want. Media controls use MPRIS over the session D-Bus.

**Gentoo:** see [`packaging/gentoo/`](packaging/gentoo/README.md) for the binary ebuild, overlay setup, runtime dependencies, and lock-screen PAM policy.

## Optional online accounts

**Settings → Accounts** supports Google, Spotify, and ChatGPT sign-in. Credentials are stored through a Freedesktop Secret Service provider such as GNOME Keyring, KDE Wallet, or KeePassXC—not plaintext files.

Client IDs can be entered in Settings or supplied through `HYPRNETSHELL_GOOGLE_CLIENT_ID`, `HYPRNETSHELL_SPOTIFY_CLIENT_ID`, and `HYPRNETSHELL_OPENAI_CLIENT_ID`. Embedded build values take priority over environment variables, then saved settings.

- **Google:** create a **Desktop app** OAuth client. Also supply `HYPRNETSHELL_GOOGLE_CLIENT_SECRET` from its credential JSON; this value is not saved in settings.
- **Spotify:** use Authorization Code with PKCE and register `http://127.0.0.1:5543/auth/callback` exactly. No client secret is needed.
- **ChatGPT:** uses OpenAI's first-party Codex OAuth flow, not a stable third-party API; upstream changes may break it. No client secret is needed.

`build.sh` embeds supplied OAuth values from the environment or the local, gitignored `credentials.sh` during managed builds and publishing. Do not put private credentials in builds you share.

## Code layout

`Application/` handles startup and shell orchestration; `Core/` contains features and bar modules; `GUI/` provides layout and controls; `Rendering/` owns OpenGL drawing; `Native/` handles Wayland and interop; `Generators/` embeds SVG assets.

See [`AGENTS.md`](AGENTS.md) for architecture, development conventions, and validation commands.

## License

HyprNetShell's original code is licensed under the [MIT License](LICENSE).

Embedded license notices are available in **Settings → Info → Licenses**.

Third-party dependencies and bundled assets retain their upstream licenses. This includes [Lucide icons](assets/icons/lucide/LICENSE), fonts under `assets/fonts/`, and Wayland protocol definitions under `Native/protocols/` (which include their license notices). The MIT License does not replace those licenses or their attribution requirements.
