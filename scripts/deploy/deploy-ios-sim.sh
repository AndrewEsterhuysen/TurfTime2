#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   ./deploy-ios-sim.sh [SimulatorNameOrUDID]
# Examples:
#   ./deploy-ios-sim.sh                 # use the already-booted sim, else "iPhone 17"
#   ./deploy-ios-sim.sh "iPhone 17 Pro"
#   ./deploy-ios-sim.sh B9111FA4-3E50-4887-BB38-D941B865389C
#
# Note:
# The combined `dotnet build -t:Run` one-liner fails on a clean tree with
# "The app must be built before the arguments ... can be computed" because no
# simulator .app exists yet. So we build first, then install + launch via simctl
# (the same build-then-run split the device deploy uses).

TARGET="${1:-}"
RID="iossimulator-arm64"
TFM="net10.0-ios"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

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
  # Treat TARGET as a UDID if it looks like one, otherwise resolve by name.
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

echo "Target simulator UDID: $UDID"

# --- Boot it (idempotent) and bring the Simulator window up -------------------
open -a Simulator || true
xcrun simctl bootstatus "$UDID" -b >/dev/null 2>&1 || true

# --- Build for the simulator RID ----------------------------------------------
echo "Building TurfTime2 for $RID ..."
dotnet build TurfTime2.csproj -f "$TFM" -c Debug -p:RuntimeIdentifier="$RID"

# --- Locate the freshly built .app and its bundle id --------------------------
APP="$(find "bin/Debug/$TFM/$RID" -type d -name '*.app' -maxdepth 4 | head -1)"
if [[ -z "$APP" ]]; then
  echo "Build succeeded but no .app bundle was found under bin/Debug/$TFM/$RID." >&2
  exit 1
fi
BID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Info.plist")"
echo "App bundle: $APP"
echo "Bundle id : $BID"

# --- Install + launch ---------------------------------------------------------
echo "Installing ..."
xcrun simctl install "$UDID" "$APP"
echo "Launching ..."
xcrun simctl launch "$UDID" "$BID"
echo "Done. TurfTime2 launched on simulator $UDID."
