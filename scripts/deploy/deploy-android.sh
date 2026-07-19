#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   ./scripts/deploy/deploy-android.sh [phone|emulator|both|SERIAL]
# Examples:
#   ./scripts/deploy/deploy-android.sh              # all physical phones
#   ./scripts/deploy/deploy-android.sh phone
#   ./scripts/deploy/deploy-android.sh ZY227KSJL3
#
# Always uninstalls first so Debug can replace a store/release-signed install.
# Compatible with macOS system bash 3.2 (no mapfile).

TARGET="${1:-phone}"
PACKAGE_ID="com.andrewestherhuysen.turftime"
TFM="net10.0-android"
CONFIG="Debug"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

cd "$REPO_ROOT"

if [[ ! -f "$PROJECT_FILE" ]]; then
  echo "ERROR: Project not found: $PROJECT_FILE" >&2
  exit 1
fi

if ! command -v adb >/dev/null 2>&1; then
  echo "ERROR: adb not found on PATH." >&2
  exit 1
fi

ALL_DEVICES=()
while IFS= read -r line; do
  [[ -n "$line" ]] && ALL_DEVICES+=("$line")
done < <(adb devices | awk '/\tdevice$/{print $1}')

if [[ ${#ALL_DEVICES[@]} -eq 0 ]]; then
  echo "ERROR: No adb devices in 'device' state." >&2
  adb devices -l >&2 || true
  exit 1
fi

PHONES=()
EMULATORS=()
for d in "${ALL_DEVICES[@]}"; do
  if [[ "$d" == emulator-* ]]; then
    EMULATORS+=("$d")
  else
    PHONES+=("$d")
  fi
done

TARGET_DEVICES=()
case "$TARGET" in
  phone)
    TARGET_DEVICES=("${PHONES[@]+"${PHONES[@]}"}")
    if [[ ${#TARGET_DEVICES[@]} -eq 0 ]]; then
      echo "ERROR: No physical Android devices connected." >&2
      exit 1
    fi
    ;;
  emulator)
    TARGET_DEVICES=("${EMULATORS[@]+"${EMULATORS[@]}"}")
    if [[ ${#TARGET_DEVICES[@]} -eq 0 ]]; then
      echo "ERROR: No Android emulators running." >&2
      exit 1
    fi
    ;;
  both)
    TARGET_DEVICES=("${ALL_DEVICES[@]}")
    ;;
  *)
    if adb -s "$TARGET" get-state 2>/dev/null | grep -q device; then
      TARGET_DEVICES=("$TARGET")
    else
      echo "ERROR: Unknown target '$TARGET' (use phone|emulator|both|SERIAL)." >&2
      exit 1
    fi
    ;;
esac

echo "=== TurfTime Android deploy ==="
echo "Repo:    $REPO_ROOT"
echo "Project: $PROJECT_FILE"
echo "Config:  $CONFIG | $TFM"
echo "Targets: ${TARGET_DEVICES[*]}"
echo

echo "Building Debug APK..."
dotnet build "$PROJECT_FILE" -f "$TFM" -c "$CONFIG"

# Prefer the signed fat APK at the TFM root (not nested android-arm64 copies).
APK_DIR="$REPO_ROOT/bin/$CONFIG/$TFM"
APK=""
if [[ -f "$APK_DIR/${PACKAGE_ID}-Signed.apk" ]]; then
  APK="$APK_DIR/${PACKAGE_ID}-Signed.apk"
elif [[ -f "$APK_DIR/${PACKAGE_ID}.apk" ]]; then
  APK="$APK_DIR/${PACKAGE_ID}.apk"
else
  APK="$(find "$APK_DIR" -maxdepth 1 -name '*-Signed.apk' -type f 2>/dev/null | head -1 || true)"
fi

if [[ -z "$APK" || ! -f "$APK" ]]; then
  echo "ERROR: Signed Debug APK not found under $APK_DIR" >&2
  find "$APK_DIR" -name '*.apk' 2>/dev/null | head -20 >&2 || true
  exit 1
fi

echo "APK: $APK ($(du -h "$APK" | awk '{print $1}'))"
echo

for device in "${TARGET_DEVICES[@]}"; do
  kind="Phone"
  [[ "$device" == emulator-* ]] && kind="Emulator"
  echo "Deploying to $kind ($device)..."

  # Uninstall so Debug can replace release/store signature.
  adb -s "$device" uninstall "$PACKAGE_ID" >/dev/null 2>&1 || true

  if ! adb -s "$device" install -r "$APK"; then
    echo "  ✗ install failed on $device" >&2
    exit 1
  fi
  echo "  ✓ installed"

  # Resolve launcher activity (crc* can change between builds).
  ACTIVITY="$(adb -s "$device" shell cmd package resolve-activity --brief "$PACKAGE_ID" 2>/dev/null | tail -1 | tr -d '\r' || true)"
  if [[ -n "$ACTIVITY" && "$ACTIVITY" == */* ]]; then
    echo "  Launching $ACTIVITY ..."
    adb -s "$device" shell am start -n "$ACTIVITY" >/dev/null || true
  else
    echo "  Launching via monkey ..."
    adb -s "$device" shell monkey -p "$PACKAGE_ID" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 || true
  fi
  echo
done

echo "=== Android deployment complete ==="
