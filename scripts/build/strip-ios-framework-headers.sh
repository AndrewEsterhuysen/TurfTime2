#!/usr/bin/env bash
# Remove Headers / PrivateHeaders from Frameworks inside an iOS .app bundle.
# Runtime only needs the native binaries; headers are build-time waste (~40–50 MB).
set -euo pipefail

FW="${1:-}"
if [ -z "$FW" ] || [ ! -d "$FW" ]; then
  echo "[iOS size] Skip header strip — Frameworks dir missing: ${FW:-<empty>}"
  exit 0
fi

BEFORE=$(du -sk "$FW" | awk '{print $1}')
N=$(find "$FW" -type d \( -name Headers -o -name PrivateHeaders \) 2>/dev/null | wc -l | tr -d ' ')
find "$FW" -type d \( -name Headers -o -name PrivateHeaders \) -print0 2>/dev/null | xargs -0 rm -rf
AFTER=$(du -sk "$FW" | awk '{print $1}')
echo "[iOS size] Stripped $N header dirs under $FW (${BEFORE} KB → ${AFTER} KB)"
