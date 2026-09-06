# Copyright 1999-2026 Gentoo Authors
# Distributed under the terms of the GNU General Public License v2

EAPI=8

inherit pam

DESCRIPTION="NativeAOT Hyprland shell and status bar"
HOMEPAGE="https://github.com/TheFikusF/HyprNetShell"
SRC_URI="https://github.com/TheFikusF/HyprNetShell/releases/download/v${PV}/HyprNetShell-${PV}-linux-x64.tar.xz"
S="${WORKDIR}/HyprNetShell-${PV}-linux-x64"

LICENSE="all-rights-reserved"
SLOT="0"
KEYWORDS="~amd64"
IUSE="+audio +bluetooth +clipboard +network +ocr +power-profiles +wallpaper"

RDEPEND="
	app-arch/bzip2
	dev-libs/expat
	dev-libs/glib:2
	dev-libs/wayland
	media-libs/fontconfig:1.0
	media-libs/freetype:2
	media-libs/libpng
	media-libs/mesa[egl(+)]
	net-misc/socat
	sys-apps/dbus
	sys-libs/pam
	sys-libs/zlib
	x11-libs/libxkbcommon
	gui-wm/hyprland
	audio? (
		media-video/pipewire
		media-video/wireplumber:0/0.5
	)
	bluetooth? ( net-wireless/bluez )
	clipboard? ( gui-apps/wl-clipboard )
	network? ( net-misc/networkmanager )
	ocr? ( app-text/tesseract )
	power-profiles? ( sys-power/power-profiles-daemon )
	wallpaper? (
		gui-apps/hyprpaper
		gui-apps/hyprsunset
	)
"

RESTRICT="strip"
QA_PREBUILT="usr/libexec/hyprnetshell/*"

src_install() {
	local appdir="/usr/libexec/hyprnetshell"

	[[ -x HyprNetShell ]] || die "NativeAOT executable is missing from the release archive"

	exeinto "${appdir}"
	doexe HyprNetShell

	local library
	for library in ./*.so ; do
		[[ -e ${library} ]] || continue
		if [[ ${library##*/} == libhypr_audio.so ]] && ! use audio ; then
			continue
		fi
		doexe "${library}"
	done

	dosym -r "${appdir}/HyprNetShell" /usr/bin/hyprnetshell
	pamd_mimic_system hyprnetshell auth account
}

pkg_postinst() {
	einfo "Start HyprNetShell from your hyprland.lua startup callback with:"
	einfo "  hl.exec_cmd(\"hyprnetshell\")"
	einfo
	einfo "The ebuild installed /etc/pam.d/hyprnetshell for the in-house lock screen."
	einfo "Remove old Print/CTRL+Print/SHIFT+Print binds before using the built-in screenshot bindings."
}
