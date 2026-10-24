#!/usr/bin/env bash
# Measure TurfTime2 IPA / .app install-size drivers (Frameworks, splash, dead DLLs).
# Usage:
#   ./scripts/build/measure-ipa-size.sh [path/to/TurfTime2.ipa]
# Defaults to the latest Desktop TestFlight IPA, then bin/Release publish IPA.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IPA="${1:-}"

if [[ -z "$IPA" ]]; then
  IPA="$(ls -1t "$HOME"/Desktop/TurfTime2-*-TestFlight.ipa 2>/dev/null | head -1 || true)"
fi
if [[ -z "$IPA" || ! -f "$IPA" ]]; then
  IPA="$ROOT/bin/Release/net10.0-ios/ios-arm64/publish/TurfTime2.ipa"
fi
if [[ ! -f "$IPA" ]]; then
  echo "No IPA found. Pass a path: $0 /path/to/TurfTime2.ipa" >&2
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "=== IPA ==="
ls -lh "$IPA"
unzip -q -o "$IPA" -d "$TMP"
APP="$(find "$TMP/Payload" -maxdepth 1 -name '*.app' -print -quit)"
if [[ -z "$APP" || ! -d "$APP" ]]; then
  echo "No .app inside IPA Payload" >&2
  exit 1
fi

echo
echo "=== Unpacked .app ==="
du -sh "$APP"

echo
echo "=== Frameworks (top) ==="
if [[ -d "$APP/Frameworks" ]]; then
  du -sh "$APP/Frameworks"
  du -sh "$APP/Frameworks"/* 2>/dev/null | sort -hr | head -25
  HDR_COUNT="$(find "$APP/Frameworks" -type d \( -name Headers -o -name PrivateHeaders \) 2>/dev/null | wc -l | tr -d ' ')"
  HDR_SIZE="$(find "$APP/Frameworks" -type d \( -name Headers -o -name PrivateHeaders \) -exec du -sk {} + 2>/dev/null | awk '{s+=$1} END {printf "%.1fM", s/1024}')"
  echo "Header dirs: $HDR_COUNT  (~$HDR_SIZE)"
else
  echo "(no Frameworks/)"
fi

echo
echo "=== Splash / large PNGs ==="
find "$APP" -iname '*splash*' -o -iname '*.png' 2>/dev/null \
  | while read -r f; do ls -la "$f"; done \
  | sort -k5 -n -r | head -15

echo
echo "=== Dead / heavy managed (ImageSharp, QuestPDF, locales) ==="
find "$APP" \( -iname '*ImageSharp*' -o -iname '*QuestPDF*' -o -iname 'dotnet_bot*' \) -print 2>/dev/null || true
LOCALE_DIRS="$(find "$APP" -type d -regex '.*/[a-z][a-z]\(_[A-Z][A-Z]\)?\.lproj' 2>/dev/null | wc -l | tr -d ' ')"
echo "Locale .lproj-ish / satellite dirs sample count: $LOCALE_DIRS"
find "$APP" -type d -name '*.resources' 2>/dev/null | head -20 || true

echo
echo "=== Top files overall ==="
find "$APP" -type f -exec ls -la {} \; 2>/dev/null | sort -k5 -n -r | head -20
