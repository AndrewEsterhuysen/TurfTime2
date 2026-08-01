#!/usr/bin/env bash
set -euo pipefail

# Build signed Android Release artifacts (.aab + .apk) for Google Play.
# Automatically increments <ApplicationVersion> in TurfTime2.csproj before building
# (Play Store rejects two uploads with the same versionCode).
#
# Env overrides:
#   SKIP_VERSION_BUMP=1 — do not change ApplicationVersion (use value already in .csproj)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

echo "Building Turf Time signed Android release..."
echo "Repo: $REPO_ROOT"
echo "Project: $PROJECT_FILE"

# --- Auto-increment integer build number (ApplicationVersion / versionCode) ---
increment_application_version() {
  local project_file="$1"
  if [[ ! -f "$project_file" ]]; then
    echo "ERROR: project file not found: $project_file" >&2
    exit 1
  fi

  local current
  current="$(sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' "$project_file" | head -1)"
  if [[ -z "${current}" ]]; then
    echo "ERROR: could not find <ApplicationVersion> in $project_file" >&2
    exit 1
  fi

  local next=$((current + 1))
  if sed --version >/dev/null 2>&1; then
    sed -i "s/<ApplicationVersion>${current}<\/ApplicationVersion>/<ApplicationVersion>${next}<\/ApplicationVersion>/" "$project_file"
  else
    sed -i '' "s/<ApplicationVersion>${current}<\/ApplicationVersion>/<ApplicationVersion>${next}<\/ApplicationVersion>/" "$project_file"
  fi

  local display
  display="$(sed -n 's/.*<ApplicationDisplayVersion>\([^<]*\)<\/ApplicationDisplayVersion>.*/\1/p' "$project_file" | head -1)"
  echo "Version bump: ApplicationDisplayVersion=${display:-?}  ApplicationVersion ${current} → ${next}"
}

if [[ "${SKIP_VERSION_BUMP:-0}" != "1" ]]; then
  increment_application_version "$PROJECT_FILE"
else
  DISPLAY_V="$(sed -n 's/.*<ApplicationDisplayVersion>\([^<]*\)<\/ApplicationDisplayVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"
  BUILD_V="$(sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"
  echo "SKIP_VERSION_BUMP=1 — using ApplicationDisplayVersion=${DISPLAY_V} ApplicationVersion=${BUILD_V}"
fi

dotnet restore "$PROJECT_FILE"
dotnet publish "$PROJECT_FILE" \
  -f net10.0-android \
  -c Release \
  /p:AndroidPackageFormat=aab \
  /p:AndroidKeyStore=true

OUTPUT_DIR="$REPO_ROOT/bin/Release/net10.0-android/publish"
SIGNED_AAB="$OUTPUT_DIR/com.andrewestherhuysen.turftime-Signed.aab"
SIGNED_APK="$OUTPUT_DIR/com.andrewestherhuysen.turftime-Signed.apk"

# Fallbacks if naming differs slightly
if [ ! -f "$SIGNED_AAB" ]; then
  SIGNED_AAB="$(find "$REPO_ROOT/bin/Release/net10.0-android" -name '*-Signed.aab' 2>/dev/null | head -1 || true)"
fi
if [ ! -f "$SIGNED_APK" ]; then
  SIGNED_APK="$(find "$REPO_ROOT/bin/Release/net10.0-android" -name '*-Signed.apk' 2>/dev/null | head -1 || true)"
fi

if [ -z "${SIGNED_AAB:-}" ] || [ ! -f "$SIGNED_AAB" ]; then
  echo "ERROR: Signed AAB not found under bin/Release/net10.0-android" >&2
  find "$REPO_ROOT/bin/Release/net10.0-android" -name '*.aab' 2>/dev/null || true
  exit 1
fi

BUILD_V="$(sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"
DISPLAY_V="$(sed -n 's/.*<ApplicationDisplayVersion>\([^<]*\)<\/ApplicationDisplayVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"

echo "Build successful."
echo "Version: ${DISPLAY_V} (${BUILD_V})"
echo "AAB: $SIGNED_AAB"
ls -lh "$SIGNED_AAB"
if [ -n "${SIGNED_APK:-}" ] && [ -f "$SIGNED_APK" ]; then
  echo "APK: $SIGNED_APK"
  ls -lh "$SIGNED_APK"
  cp -f "$SIGNED_APK" "$HOME/Desktop/TurfTime2-${DISPLAY_V}-b${BUILD_V}.apk" || true
fi
cp -f "$SIGNED_AAB" "$HOME/Desktop/TurfTime2-${DISPLAY_V}-b${BUILD_V}.aab"
echo "Desktop copy: $HOME/Desktop/TurfTime2-${DISPLAY_V}-b${BUILD_V}.aab"
