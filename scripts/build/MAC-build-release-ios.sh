#!/usr/bin/env bash
set -euo pipefail

# Build signed iOS Release IPA for App Store Connect

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

echo "Building Turf Time signed iOS release..."
echo "Repo: $REPO_ROOT"
echo "Project: $PROJECT_FILE"

dotnet clean "$PROJECT_FILE" -f net10.0-ios -r ios-arm64 -c Release
dotnet publish "$PROJECT_FILE" \
  -f net10.0-ios \
  -c Release \
  -r ios-arm64 \
  -p:ArchiveOnBuild=true \
  -p:BuildIpa=true \
  -p:EnableCodeSigning=true

OUTPUT_DIR="$REPO_ROOT/bin/Release/net10.0-ios/ios-arm64/publish"
IPA_FILE="$OUTPUT_DIR/TurfTime2.ipa"

if [ ! -f "$IPA_FILE" ]; then
  echo "ERROR: Signed IPA not found: $IPA_FILE" >&2
  exit 1
fi

echo "Build successful."
echo "IPA: $IPA_FILE"
