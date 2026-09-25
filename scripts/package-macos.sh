#!/usr/bin/env bash
set -euo pipefail

version="${1:?version required}"
rid="${2:?runtime identifier required}"
publish_dir="${3:?published application directory required}"
fdkaac_dir="${4:?fdkaac build directory required}"
output_dir="${5:?DMG output directory required}"

case "$rid" in osx-x64|osx-arm64) ;; *) echo "Unsupported macOS RID: $rid" >&2; exit 1 ;; esac
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.+-]+)?$ ]] || exit 1
[[ -x "$publish_dir/127c-encoder" && -x "$fdkaac_dir/fdkaac" ]] || exit 1

staging="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/127c-dmg-${rid}-${RANDOM}"
app="$staging/127c-encoder.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/encoder" "$output_dir"
trap 'rm -rf "$staging"' EXIT
cp -R "$publish_dir/." "$app/Contents/MacOS/"
find "$app/Contents/MacOS" -name '*.pdb' -type f -delete
cp "$fdkaac_dir/fdkaac" "$fdkaac_dir/FDK-AAC-NOTICE" "$app/Contents/Resources/encoder/"
chmod 755 "$app/Contents/MacOS/127c-encoder" "$app/Contents/Resources/encoder/fdkaac"

numeric_version="${version%%[-+]*}"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>kr.1227.encoder</string>
  <key>CFBundleName</key><string>127c-encoder</string>
  <key>CFBundleDisplayName</key><string>127c-encoder</string>
  <key>CFBundleExecutable</key><string>127c-encoder</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleVersion</key><string>$numeric_version</string>
  <key>CFBundleShortVersionString</key><string>$numeric_version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
plutil -lint "$app/Contents/Info.plist"

for binary in "$app/Contents/MacOS/127c-encoder" "$app/Contents/Resources/encoder/fdkaac"; do
  minos="$(xcrun vtool -show-build "$binary" | awk '$1 == "minos" { print $2; exit }')"
  if [[ -z "$minos" ]] || ! python3 - "$minos" <<'PY'
import sys
parts = tuple(int(part) for part in sys.argv[1].split('.'))
sys.exit(0 if parts <= (12, 0) else 1)
PY
  then
    echo "Binary requires a newer macOS than 12.0: $binary ($minos)" >&2
    exit 1
  fi
done

# Ad-hoc signing makes the bundle internally consistent. Developer ID
# notarization requires a separate Apple certificate and credentials.
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q 'Mach-O'; then
    codesign --force --sign - --timestamp=none "$binary"
  fi
done < <(find "$app/Contents/MacOS" "$app/Contents/Resources/encoder" -type f -print0)
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ln -s /Applications "$staging/Applications"
# hdiutil's automatic volume estimate can run out of space while copying the
# self-contained app into the image, even when the runner itself has room.
image_size_mb="$(du -sk "$staging" | awk '{ print int(($1 + 1023) / 1024) + 256 }')"
hdiutil create -volname '127c-encoder' -srcfolder "$staging" \
  -size "${image_size_mb}m" -format UDZO \
  "$output_dir/127c-encoder-v${version}-${rid}.dmg"
