#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   ./scripts/deploy/deploy-ios-device.sh [DeviceName]
# Example:
#   ./scripts/deploy/deploy-ios-device.sh AndrewsiPhone
#
# Builds Debug ios-arm64, installs with devicectl, and launches without attaching
# a console (dotnet -t:Run --console + wait-for-exit often drops CoreDevice and
# falsely fails after a successful install).

DEVICE_NAME="${1:-AndrewsiPhone}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"
TFM="net10.0-ios"
RID="ios-arm64"
CONFIG="Debug"
BUNDLE_ID="com.andrewestherhuysen.turftime"

cd "$REPO_ROOT"

echo "=== TurfTime iOS device deploy ==="
echo "Repo:    $REPO_ROOT"
echo "Project: $PROJECT_FILE"
echo "Device:  $DEVICE_NAME"
echo "Config:  $CONFIG | $TFM | $RID"
echo

if [[ ! -f "$PROJECT_FILE" ]]; then
  echo "ERROR: Project not found: $PROJECT_FILE" >&2
  exit 1
fi

# Prefer device name; fall back to first available paired iPhone if needed.
if ! xcrun devicectl list devices 2>/dev/null | grep -q "$DEVICE_NAME"; then
  echo "WARNING: Device '$DEVICE_NAME' not listed. Available devices:" >&2
  xcrun devicectl list devices 2>&1 || true
fi

echo "Restoring..."
dotnet restore "$PROJECT_FILE"

echo "Building for device..."
dotnet build "$PROJECT_FILE" \
  -f "$TFM" \
  -c "$CONFIG" \
  -p:RuntimeIdentifier="$RID"

# Prefer device-builds .app (installable bundle); fall back to any newest .app under RID.
APP=""
SEARCH_ROOT="$REPO_ROOT/bin/$CONFIG/$TFM/$RID"
if [[ -d "$SEARCH_ROOT" ]]; then
  # macOS-safe: handle spaces in paths (e.g. "UTM Shared"); avoid xargs.
  # Prefer device-builds/*/*.app, then any other .app under the RID output.
  APP="$(find "$SEARCH_ROOT/device-builds" -type d -name '*.app' 2>/dev/null \
    | while IFS= read -r p; do printf '%s\t%s\n' "$(stat -f '%m' "$p" 2>/dev/null || echo 0)" "$p"; done \
    | sort -nr | head -1 | cut -f2- || true)"
  if [[ -z "$APP" || ! -d "$APP" ]]; then
    APP="$(find "$SEARCH_ROOT" -type d -name '*.app' 2>/dev/null \
      | while IFS= read -r p; do printf '%s\t%s\n' "$(stat -f '%m' "$p" 2>/dev/null || echo 0)" "$p"; done \
      | sort -nr | head -1 | cut -f2- || true)"
  fi
fi

if [[ -z "$APP" || ! -d "$APP" ]]; then
  echo "ERROR: Build succeeded but no .app was found under $SEARCH_ROOT" >&2
  find "$SEARCH_ROOT" -type d -name '*.app' 2>/dev/null | head -20 >&2 || true
  exit 1
fi

BID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Info.plist" 2>/dev/null || echo "$BUNDLE_ID")"
VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Info.plist" 2>/dev/null || true)"
BUILD="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$APP/Info.plist" 2>/dev/null || true)"
echo "App:     $APP"
echo "Bundle:  $BID"
echo "Version: ${VER:-unknown} (${BUILD:-unknown})"
echo

echo "Installing on $DEVICE_NAME via devicectl ..."
if ! xcrun devicectl device install app --device "$DEVICE_NAME" "$APP"; then
  echo "devicectl install by name failed; retrying with mlaunch install only..." >&2
  MLAUNCH="$(ls -d /usr/local/share/dotnet/packs/Microsoft.iOS.Sdk*/**/tools/bin/mlaunch 2>/dev/null | sort | tail -1 || true)"
  if [[ -z "$MLAUNCH" ]]; then
    MLAUNCH="$(find /usr/local/share/dotnet/packs -name mlaunch -type f 2>/dev/null | sort | tail -1 || true)"
  fi
  if [[ -n "$MLAUNCH" ]]; then
    "$MLAUNCH" --installdev "$APP" --devname "$DEVICE_NAME"
  else
    echo "ERROR: Could not install app (devicectl failed and mlaunch not found)." >&2
    exit 1
  fi
fi

echo "Launching $BID (no console attach) ..."
if ! xcrun devicectl device process launch --terminate-existing --device "$DEVICE_NAME" "$BID"; then
  echo "WARNING: Launch via devicectl failed; app may still be installed. Open it on the device." >&2
  # Don't fail hard if install succeeded — launch can flake when the phone is locked.
fi

echo "Done. TurfTime2 Debug $VER ($BUILD) deployed to iOS device: $DEVICE_NAME"
