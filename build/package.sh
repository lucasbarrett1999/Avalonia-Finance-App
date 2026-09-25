#!/usr/bin/env bash
# Builds Keel's installers with Velopack (PRD 8, ADR 0083).
#
#   build/package.sh [--rid RID]... [--out DIR] [--skip-publish] [--no-deb] [--dry-run]
#
# RIDs: linux-x64, osx-x64, osx-arm64, win-x64, win-arm64. Without --rid the script builds what the
# current OS can build: Linux -> linux-x64 (AppImage + .deb); macOS -> osx-arm64 and osx-x64 (.pkg,
# portable zip, and a .dmg holding Keel.app). Windows packages are built by build/package.ps1 on Windows;
# from Linux or macOS `--rid win-x64` cross-packs them with vpk's [win] directive (unsigned).
#
# Every RID is published self-contained, then `vpk pack` makes the installer and update packages under
# <out>/releases/<rid>. The version comes from Directory.Build.props (VersionPrefix/VersionSuffix).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Keel.Desktop/Keel.Desktop.csproj"
ASSETS="$ROOT/src/Keel.Desktop/Assets"
ICONS="$ROOT/build/icons/out"
OUT="$ROOT/artifacts"
PACK_ID="Keel"
TITLE="Keel"
AUTHORS="Keel contributors"
MAIN_EXE="Keel.Desktop"
BUNDLE_ID="com.keel.app"
RIDS=()
SKIP_PUBLISH=0
NO_DEB=0
DRY_RUN=0

usage() { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RIDS+=("$2"); shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    --no-deb) NO_DEB=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

OS="$(uname -s)"
if [[ ${#RIDS[@]} -eq 0 ]]; then
  case "$OS" in
    Linux) RIDS=(linux-x64) ;;
    Darwin) RIDS=(osx-arm64 osx-x64) ;;
    *) echo "On Windows use build/package.ps1" >&2; exit 2 ;;
  esac
fi

run() {
  echo "+ $*"
  if [[ $DRY_RUN -eq 0 ]]; then "$@"; fi
}

cd "$ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '[:space:]')"
if [[ -z "$VERSION" ]]; then
  echo "Could not read the version from Directory.Build.props" >&2
  exit 1
fi
echo "Keel $VERSION: ${RIDS[*]}"
run dotnet tool restore

publish() {
  local rid="$1" dir="$OUT/publish/$1"
  if [[ $SKIP_PUBLISH -eq 1 && -d "$dir" ]]; then return; fi
  run rm -rf "$dir"
  run dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:PublishReadyToRun=false -o "$dir"
}

# A Debian package for apt-based distributions (Ubuntu 22.04+). It installs to /opt/keel, registers the
# .keel MIME type and the desktop entry; it does not self-update (the AppImage does).
make_deb() {
  local rid="$1" arch
  case "$rid" in
    linux-x64) arch=amd64 ;;
    linux-arm64) arch=arm64 ;;
    *) return ;;
  esac
  local debver="${VERSION/-/\~}" stage="$OUT/deb/$rid" deb="$OUT/releases/$rid/keel_${VERSION/-/\~}_${arch}.deb"
  run rm -rf "$stage"
  run mkdir -p "$stage/DEBIAN" "$stage/opt/keel" "$stage/usr/bin" "$stage/usr/share/applications" \
    "$stage/usr/share/mime/packages"
  run cp -a "$OUT/publish/$rid/." "$stage/opt/keel/"
  run ln -sf /opt/keel/$MAIN_EXE "$stage/usr/bin/keel"
  for size in 16 24 32 48 64 128 256 512; do
    run mkdir -p "$stage/usr/share/icons/hicolor/${size}x${size}/apps" "$stage/usr/share/icons/hicolor/${size}x${size}/mimetypes"
    run cp "$ICONS/keel-$size.png" "$stage/usr/share/icons/hicolor/${size}x${size}/apps/keel.png"
    run cp "$ICONS/keel-$size.png" "$stage/usr/share/icons/hicolor/${size}x${size}/mimetypes/application-vnd.keel.budget.png"
  done
  if [[ $DRY_RUN -eq 1 ]]; then echo "+ write DEBIAN/control, postinst, keel.desktop, keel.xml"; echo "+ dpkg-deb --root-owner-group --build $stage $deb"; return; fi

  local size_kb
  size_kb="$(du -sk "$stage/opt" | cut -f1)"
  cat > "$stage/DEBIAN/control" <<EOF
Package: keel
Version: $debver
Section: utils
Priority: optional
Architecture: $arch
Installed-Size: $size_kb
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libssl3 | libssl1.1, libicu76 | libicu74 | libicu72 | libicu70 | libicu67
Maintainer: $AUTHORS <noreply@keel.invalid>
Homepage: https://github.com/lucasbarrett1999/Avalonia-Finance-App
Description: Local-first envelope budgeting and net worth
 Keel is a desktop personal-finance app: envelope budgeting, net worth,
 recurring bills and forecasts, on one budget file you own. It works
 offline; bank sync is optional and uses your own provider keys.
EOF
  cat > "$stage/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-mime-database >/dev/null 2>&1 && update-mime-database /usr/share/mime || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
EOF
  cp "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm"
  chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm"
  cat > "$stage/usr/share/applications/keel.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Keel
GenericName=Personal finance
Comment=Envelope budgeting and net worth on a file you own
Exec=/opt/keel/$MAIN_EXE %f
Icon=keel
Terminal=false
Categories=Office;Finance;
MimeType=application/vnd.keel.budget;
StartupWMClass=Keel
EOF
  cat > "$stage/usr/share/mime/packages/keel.xml" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/vnd.keel.budget">
    <comment>Keel budget file</comment>
    <sub-class-of type="application/vnd.sqlite3"/>
    <glob pattern="*.keel"/>
    <icon name="application-vnd.keel.budget"/>
  </mime-type>
</mime-info>
EOF
  mkdir -p "$(dirname "$deb")"
  dpkg-deb --root-owner-group -Zxz --build "$stage" "$deb"
  echo "Built $deb"
}

# macOS: Keel.app with an Info.plist that declares the .keel document type (double-click opens it),
# packed by vpk (it signs and notarizes when the CI provides identities), then a .dmg around Keel.app.
make_app() {
  local rid="$1" app="$OUT/app/$1/Keel.app"
  run rm -rf "$app"
  run mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  run cp -a "$OUT/publish/$rid/." "$app/Contents/MacOS/"
  run cp "$ASSETS/keel.icns" "$app/Contents/Resources/keel.icns"
  if [[ $DRY_RUN -eq 1 ]]; then echo "+ write $app/Contents/Info.plist"; return; fi
  cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$TITLE</string>
  <key>CFBundleDisplayName</key><string>$TITLE</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>$MAIN_EXE</string>
  <key>CFBundleIconFile</key><string>keel.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION%%-*}</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) Keel contributors</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Keel budget file</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>CFBundleTypeIconFile</key><string>keel.icns</string>
      <key>LSItemContentTypes</key><array><string>app.keel.budget</string></array>
    </dict>
  </array>
  <key>UTExportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeIdentifier</key><string>app.keel.budget</string>
      <key>UTTypeDescription</key><string>Keel budget file</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string><string>public.database</string></array>
      <key>UTTypeIconFile</key><string>keel.icns</string>
      <key>UTTypeTagSpecification</key>
      <dict><key>public.filename-extension</key><array><string>keel</string></array></dict>
    </dict>
  </array>
</dict>
</plist>
EOF
}

make_dmg() {
  local rid="$1" app="$OUT/app/$1/Keel.app" dmg="$OUT/releases/$1/Keel-$VERSION-$1.dmg" stage="$OUT/dmg/$1"
  if ! command -v hdiutil >/dev/null 2>&1; then
    echo "hdiutil not found: the .dmg is only built on macOS" >&2
    return
  fi
  run rm -rf "$stage"
  run mkdir -p "$stage"
  run cp -R "$app" "$stage/"
  run ln -s /Applications "$stage/Applications"
  run hdiutil create -volname "$TITLE" -srcfolder "$stage" -ov -format UDZO "$dmg"
}

for rid in "${RIDS[@]}"; do
  releases="$OUT/releases/$rid"
  run mkdir -p "$releases"
  case "$rid" in
    linux-*)
      [[ "$OS" == Linux ]] || { echo "Linux packages are built on Linux" >&2; exit 2; }
      publish "$rid"
      run dotnet tool run vpk pack --packId "$PACK_ID" --packVersion "$VERSION" --packDir "$OUT/publish/$rid" \
        --mainExe "$MAIN_EXE" --packTitle "$TITLE" --packAuthors "$AUTHORS" --icon "$ICONS/keel.png" \
        --categories "Office;Finance" --channel "$rid" --runtime "$rid" --outputDir "$releases"
      [[ $NO_DEB -eq 1 ]] || make_deb "$rid"
      ;;
    osx-*)
      [[ "$OS" == Darwin ]] || { echo "macOS packages are built on macOS (codesign, pkgbuild, hdiutil)" >&2; exit 2; }
      publish "$rid"
      make_app "$rid"
      signing=()
      if [[ -n "${KEEL_MAC_APP_IDENTITY:-}" ]]; then signing+=(--signAppIdentity "$KEEL_MAC_APP_IDENTITY"); fi
      if [[ -n "${KEEL_MAC_INSTALL_IDENTITY:-}" ]]; then signing+=(--signInstallIdentity "$KEEL_MAC_INSTALL_IDENTITY"); fi
      if [[ -n "${KEEL_MAC_NOTARY_PROFILE:-}" ]]; then signing+=(--notaryProfile "$KEEL_MAC_NOTARY_PROFILE"); fi
      run dotnet tool run vpk pack --packId "$PACK_ID" --packVersion "$VERSION" --packDir "$OUT/app/$rid/Keel.app" \
        --mainExe "$MAIN_EXE" --packTitle "$TITLE" --packAuthors "$AUTHORS" --icon "$ASSETS/keel.icns" \
        --bundleId "$BUNDLE_ID" --channel "$rid" --runtime "$rid" --outputDir "$releases" "${signing[@]}"
      make_dmg "$rid"
      ;;
    win-*)
      publish "$rid"
      directive=()
      [[ "$OS" == MINGW* || "$OS" == MSYS* || "$OS" == CYGWIN* ]] || directive=("[win]")
      run dotnet tool run vpk "${directive[@]}" pack --packId "$PACK_ID" --packVersion "$VERSION" --packDir "$OUT/publish/$rid" \
        --mainExe "$MAIN_EXE.exe" --packTitle "$TITLE" --packAuthors "$AUTHORS" --icon "$ASSETS/keel.ico" \
        --channel "$rid" --runtime "$rid" --outputDir "$releases"
      ;;
    *) echo "Unsupported RID: $rid" >&2; exit 2 ;;
  esac
done

echo "Packages:"
if [[ $DRY_RUN -eq 0 ]]; then find "$OUT/releases" -maxdepth 2 -type f -printf '  %p (%s bytes)\n' 2>/dev/null || ls -lR "$OUT/releases"; fi
