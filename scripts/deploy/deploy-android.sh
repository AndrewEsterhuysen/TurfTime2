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

# Build the APK ABI(s) needed for the selected targets.
# Physical phones are almost always arm64; emulators may be arm64 (Apple Silicon)
# or x86_64 (Intel / some AVDs) — probe each device's primary ABI.
need_arm64=0
need_x64=0
device_rid_for() {
  local device="$1"
  local abi
  abi="$(adb -s "$device" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\r' || true)"
  case "$abi" in
    x86_64|x86) echo "android-x64" ;;
    *)          echo "android-arm64" ;;  # arm64-v8a, armeabi-v7a, unknown → arm64
  esac
}
for device in "${TARGET_DEVICES[@]}"; do
  rid="$(device_rid_for "$device")"
  if [[ "$rid" == "android-x64" ]]; then
    need_x64=1
  else
    need_arm64=1
  fi
  echo "Device $device ABI → $rid"
done

build_and_find_apk() {
  local rid="$1"
  echo "Building Debug APK ($rid)..." >&2
  dotnet build "$PROJECT_FILE" -f "$TFM" -c "$CONFIG" -p:RuntimeIdentifier="$rid" >&2

  local search_root="$REPO_ROOT/bin/$CONFIG/$TFM"
  local found=""
  # Prefer RID-specific output, then TFM root signed APK.
  if [[ -f "$search_root/$rid/${PACKAGE_ID}-Signed.apk" ]]; then
    found="$search_root/$rid/${PACKAGE_ID}-Signed.apk"
  elif [[ -f "$search_root/${PACKAGE_ID}-Signed.apk" ]]; then
    found="$search_root/${PACKAGE_ID}-Signed.apk"
  elif [[ -f "$search_root/$rid/${PACKAGE_ID}.apk" ]]; then
    found="$search_root/$rid/${PACKAGE_ID}.apk"
  else
    found="$(find "$search_root" -name '*-Signed.apk' -type f 2>/dev/null | head -1 || true)"
  fi

  if [[ -z "$found" || ! -f "$found" ]]; then
    echo "ERROR: Signed Debug APK not found under $search_root (rid=$rid)" >&2
    find "$search_root" -name '*.apk' 2>/dev/null | head -20 >&2 || true
    return 1
  fi

  echo "APK ($rid): $found ($(du -h "$found" | awk '{print $1}'))" >&2
  # Only the path goes to stdout (for capture by callers).
  printf '%s\n' "$found"
}

APK_ARM64=""
APK_X64=""
if [[ "$need_arm64" -eq 1 ]]; then
  APK_ARM64="$(build_and_find_apk android-arm64)"
fi
if [[ "$need_x64" -eq 1 ]]; then
  APK_X64="$(build_and_find_apk android-x64)"
fi
echo

for device in "${TARGET_DEVICES[@]}"; do
  kind="Phone"
  [[ "$device" == emulator-* ]] && kind="Emulator"
  rid="$(device_rid_for "$device")"
  if [[ "$rid" == "android-x64" ]]; then
    apk="$APK_X64"
  else
    apk="$APK_ARM64"
  fi
  if [[ -z "$apk" || ! -f "$apk" ]]; then
    echo "ERROR: No APK built for $rid (device $device)" >&2
    exit 1
  fi
  echo "Deploying to $kind ($device) with $rid ..."

  # Uninstall so Debug can replace release/store signature.
  adb -s "$device" uninstall "$PACKAGE_ID" >/dev/null 2>&1 || true

  if ! adb -s "$device" install -r "$apk"; then
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
