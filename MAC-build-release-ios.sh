#!/usr/bin/env bash
set -euo pipefail

# Build Release iOS archive/IPA for App Store Connect (macOS shell script)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "Building Turf Time iOS Release archive..."
echo "Cleaning previous builds..."
dotnet clean

echo "Publishing iOS Release (Archive + IPA)..."
dotnet publish -f net10.0-ios -c Release -r ios-arm64 -p:ArchiveOnBuild=true -p:BuildIpa=true

echo "Build successful. Expected output: bin/Release/net10.0-ios/ios-arm64/publish/"

OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net10.0-ios/ios-arm64/publish"
if [ -d "$OUTPUT_DIR" ]; then
  # Open Finder at output folder on macOS
  open "$OUTPUT_DIR"
fi

