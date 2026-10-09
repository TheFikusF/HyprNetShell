#!/usr/bin/env python3
"""Build version-pinned Linux x64 SQLite, SkiaSharp and HarfBuzzSharp archives for NativeAOT."""

import argparse
import ast
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "Native" / "build"
SKIA_REVISION = "bdd0c3a8eaba1afa7148f02bba3a07f94e682847"  # SkiaSharp v4.151.1
SKIA_SHA256 = "0e58036bbe8395e54c54456914840bf3906d5f4df30be1d74eb01d02903ab476"
SQLITE_VERSION = "3530300"  # SQLitePCLRaw 2.1.12 ships SQLite 3.53.3.
SQLITE_SHA256 = "646421e12aac110282ef8cc68f1a62d4bb15fc7b8f09da0b53e29ee690500431"
# SkiaSharp v4.148.0 scripts/VERSIONS.txt identifies HarfBuzzSharp 14.2.0.
# Its externals/skia (1a155bae3ac86db6d3efbd996f00e774b6a7b722) DEPS pins this source.
HARFBUZZ_REVISION = "b0ffab42d473eb380ad0fcf42730e0f1868cbc97"
HARFBUZZ_SHA256 = "f1b3ded79afb04296a328d1a2049484717321d786a138b56e301b75300b48c1a"
HARFBUZZ_BINDING_REVISION = "4e4ce7af7ea8702593af5aeb25d05c65ffb74e90"  # v4.148.0
HARFBUZZ_BINDING_SHA256 = "61b520c65bec04e3f5fca74b83261eeb2a4944374e0310810792bc5293a8aed3"


def run(*arguments, cwd=ROOT):
    subprocess.run(arguments, cwd=cwd, check=True)


def download(url, destination, digest=None):
    if not destination.exists():
        print(f"Downloading {url}", flush=True)
        temporary = destination.with_suffix(destination.suffix + ".partial")
        with urllib.request.urlopen(url, timeout=120) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        temporary.replace(destination)
    if digest and hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
        raise RuntimeError(f"Checksum mismatch: {destination}; remove it and retry")


def extract_tar(archive, destination):
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive) as source:
        source.extractall(destination, filter="data")


def update_versions(values):
    destination = BUILD / "static-library-versions.json"
    versions = json.loads(destination.read_text()) if destination.exists() else {}
    versions.update(values)
    destination.write_text(json.dumps(versions, indent=2) + "\n")


def build_harfbuzz(jobs):
    archive = BUILD / f"harfbuzz-{HARFBUZZ_REVISION}.tar.gz"
    download(f"https://codeload.github.com/harfbuzz/harfbuzz/tar.gz/{HARFBUZZ_REVISION}",
             archive, HARFBUZZ_SHA256)
    source = BUILD / f"harfbuzz-{HARFBUZZ_REVISION}"
    if not source.exists():
        extract_tar(archive, BUILD)
    binding = BUILD / f"HarfBuzzApi-{HARFBUZZ_BINDING_REVISION}.generated.cs"
    download(f"https://raw.githubusercontent.com/mono/SkiaSharp/{HARFBUZZ_BINDING_REVISION}"
             "/binding/HarfBuzzSharp/HarfBuzzApi.generated.cs", binding, HARFBUZZ_BINDING_SHA256)
    output = source / "out-hyprnetshell-static"
    # Built-in Unicode/OpenType support needs none of FreeType, ICU, GLib or fontconfig.
    # Keep deprecated APIs enabled: the managed binding still imports some of them.
    run("meson", "setup", *(["--reconfigure"] if (output / "meson-private/coredata.dat").exists() else []),
        str(output), str(source), "--wrap-mode=nodownload", "--buildtype=release",
        "-Ddefault_library=static", "-Db_staticpic=true", "-Dauto_features=disabled",
        "-Dsubset=disabled", "-Draster=disabled", "-Dvector=disabled", "-Dgpu=disabled",
        "-Dtests=disabled", "-Dutilities=disabled", "-Dwith_libstdcxx=false")
    run("meson", "compile", "-C", str(output), "-j", str(jobs), "harfbuzz")
    native_archive = output / "src/libharfbuzz.a"
    symbols = subprocess.check_output(["nm", "-g", "--defined-only", str(native_archive)], text=True)
    exports = set(re.findall(r"\b(hb_\w+)\s*$", symbols, re.MULTILINE))
    # Delegate lookups cover every import, including pointer returns with inline comments.
    required = set(re.findall(r'GetSymbol<[^>]+>\s*\("(hb_\w+)"\)', binding.read_text()))
    if not required or required - exports:
        raise RuntimeError(f"HarfBuzzSharp export mismatch: {sorted(required - exports)}; "
                           f"found {len(required)} managed imports")
    shutil.copy2(native_archive, BUILD / "libHarfBuzzSharp.a")
    licenses = BUILD / "static-licenses" / "harfbuzz"
    licenses.mkdir(parents=True, exist_ok=True)
    for notice in source.rglob("*"):
        if notice.is_file() and output not in notice.parents and notice.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")):
            destination = licenses / notice.relative_to(source)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(notice, destination)
    update_versions({"HarfBuzzSharp": "14.2.0", "HarfBuzz": "14.2.0",
                     "harfbuzzRevision": HARFBUZZ_REVISION, "harfbuzzSourceSha256": HARFBUZZ_SHA256,
                     "harfbuzzBindingRevision": HARFBUZZ_BINDING_REVISION,
                     "harfbuzzBindingSha256": HARFBUZZ_BINDING_SHA256,
                     "harfbuzzVerifiedExports": len(required)})
    print(f"Verified {len(required)} HarfBuzzSharp imports in libHarfBuzzSharp.a", flush=True)


def build_sqlite():
    archive = BUILD / "sqlite-source.zip"
    download(f"https://sqlite.org/2026/sqlite-amalgamation-{SQLITE_VERSION}.zip", archive, SQLITE_SHA256)
    source = BUILD / f"sqlite-amalgamation-{SQLITE_VERSION}"
    if not source.exists():
        with zipfile.ZipFile(archive) as zipped:
            zipped.extractall(BUILD)
    output = BUILD / "libe_sqlite3.a"
    licenses = BUILD / "static-licenses" / "sqlite"
    licenses.mkdir(parents=True, exist_ok=True)
    header = (source / "sqlite3.h").read_text().split("*************************************************************************", 1)[0]
    if "The author disclaims copyright" not in header:
        raise RuntimeError("SQLite public-domain notice changed; review the upstream header")
    (licenses / "LICENSE.txt").write_text(
        "SQLite is in the public domain.\nhttps://www.sqlite.org/copyright.html\n\n"
        + header + "*/\n")
    if output.exists() and output.stat().st_mtime >= max((source / "sqlite3.c").stat().st_mtime, Path(__file__).stat().st_mtime):
        return
    run(os.environ.get("CC", "cc"), "-O2", "-fPIC", "-ffunction-sections", "-fdata-sections",
        "-DSQLITE_THREADSAFE=1", "-DSQLITE_ENABLE_FTS5", "-DSQLITE_ENABLE_FTS4",
        "-DSQLITE_ENABLE_RTREE", "-DSQLITE_ENABLE_COLUMN_METADATA", "-DSQLITE_ENABLE_MATH_FUNCTIONS",
        "-DSQLITE_ENABLE_DBSTAT_VTAB",
        "-c", str(source / "sqlite3.c"), "-o", str(BUILD / "sqlite3.o"))
    run("ar", "rcs", str(output), str(BUILD / "sqlite3.o"))


def build_skia(jobs):
    archive = BUILD / "skia-source.tar.gz"
    download(f"https://codeload.github.com/mono/skia/tar.gz/{SKIA_REVISION}", archive, SKIA_SHA256)
    source = BUILD / f"skia-{SKIA_REVISION}"
    if not source.exists():
        extract_tar(archive, BUILD)
    # Include transitive source sets; GN still leaves other static archives separate.
    config = source / "gn" / "BUILDCONFIG.gn"
    text = config.read_text()
    original = 'target(_skiasharp_mode, target_name) {\n    forward_variables_from(invoker, "*")'
    replacement = original + '\n    if (is_static_skiasharp) {\n      complete_static_lib = true\n    }'
    if replacement not in text:
        if original not in text:
            raise RuntimeError("Upstream SkiaSharp static target changed")
        config.write_text(text.replace(original, replacement))
    # Only fetch dependencies used by our CPU image/SVG build, at upstream-pinned revisions.
    tree = ast.parse((source / "DEPS").read_text())
    dependencies = next(node.value for node in tree.body if isinstance(node, ast.Assign)
                        and any(isinstance(target, ast.Name) and target.id == "deps" for target in node.targets))
    required = {"brotli", "expat", "freetype", "libjpeg-turbo", "libpng", "libwebp", "wuffs", "zlib"}
    for key, value in zip(dependencies.keys, dependencies.values):
        if not isinstance(key, ast.Constant) or Path(key.value).name not in required:
            continue
        path = source / key.value
        if path.exists():
            continue
        repo, revision = ast.literal_eval(value).rsplit("@", 1)
        if repo.startswith("https://github.com/"):
            project = repo.removeprefix("https://github.com/").removesuffix(".git")
            url = f"https://codeload.github.com/{project}/tar.gz/{revision}"
        else:
            url = f"{repo}/+archive/{revision}.tar.gz"
        dependency_archive = BUILD / f"{path.name}-{revision}.tar.gz"
        download(url, dependency_archive)
        if repo.startswith("https://github.com/"):
            staging = BUILD / f"extract-{path.name}"
            extract_tar(dependency_archive, staging)
            path.parent.mkdir(parents=True, exist_ok=True)
            next(staging.iterdir()).rename(path)
            staging.rmdir()
        else:
            extract_tar(dependency_archive, path)
    gn = shutil.which("gn")
    if not gn:
        gn_archive = BUILD / "gn.zip"
        download("https://chrome-infra-packages.appspot.com/dl/gn/gn/linux-amd64/+/latest", gn_archive)
        gn_directory = BUILD / "gn-tool"
        if not (gn_directory / "gn").exists():
            with zipfile.ZipFile(gn_archive) as zipped:
                zipped.extractall(gn_directory)
        gn = str(gn_directory / "gn")
        Path(gn).chmod(0o755)
    arguments = ' '.join([
        'is_official_build=true', 'is_static_skiasharp=true', 'target_cpu="x64"',
        'cc="clang"', 'cxx="clang++"', 'skia_enable_ganesh=true', 'skia_enable_graphite=false',
        'skia_use_gl=true', 'skia_use_egl=true', 'skia_use_x11=false',
        'skia_use_vulkan=false', 'skia_use_harfbuzz=false', 'skia_use_icu=false',
        'skia_use_partition_alloc=false', 'skia_use_piex=false', 'skia_enable_skottie=true',
        'skia_use_system_expat=false', 'skia_use_system_freetype2=false',
        'skia_use_system_libjpeg_turbo=false', 'skia_use_system_libpng=false',
        'skia_use_system_libwebp=false', 'skia_use_system_zlib=false',
        'extra_cflags=["-fPIC", "-ffunction-sections", "-fdata-sections", "-DHAVE_SYSCALL_GETRANDOM", "-DXML_DEV_URANDOM", "-DSK_ENABLE_LEGACY_SHADERCONTEXT"]',
    ])
    run(gn, "gen", "out/hyprnetshell-static", f"--args={arguments}", cwd=source)
    run("ninja", "-C", "out/hyprnetshell-static", "-j", str(jobs), "SkiaSharp", cwd=source)
    # MRI ADDLIB merges archive members rather than nesting .a files. This includes
    # implementation targets that GN's complete_static_lib leaves as dependencies.
    output = source / "out/hyprnetshell-static"
    combined = BUILD / "libSkiaSharp.a"
    temporary = BUILD / "libSkiaSharp-combined.a"
    instructions = [f"CREATE {temporary.name}"]
    instructions.extend(f"ADDLIB {archive.relative_to(BUILD)}" for archive in sorted(output.glob("*.a")))
    instructions.extend(["SAVE", "END", ""])
    subprocess.run(["ar", "-M"], input="\n".join(instructions), text=True, check=True, cwd=BUILD)
    temporary.replace(combined)
    licenses = BUILD / "static-licenses"
    for notice in source.rglob("*"):
        if notice.is_file() and notice.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")):
            destination = licenses / "skia" / notice.relative_to(source)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(notice, destination)
    update_versions({"SkiaSharp": "4.151.1", "skiaRevision": SKIA_REVISION, "SQLite": "3.53.3"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 1))
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--sqlite-only", action="store_true")
    selection.add_argument("--harfbuzz-only", action="store_true")
    args = parser.parse_args()
    if args.jobs <= 0:
        parser.error("--jobs must be positive")
    BUILD.mkdir(parents=True, exist_ok=True)
    if not args.harfbuzz_only:
        build_sqlite()
    if not args.sqlite_only:
        if not args.harfbuzz_only:
            build_skia(args.jobs)
        build_harfbuzz(args.jobs)


if __name__ == "__main__":
    main()
