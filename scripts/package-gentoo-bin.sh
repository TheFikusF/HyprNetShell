#!/bin/sh
set -eu

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
    echo "Usage: $0 VERSION [PUBLISH_DIRECTORY] [OUTPUT_DIRECTORY]" >&2
    exit 2
fi

version=$1
publish_directory=${2:-bin/Release/net10.0/linux-x64/publish}
output_directory=${3:-dist}
archive_name="HyprNetShell-${version}-linux-x64.tar.xz"
package_directory="HyprNetShell-${version}-linux-x64"

if [ ! -x "${publish_directory}/HyprNetShell" ]; then
    echo "Missing NativeAOT executable: ${publish_directory}/HyprNetShell" >&2
    echo "Publish the project before creating the binary archive." >&2
    exit 1
fi

mkdir -p "${output_directory}"
output_directory=$(CDPATH= cd -- "${output_directory}" && pwd)
staging_root=$(mktemp -d "${TMPDIR:-/tmp}/hyprnetshell-package.XXXXXX")
staging_directory="${staging_root}/${package_directory}"
trap 'rm -rf "${staging_root}"' EXIT HUP INT TERM

mkdir -p "${staging_directory}"
cp -a "${publish_directory}/." "${staging_directory}/"
find "${staging_directory}" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.dbg' -o -name '*.tar.xz' \) -delete

tar -C "${staging_root}" -cJf "${output_directory}/${archive_name}" "${package_directory}"

printf 'Created %s\n' "${output_directory}/${archive_name}"
