# 83. Packaging, file association and updates

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

PRD 8 and D11 choose Velopack for installers on Windows 10+ (x64, arm64), macOS 13+ (x64, arm64) and
Linux (x64). M8 asks for a Windows Setup.exe, a macOS .app in a .dmg, a Linux AppImage and .deb, the
`.keel` extension registered (D12), installers under 80 MB (PRD 11), a release workflow on version
tags, and an update check that is off by default (PRD 10).

## Decision

- **Scripts**: `build/package.sh` (Linux, macOS; Windows by cross-packing) and `build/package.ps1`
  (Windows). Each RID is published self-contained (no trimming, no ReadyToRun: Avalonia and EF Core
  use reflection) and packed with `vpk` 1.2.158 (a local tool in `dotnet-tools.json`, same version
  as the Velopack package). The version is read from `Directory.Build.props` with
  `dotnet msbuild -getProperty:Version`. Each RID is its own Velopack channel (`win-x64`,
  `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`), so one GitHub release holds every feed.
- **Windows**: `Keel-<rid>-Setup.exe` and a portable zip, desktop and Start menu shortcuts. The
  `.keel` type is registered per user (HKCU, no elevation) from Velopack's install and update hooks
  and removed on uninstall (`Platform/Windows/WindowsFileAssociation`); a double-click starts Keel
  with the path, and a running Keel receives it through the single-instance pipe (ADR 0080).
- **macOS**: the script builds `Keel.app` itself (Info.plist with bundle id `com.keel.app`, the
  `.keel` document type as the exported UTI `app.keel.budget`, icon, minimum macOS 13), gives it to
  `vpk pack` (which produces the .pkg installer, the portable zip and the update packages, and signs
  and notarizes when identities are provided), and wraps `Keel.app` in a `.dmg` with `hdiutil`,
  because vpk does not make disk images. Double-clicked files arrive as an activation event.
- **Linux**: vpk makes the AppImage (self-updating). vpk has no .deb output, so the script builds the
  .deb with `dpkg-deb`: files in `/opt/keel`, `/usr/bin/keel`, a desktop entry, hicolor icons and a
  shared-mime-info type `application/vnd.keel.budget` for `*.keel`; dependencies are the native
  libraries Avalonia and .NET use (X11, fontconfig, ICU, OpenSSL). The .deb does not self-update;
  apt users update by installing the next .deb.
- **Icons** come from one vector source (`Assets/keel-icon.svg`); `build/icons/generate-icons.cs`
  (a .NET file-based program with SkiaSharp) writes the PNG sizes, `keel.ico` and `keel.icns`, which
  are checked in so packaging needs no image tools.
- **Signing is optional**: `KEEL_WIN_SIGN_TEMPLATE`, `KEEL_MAC_APP_IDENTITY`,
  `KEEL_MAC_INSTALL_IDENTITY`, `KEEL_MAC_NOTARY_PROFILE` (secrets in the release workflow). Without
  them packages are unsigned, and the user guide explains the first-launch warnings.
- **Release workflow** (`.github/workflows/release.yml`) runs on `v*` tags: it checks the tag equals
  the version, runs the tests, packages on windows-latest, macos-latest and ubuntu-latest, and
  publishes all packages in one GitHub release (a prerelease when the version has a suffix). CI runs
  the packaging script as a dry run on every push.
- **Updates** use Velopack's `UpdateManager` with the GitHub releases of this repository
  (prereleases included). The check is off by default (PRD 10) and switched on in Settings →
  Updates; with it off, no update source is created and nothing touches the network. A development
  build (not installed by Velopack) says so instead of checking.

## Consequences

- Sizes measured on Linux for 1.0.0-rc.1: Windows x64 Setup.exe 65.7 MB, Linux AppImage 56.5 MB,
  .deb 37.4 MB, all under the 80 MB target.
- The macOS path (vpk with a prebuilt .app, `--bundleId`, signing flags, `hdiutil`) and the Windows
  registry code can only be exercised on those systems; they run in the release workflow.
- osx-x64 is packed on the arm64 macOS runner (cross-published); the Intel .app is not launched in CI.
