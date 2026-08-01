#!/usr/bin/env bash
set -euo pipefail

# Build signed iOS Release IPA for App Store Connect / TestFlight.
# Automatically increments <ApplicationVersion> in TurfTime2.csproj before building
# (App Store rejects two uploads with the same build number).
#
# Env overrides:
#   SKIP_VERSION_BUMP=1   — do not change ApplicationVersion (use value already in .csproj)
#   DIST_PROFILE_SRC=…    — path to distribution .mobileprovision to install

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_FILE="$REPO_ROOT/TurfTime2.csproj"

echo "Building Turf Time signed iOS release..."
echo "Repo: $REPO_ROOT"
echo "Project: $PROJECT_FILE"

# --- Auto-increment integer build number (ApplicationVersion / CFBundleVersion) ---
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
  # macOS BSD sed requires '' after -i
  if sed --version >/dev/null 2>&1; then
    # GNU sed
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

# Ensure distribution profile is installed (optional local copy in Downloads)
DIST_PROFILE_SRC="${DIST_PROFILE_SRC:-$HOME/Downloads/TurfTimer_Distribution_Provisioning_Profile.mobileprovision}"
if [ -f "$DIST_PROFILE_SRC" ]; then
  PROF_DIR="$HOME/Library/MobileDevice/Provisioning Profiles"
  mkdir -p "$PROF_DIR"
  UUID=$(security cms -D -i "$DIST_PROFILE_SRC" 2>/dev/null | plutil -extract UUID raw - || true)
  if [ -n "${UUID:-}" ]; then
    cp -f "$DIST_PROFILE_SRC" "$PROF_DIR/${UUID}.mobileprovision"
    echo "Installed distribution profile UUID=$UUID"
  fi
fi

# Restore with RID first (clean -r ios-arm64 fails if assets lack that RID)
dotnet restore "$PROJECT_FILE" -r ios-arm64

dotnet publish "$PROJECT_FILE" \
  -f net10.0-ios \
  -c Release \
  -r ios-arm64 \
  -p:ArchiveOnBuild=true \
  -p:BuildIpa=true \
  -p:EnableCodeSigning=true \
  -p:CodesignKey="Apple Distribution: Andrew Esterhuysen (YT6V9JS4F9)" \
  -p:CodesignProvision="TurfTimer_Distribution_Provisioning_Profile"

OUTPUT_DIR="$REPO_ROOT/bin/Release/net10.0-ios/ios-arm64/publish"
IPA_FILE="$OUTPUT_DIR/TurfTime2.ipa"

if [ ! -f "$IPA_FILE" ]; then
  echo "ERROR: Signed IPA not found: $IPA_FILE" >&2
  find "$REPO_ROOT/bin/Release/net10.0-ios" -name "*.ipa" 2>/dev/null || true
  exit 1
fi

BUILD_V="$(sed -n 's/.*<ApplicationVersion>\([0-9][0-9]*\)<\/ApplicationVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"
DISPLAY_V="$(sed -n 's/.*<ApplicationDisplayVersion>\([^<]*\)<\/ApplicationDisplayVersion>.*/\1/p' "$PROJECT_FILE" | head -1)"

echo "Build successful."
echo "Version: ${DISPLAY_V} (${BUILD_V})"
echo "IPA: $IPA_FILE"
ls -lh "$IPA_FILE"
# Convenience copy with version in the name
DESKTOP_IPA="$HOME/Desktop/TurfTime2-${DISPLAY_V}-b${BUILD_V}-TestFlight.ipa"
cp -f "$IPA_FILE" "$DESKTOP_IPA"
echo "Desktop copy: $DESKTOP_IPA"
echo "Upload with Transporter, or:"
echo "  xcrun altool --upload-app -f \"$IPA_FILE\" -t ios -u APPLE_ID -p @keychain:AC_PASSWORD"
