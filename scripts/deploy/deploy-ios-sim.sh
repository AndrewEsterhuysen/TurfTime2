#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   ./scripts/deploy/deploy-ios-sim.sh [SimulatorNameOrUDID]
# Examples:
#   ./scripts/deploy/deploy-ios-sim.sh                 # booted sim, else "iPhone 17"
#   ./scripts/deploy/deploy-ios-sim.sh "iPhone 17 Pro"
#   ./scripts/deploy/deploy-ios-sim.sh B9111FA4-3E50-4887-BB38-D941B865389C
#
# Note:
# Combined `dotnet build -t:Run` can fail on a clean tree because no simulator
# .app exists yet. Build first, then install + launch via simctl.
#
# Modern .NET iOS SDK places the installable .app under
# bin/.../iossimulator-arm64/device-builds/<model-os>/TurfTime2.app
# (not at the RID root). We prefer that path, then fall back to any .app.

TARGET="${1:-}"
RID="iossimulator-arm64"
TFM="net10.0-ios"
CONFIG="Debug"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

cd "$REPO_ROOT"

if [[ ! -f "$PROJECT_FILE" ]]; then
  echo "ERROR: Project not found: $PROJECT_FILE" >&2
  exit 1
fi

# --- Resolve a simulator UDID -------------------------------------------------
booted_udid="$(xcrun simctl list devices booted -j | /usr/bin/python3 -c \
  'import json,sys; d=json.load(sys.stdin)["devices"]; ids=[x["udid"] for v in d.values() for x in v if x["state"]=="Booted"]; print(ids[0] if ids else "")')"

if [[ -z "$TARGET" ]]; then
  if [[ -n "$booted_udid" ]]; then
    UDID="$booted_udid"
  else
    TARGET="iPhone 17"
  fi
fi

if [[ -z "${UDID:-}" ]]; then
  if [[ "$TARGET" =~ ^[0-9A-Fa-f-]{36}$ ]]; then
    UDID="$TARGET"
  else
    UDID="$(xcrun simctl list devices available -j | /usr/bin/python3 -c \
      'import json,sys; n=sys.argv[1]; d=json.load(sys.stdin)["devices"]; ids=[x["udid"] for v in d.values() for x in v if x["name"]==n and x.get("isAvailable")]; print(ids[0] if ids else "")' "$TARGET")"
  fi
fi

if [[ -z "${UDID:-}" ]]; then
  echo "Could not resolve a simulator for '${TARGET:-<booted>}'." >&2
  echo "Available iPhone simulators:" >&2
  xcrun simctl list devices available | grep -i iphone >&2
  exit 1
fi

echo "=== TurfTime iOS simulator deploy ==="
echo "Repo:   $REPO_ROOT"
echo "Target: $UDID"
echo "Config: $CONFIG | $TFM | $RID"
echo

# --- Boot it (idempotent) and bring the Simulator window up -------------------
open -a Simulator || true
xcrun simctl bootstatus "$UDID" -b >/dev/null 2>&1 || true

# --- Build for the simulator RID ----------------------------------------------
echo "Building TurfTime2 for $RID ..."
dotnet build "$PROJECT_FILE" -f "$TFM" -c "$CONFIG" -p:RuntimeIdentifier="$RID"

# Prefer device-builds .app (current .NET iOS layout); fall back to any newest .app under RID.
# macOS-safe: handle spaces in paths (e.g. "UTM Shared"); avoid xargs.
APP=""
SEARCH_ROOT="$REPO_ROOT/bin/$CONFIG/$TFM/$RID"
if [[ -d "$SEARCH_ROOT" ]]; then
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

BID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Info.plist" 2>/dev/null || echo "com.andrewestherhuysen.turftime")"
VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Info.plist" 2>/dev/null || true)"
BUILD="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$APP/Info.plist" 2>/dev/null || true)"
echo "App bundle: $APP"
echo "Bundle id : $BID"
echo "Version   : ${VER:-unknown} (${BUILD:-unknown})"

# --- Install + launch ---------------------------------------------------------
echo "Installing ..."
xcrun simctl install "$UDID" "$APP"
echo "Launching ..."
xcrun simctl launch "$UDID" "$BID"
echo "Done. TurfTime2 launched on simulator $UDID."
