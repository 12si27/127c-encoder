#!/usr/bin/env bash
set -euo pipefail

# Run natively on the macOS architecture used by the release job.
destination="${1:?output directory required}"
export MACOSX_DEPLOYMENT_TARGET=12.0
workdir="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/127c-fdkaac-${RANDOM}"
mkdir -p "$destination" "$workdir"
trap 'rm -rf "$workdir"' EXIT

git clone --depth 1 --branch v2.0.3 https://github.com/mstorsjo/fdk-aac.git "$workdir/fdk-aac"
git clone --depth 1 --branch v1.0.3 https://github.com/nu774/fdkaac.git "$workdir/fdkaac"

pushd "$workdir/fdk-aac" >/dev/null
autoreconf -fiv
./configure --prefix="$workdir/fdk-install" --enable-static --disable-shared
make -j"$(sysctl -n hw.ncpu)"
make install
popd >/dev/null

pushd "$workdir/fdkaac" >/dev/null
autoreconf -fiv
CPPFLAGS="-I$workdir/fdk-install/include" \
LDFLAGS="-L$workdir/fdk-install/lib" \
LIBS="-lc++" \
  ./configure
make -j"$(sysctl -n hw.ncpu)"
cp fdkaac "$destination/fdkaac"
popd >/dev/null

cp "$workdir/fdk-aac/NOTICE" "$destination/FDK-AAC-NOTICE"
chmod 755 "$destination/fdkaac"
help_output="$("$destination/fdkaac" --help 2>&1 || true)"
grep -qi fdkaac <<< "$help_output"

# The dependency must be self-contained; a Homebrew dylib will not exist on users' Macs.
if otool -L "$destination/fdkaac" | grep -E '/(opt/homebrew|usr/local)/|libfdk-aac'; then
  echo 'fdkaac unexpectedly depends on a non-system shared library' >&2
  exit 1
fi
