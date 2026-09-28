#!/usr/bin/env bash
# Provisions lib/ (reference assemblies for building PeakRelay.Client) without
# redistributing any game or mod-framework files in the repository.
#
# Usage: scripts/fetch-libs.sh [path-to-PEAK-install]
# Requires: curl, unzip
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIB="$ROOT/lib"
PEAK_MANAGED="${1:-$ROOT/../PEAK_Data/Managed}"
BEPINEX_VERSION="5.4.23.2"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"

mkdir -p "$LIB"

echo ">> copying game reference DLLs from $PEAK_MANAGED"
for dll in Photon3Unity3D.dll PhotonRealtime.dll PhotonUnityNetworking.dll UnityEngine.dll UnityEngine.CoreModule.dll; do
    cp "$PEAK_MANAGED/$dll" "$LIB/"
done

echo ">> fetching BepInEx $BEPINEX_VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -sL -o "$TMP/bepinex.zip" "$BEPINEX_URL"
unzip -qo "$TMP/bepinex.zip" -d "$TMP/bepinex"
cp "$TMP/bepinex/BepInEx/core/BepInEx.dll" "$LIB/"
cp "$TMP/bepinex/BepInEx/core/0Harmony.dll" "$LIB/"

echo ">> lib/ ready:"
ls -1 "$LIB"
