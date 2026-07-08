#!/usr/bin/env bash
set -euo pipefail

# Build Release AAB for Google Play Store (macOS shell script)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "Building Turf Time Android Release AAB..."
echo "Cleaning previous builds..."
dotnet clean -c Release

echo "Publishing Android App Bundle..."
dotnet publish -f net10.0-android -c Release /p:AndroidPackageFormat=aab

echo "Build successful. Output: bin/Release/net10.0-android/publish/"

OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net10.0-android/publish"
if [ -d "$OUTPUT_DIR" ]; then
  # Open Finder at output folder on macOS
  open "$OUTPUT_DIR"
fi

