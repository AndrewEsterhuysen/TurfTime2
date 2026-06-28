#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   ./deploy-ios-device.sh [DeviceName]
# Example:
#   ./deploy-ios-device.sh AndrewsiPhone
#
# Note:
# In this environment, deploying by UDID (:v2:udid=...) can stall.
# Deploying by the CoreDevice device name works reliably.

DEVICE_NAME="${1:-AndrewsiPhone}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cd "$ROOT_DIR"

echo "Deploying TurfTime2 to iOS device: $DEVICE_NAME"
dotnet restore TurfTime2.csproj
dotnet build TurfTime2.csproj -f net10.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64
dotnet build TurfTime2.csproj -t:Run -f net10.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64 -p:_DeviceName="$DEVICE_NAME"
