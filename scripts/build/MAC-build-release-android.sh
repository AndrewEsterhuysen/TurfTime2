#!/usr/bin/env bash
set -euo pipefail

# Build signed Android Release artifacts (.aab + .apk)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

echo "Building Turf Time signed Android release..."
echo "Repo: $REPO_ROOT"
echo "Project: $PROJECT_FILE"

dotnet clean "$PROJECT_FILE" -f net10.0-android -c Release
dotnet publish "$PROJECT_FILE" \
  -f net10.0-android \
  -c Release \
  /p:AndroidPackageFormat=aab \
  /p:AndroidKeyStore=true

OUTPUT_DIR="$REPO_ROOT/bin/Release/net10.0-android/publish"
SIGNED_AAB="$OUTPUT_DIR/com.andrewestherhuysen.turftime-Signed.aab"
SIGNED_APK="$OUTPUT_DIR/com.andrewestherhuysen.turftime-Signed.apk"

if [ ! -f "$SIGNED_AAB" ]; then
  echo "ERROR: Signed AAB not found: $SIGNED_AAB" >&2
  exit 1
fi

if [ ! -f "$SIGNED_APK" ]; then
  echo "ERROR: Signed APK not found: $SIGNED_APK" >&2
  exit 1
fi

echo "Build successful."
echo "AAB: $SIGNED_AAB"
echo "APK: $SIGNED_APK"
