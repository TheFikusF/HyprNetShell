# Gentoo binary package

This directory is a local Portage overlay containing `gui-apps/hyprnetshell-bin`.
The ebuild installs the published NativeAOT executable and its bundled native libraries; it does not build .NET or C sources.

## 1. Create the binary archive

The NativeAOT publish profile creates the archive automatically:

```bash
dotnet publish HyprNetShell.csproj \
  -p:PublishProfile=Properties/PublishProfiles/NativeAotOneFile.pubxml
```

The generated archive is:

```text
bin/Release/net10.0/linux-x64/publish/HyprNetShell-0.1.0-linux-x64.tar.xz
```

To package an already-existing publish directory manually instead, run:

```bash
./scripts/package-gentoo-bin.sh 0.1.0
```

The script's default output archive is:

```text
dist/HyprNetShell-0.1.0-linux-x64.tar.xz
```

A different publish or output directory may be supplied as the second and third arguments.

## 2. Make the archive available to Portage

For a local installation, copy the exact archive into the configured Portage distfiles directory and generate its Manifest:

```bash
sudo cp bin/Release/net10.0/linux-x64/publish/HyprNetShell-0.1.0-linux-x64.tar.xz /var/cache/distfiles/
ebuild --force packaging/gentoo/gui-apps/hyprnetshell-bin/hyprnetshell-bin-0.1.0.ebuild manifest
```

For distribution, upload the same archive to GitHub release `v0.1.0`; the ebuild's `SRC_URI` already points there.

## 3. Enable the local overlay

Create `/etc/portage/repos.conf/hyprnetshell.conf`, replacing the location with the absolute path to this checkout:

```ini
[hyprnetshell]
location = /home/USER/path/to/HyprNetShell/packaging/gentoo
masters = gentoo
auto-sync = no
```

The package also depends on `gui-wm/hyprland`, `gui-apps/hyprpaper`, and `gui-apps/hyprsunset`. Enable `hyproverlay` if those packages are not supplied by another configured repository.

## 4. Install

The package is initially keyworded `~amd64`. Accept that keyword locally:

```bash
echo '=gui-apps/hyprnetshell-bin-0.1.0 ~amd64' | sudo tee /etc/portage/package.accept_keywords/hyprnetshell
```

Then install it:

```bash
sudo emerge --ask gui-apps/hyprnetshell-bin
```

All feature USE flags are enabled by default. Disable integrations you do not need in `/etc/portage/package.use/hyprnetshell`, for example:

```text
gui-apps/hyprnetshell-bin -ocr -wallpaper
```

The installation places:

- the application under `/usr/libexec/hyprnetshell/`;
- a `/usr/bin/hyprnetshell` symlink;
- a Gentoo-native PAM policy at `/etc/pam.d/hyprnetshell`.

## 5. Start from Hyprland

Add this to `~/.config/hypr/hyprland.lua` and remove any old screenshot bindings that use the same keys:

```lua
hl.on("hyprland.start", function()
    hl.exec_cmd("hyprnetshell")
end)
```

Restart the Hyprland session, or run `hyprnetshell` from a terminal inside the session for the first smoke test.
