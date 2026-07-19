#!/usr/bin/env bash
set -euo pipefail

# Deploy Cloud Functions for Turf Time (project: turf-timer).
# Prerequisites:
#   - Firebase CLI installed (firebase --version)
#   - firebase login  (or GOOGLE_APPLICATION_CREDENTIALS + service account)
#   - Blaze plan enabled on the Firebase project
#
# Usage:
#   ./scripts/deploy/deploy-functions.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="${1:-turf-timer}"

export PATH="${HOME}/.npm-global/bin:${PATH}"

if ! command -v firebase >/dev/null 2>&1; then
  echo "ERROR: firebase CLI not found. Install with:" >&2
  echo "  npm install -g firebase-tools" >&2
  exit 1
fi

cd "$REPO_ROOT"

echo "=== Deploy Turf Time Cloud Functions ==="
echo "Repo:    $REPO_ROOT"
echo "Project: $PROJECT"
echo "CLI:     $(firebase --version)"
echo

if [[ ! -d "$REPO_ROOT/functions/node_modules" ]]; then
  echo "Installing function dependencies..."
  (cd "$REPO_ROOT/functions" && npm install)
fi

firebase use "$PROJECT"
firebase deploy --only functions --project "$PROJECT"

echo
echo "=== Done. List functions: firebase functions:list --project $PROJECT ==="
echo "=== Logs: firebase functions:log --only sendChatNotification --project $PROJECT ==="
